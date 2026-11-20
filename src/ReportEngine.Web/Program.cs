using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportBuilder.Core;
using ReportBuilder.Core.Data;
using ReportBuilder.Core.Pipeline;
using ReportBuilder.Core.Security;
using ReportBuilder.Core.Views;
using ReportEngine.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Deployment profile: set Profile=Ollama or Profile=Azure (in appsettings.json, env var, or --Profile=Azure)
var profile = builder.Configuration["Profile"];
if (!string.IsNullOrWhiteSpace(profile))
    builder.Configuration.AddJsonFile($"appsettings.{profile}.json", optional: true, reloadOnChange: true);

// Layer optional tenants.json (same format as ERP Tenants config) and user-secrets
builder.Configuration.AddJsonFile("tenants.json", optional: true, reloadOnChange: true);
builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.AddReportBuilder(builder.Configuration);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "ReportEngine.Auth";
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

static string? CurrentUserName(HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true ? ctx.User.Identity.Name : null;

async Task<ErpUserSession?> ResolveActiveSessionAsync(HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, string? companyCode, CancellationToken ct)
{
    var user = CurrentUserName(ctx);
    if (string.IsNullOrWhiteSpace(user)) return null;

    string? companyConn = null;
    if (!string.IsNullOrWhiteSpace(companyCode) && !companyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase))
    {
        var baseSession = await sec.ResolveUserAsync(user, null, ct);
        var match = baseSession?.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(companyCode, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            companyConn = await dsf.ResolveConnectionStringAsync(match, ct);
    }

    return await sec.ResolveUserAsync(user, companyConn, ct);
}

var api = app.MapGroup("/api");

// ---- Auth & User Switcher ----
api.MapGet("/auth/uat-users", async (IConfiguration cfg, ErpSecurityProvider sec, CancellationToken ct) =>
{
    if (!cfg.GetValue("UatMode", false)) return Results.Forbid();
    return Results.Ok(await sec.ListUsersAsync(100, ct));
});

api.MapPost("/auth/login", async (LoginRequest req, HttpContext ctx, IConfiguration cfg, ErpSecurityProvider sec, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.UserName))
        return Results.BadRequest(new { error = "Username is required." });

    var session = await sec.ResolveUserAsync(req.UserName.Trim(), null, ct);
    if (session is null || !session.IsActive)
        return Results.Unauthorized();

    var uatMode = cfg.GetValue("UatMode", false);
    if (!uatMode && string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Password is required." });

    var claims = new List<Claim> { new(ClaimTypes.Name, session.UserName) };
    var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Ok(session);
});

api.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { ok = true });
});

api.MapGet("/auth/me", async (string? company, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, company, ct);
    return session is null ? Results.Unauthorized() : Results.Ok(session);
});

// ---- Dynamic Schema & Field Discovery (scoped to selected company DB & user RBAC) ----
api.MapGet("/schema/areas", async (string company, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, company, ct);
    if (session is null) return Results.Unauthorized();

    if (company.Equals("ALL", StringComparison.OrdinalIgnoreCase))
    {
        var first = session.AllowedCompanies.FirstOrDefault();
        if (first is null) return Results.Ok(Array.Empty<ErpAreaViewMeta>());
        var views = await dsf.GetPermittedViewsAsync(session, first, ct);
        return Results.Ok(views);
    }

    var comp = session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(company, StringComparison.OrdinalIgnoreCase));
    if (comp is null) return Results.Forbid();
    return Results.Ok(await dsf.GetPermittedViewsAsync(session, comp, ct));
});

// ---- Zero-DB-Access AI Query Planner ----
api.MapPost("/reports/ai-configure", async (AiConfigureInput input, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, AiReportAssistant ai, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, input.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();

    var target = input.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase)
        ? session.AllowedCompanies.FirstOrDefault()
        : session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(input.CompanyCode, StringComparison.OrdinalIgnoreCase));

    if (target is null) return Results.Forbid();

    var permittedViews = await dsf.GetPermittedViewsAsync(session, target, ct);
    try
    {
        var res = await ai.ConfigureAsync(input.Question, input.CompanyCode, permittedViews, ct);
        return Results.Ok(res);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"AI planner could not parse response: {ex.Message}" });
    }
});

// ---- Deterministic C# SQL Compile + Execute + Audit ----
api.MapPost("/reports/run", async (DynamicReportRequest req, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, IAuditStore audit, IOptions<PolicyOptions> policyOpts, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, req.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();

    var refCompany = req.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase)
        ? session.AllowedCompanies.FirstOrDefault()
        : session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(req.CompanyCode, StringComparison.OrdinalIgnoreCase));

    if (refCompany is null) return Results.Forbid();

    var views = await dsf.GetPermittedViewsAsync(session, refCompany, ct);
    var viewMeta = views.FirstOrDefault(v => v.ViewName.Equals(req.ViewName, StringComparison.OrdinalIgnoreCase));
    if (viewMeta is null)
        return Results.BadRequest(new { error = $"View '{req.ViewName}' is not available or not permitted." });

    var compiled = DynamicReportCompiler.Compile(req, session, viewMeta, DateTime.UtcNow, policyOpts.Value.DefaultMaxRows);
    if (!compiled.Ok || compiled.Query is null)
        return Results.BadRequest(new { errors = compiled.Errors });

    QueryResult result;
    try
    {
        result = await dsf.ExecuteCompiledAsync(session, req.CompanyCode, compiled.Query, viewMeta.Columns, ct);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Npgsql.PostgresException ex)
    {
        return Results.BadRequest(new { error = $"Database query error ({ex.SqlState}): {ex.MessageText}" });
    }

    var runId = await audit.AppendAsync(new AuditEntry(
        session.UserName,
        $"[Dynamic:{req.ViewName}] {JsonSerializer.Serialize(req)}",
        "Answered",
        null,
        compiled.Query.Sql,
        result.Rows.Count,
        "[]",
        (int)sw.ElapsedMilliseconds), ct);

    return Results.Ok(new
    {
        runId,
        viewName = viewMeta.ViewName,
        displayName = viewMeta.DisplayName,
        companyCode = req.CompanyCode,
        sql = compiled.Query.Sql,
        chartType = req.ChartType,
        result
    });
});

// ---- Deterministic GL / Trial Balance Reconciliation Route ----
api.MapPost("/reconcile", async (ReconcileInput input, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, Presenter presenter, IAuditStore audit, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, input.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();

    var comp = session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(input.CompanyCode, StringComparison.OrdinalIgnoreCase));
    if (comp is null) return Results.Forbid();

    if (!session.AllowsView("control_totals_v") || !session.AllowsView("manual_postings_v") || !session.AllowsView("trial_balance_v"))
        return Results.Forbid();

    var cs = await dsf.ResolveConnectionStringAsync(comp, ct);
    if (string.IsNullOrWhiteSpace(cs))
        return Results.BadRequest(new { error = $"No database connection configured for {comp.Code}." });

    var ds = dsf.GetOrCreateDataSource(cs);
    var src = new CompanyReconciliationSource(ds);
    var reconciler = new Reconciler(src);
    var (tbNet, areas) = await reconciler.ExplainAsync(comp.Code, input.Areas ?? [], ct);

    var summary = await presenter.SummarizeReconciliationAsync(input.Question ?? "Reconcile ledger vs trial balance", comp.Code, tbNet, areas, ct);
    var runId = await audit.AppendAsync(new AuditEntry(
        session.UserName,
        input.Question ?? "Reconciliation check",
        "Reconciled",
        null,
        null,
        areas.Count,
        "[]",
        (int)sw.ElapsedMilliseconds), ct);

    return Results.Ok(new { runId, companyCode = comp.Code, trialBalanceNet = tbNet, areas, summary });
});

// ---- Multi-Format Exports (CSV, Excel .xlsx, PDF, JSON) ----
api.MapPost("/reports/export/{format}", async (string format, DynamicReportRequest req, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, IOptions<PolicyOptions> policyOpts, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, req.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();

    var refCompany = req.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase)
        ? session.AllowedCompanies.FirstOrDefault()
        : session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(req.CompanyCode, StringComparison.OrdinalIgnoreCase));
    if (refCompany is null) return Results.Forbid();

    var views = await dsf.GetPermittedViewsAsync(session, refCompany, ct);
    var viewMeta = views.FirstOrDefault(v => v.ViewName.Equals(req.ViewName, StringComparison.OrdinalIgnoreCase));
    if (viewMeta is null) return Results.BadRequest(new { error = $"View '{req.ViewName}' not found or not permitted." });

    var compiled = DynamicReportCompiler.Compile(req, session, viewMeta, DateTime.UtcNow, policyOpts.Value.DefaultMaxRows);
    if (!compiled.Ok || compiled.Query is null) return Results.BadRequest(new { errors = compiled.Errors });

    QueryResult result;
    try
    {
        result = await dsf.ExecuteCompiledAsync(session, req.CompanyCode, compiled.Query, viewMeta.Columns, ct);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Npgsql.PostgresException ex)
    {
        return Results.BadRequest(new { error = $"Database query error ({ex.SqlState}): {ex.MessageText}" });
    }
    var title = $"{viewMeta.DisplayName} ({req.CompanyCode})";
    var slug = $"{viewMeta.ViewName}_{req.CompanyCode}_{DateTime.UtcNow:yyyyMMddHHmm}";

    return format.ToLowerInvariant() switch
    {
        "csv" => Results.File(ReportExporters.ToCsv(result), "text/csv", $"{slug}.csv"),
        "xlsx" or "excel" => Results.File(ReportExporters.ToExcel(result, title, req.CompanyCode),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{slug}.xlsx"),
        "pdf" => Results.File(ReportExporters.ToPdf(result, title, req.CompanyCode), "application/pdf", $"{slug}.pdf"),
        "json" => Results.File(ReportExporters.ToJson(result, title, req.CompanyCode), "application/json", $"{slug}.json"),
        _ => Results.BadRequest(new { error = "Supported formats: csv, xlsx, pdf, json." })
    };
});

// ---- Admin RBAC & Company-DB Mapping Endpoints ----
api.MapGet("/admin/rbac-map", async (HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();
    return Results.Ok(await sec.GetRbacRulesAsync(ct));
});

api.MapPost("/admin/rbac-map", async (RbacRule rule, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();
    var id = await sec.UpsertRbacRuleAsync(rule, ct);
    return Results.Ok(new { id });
});

api.MapDelete("/admin/rbac-map/{id:int}", async (int id, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();
    await sec.DeleteRbacRuleAsync(id, ct);
    return Results.NoContent();
});

api.MapGet("/admin/company-db-map", async (HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();
    return Results.Ok(await dsf.GetDatabaseMappingsAsync(ct));
});

api.MapPost("/admin/company-db-map", async (CompanyDatabaseMapping map, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();
    await dsf.UpsertDatabaseMappingAsync(map, ct);
    return Results.Ok(new { ok = true });
});

// ---- AI-Suggested View Drafts + Admin 1-Click Approve & Create ----
api.MapPost("/views/suggest-draft", async (AiConfigureInput input, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, AiReportAssistant ai, IViewStore viewStore, IOptions<DesignerOptions> designerOpts, IOptions<PolicyOptions> policyOpts, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, input.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();
    var comp = session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(input.CompanyCode, StringComparison.OrdinalIgnoreCase));
    if (comp is null) return Results.Forbid();

    var sources = await dsf.GetSourceTablesAsync(comp, designerOpts.Value.Schemas, ct);
    if (sources.Count == 0) return Results.BadRequest(new { error = "No source tables available for view design." });

    var existing = (await dsf.GetPermittedViewsAsync(session, comp, ct)).Select(v => v.ViewName).ToArray();
    var (req, areaName) = await ai.SuggestViewDraftAsync(input.Question, sources, ct);
    var built = ViewBuilder.Build(req, sources, policyOpts.Value.BlockedColumns, existing);
    if (!built.Ok)
        return Results.BadRequest(new { error = string.Join("; ", built.Issues) });

    var defJson = JsonSerializer.Serialize(new { Request = req, AreaName = areaName, CompanyCode = comp.Code });
    var draftId = await viewStore.SaveDraftAsync(req.Name, req.Description, defJson, built.Sql, session.UserName, ct);
    return Results.Ok(new { draftId, viewName = req.Name, areaName, sql = built.Sql });
});

api.MapGet("/admin/view-drafts", async (HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, IViewStore viewStore, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();
    return Results.Ok(await viewStore.GetDraftsAsync(ct));
});

api.MapPost("/admin/view-drafts/{id:int}/approve", async (int id, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, IViewStore viewStore, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();

    var drafts = await viewStore.GetDraftsAsync(ct);
    var draft = drafts.FirstOrDefault(d => d.DraftId == id);
    if (draft is null) return Results.NotFound(new { error = "Draft not found." });

    using var doc = JsonDocument.Parse(draft.DefinitionJson);
    var areaName = doc.RootElement.TryGetProperty("AreaName", out var an) ? an.GetString() ?? "General" : "General";
    var companyCode = doc.RootElement.TryGetProperty("CompanyCode", out var cc) ? cc.GetString() ?? "" : "";
    var comp = session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(companyCode, StringComparison.OrdinalIgnoreCase))
        ?? session.AllowedCompanies.FirstOrDefault();
    if (comp is null) return Results.BadRequest(new { error = "No target company available." });

    await dsf.CreateApprovedViewAsync(comp, draft.Name, draft.SqlText, areaName, draft.Description, "", ct);
    await dsf.MarkViewDraftReviewedAsync(id, true, session.UserName, "Approved via ReportEngine Admin", ct);
    return Results.Ok(new { ok = true, viewName = draft.Name, companyCode = comp.Code });
});

api.MapPost("/admin/view-drafts/{id:int}/reject", async (int id, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();
    await dsf.MarkViewDraftReviewedAsync(id, false, session.UserName, "Rejected via ReportEngine Admin", ct);
    return Results.Ok(new { ok = true });
});

app.MapGet("/healthz", () => Results.Ok("ok"));
app.Run();

public sealed record LoginRequest(string UserName, string? Password);
public sealed record AiConfigureInput(string Question, string CompanyCode);
public sealed record ReconcileInput(string CompanyCode, string? Question, string[]? Areas);

internal sealed class CompanyReconciliationSource(NpgsqlDataSource ds) : IReconciliationSource
{
    public async Task<IReadOnlyList<ControlTotal>> GetControlTotalsAsync(string company, CancellationToken ct)
    {
        var list = new List<ControlTotal>();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT area, gl_balance, subledger_balance, difference FROM rpt.control_totals_v WHERE company_code = @c OR company_code = 'ALL'", conn);
        cmd.Parameters.AddWithValue("c", company);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ControlTotal(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3)));
        return list;
    }

    public async Task<decimal> GetTrialBalanceNetAsync(string company, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(SUM(closing_net), 0) FROM rpt.trial_balance_v WHERE company_code = @c OR company_code = 'ALL'", conn);
        cmd.Parameters.AddWithValue("c", company);
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<ManualPosting>> GetManualPostingsAsync(string company, IReadOnlyCollection<string> accounts, CancellationToken ct)
    {
        var list = new List<ManualPosting>();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT journal_no, posting_date, account_code, net_amount, description FROM rpt.manual_postings_v WHERE (company_code = @c OR company_code = 'ALL') ORDER BY posting_date DESC LIMIT 25", conn);
        cmd.Parameters.AddWithValue("c", company);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ManualPosting(r.GetString(0), r.GetDateTime(1), r.GetString(2), r.GetDecimal(3), r.GetString(4)));
        return list;
    }
}
