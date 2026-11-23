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
builder.Services.AddSingleton<GroupReportPackService>();
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
        return Results.Ok(await dsf.GetPermittedViewsAcrossCompaniesAsync(session, ct));

    var comp = session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(company, StringComparison.OrdinalIgnoreCase));
    if (comp is null) return Results.Forbid();
    return Results.Ok(await dsf.GetPermittedViewsAsync(session, comp, ct));
});

// ---- Zero-DB-Access AI Query Planner ----
api.MapPost("/reports/ai-configure", async (AiConfigureInput input, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, AiReportAssistant ai, IAuditStore audit, IOptions<PolicyOptions> policyOpts, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, input.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();

    var isAll = input.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase);
    var target = isAll
        ? session.AllowedCompanies.FirstOrDefault()
        : session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(input.CompanyCode, StringComparison.OrdinalIgnoreCase));

    if (target is null) return Results.Forbid();

    var permittedViews = isAll
        ? await dsf.GetPermittedViewsAcrossCompaniesAsync(session, ct)
        : await dsf.GetPermittedViewsAsync(session, target, ct);
    try
    {
        var res = await ai.ConfigureAsync(
            input.Question,
            input.CompanyCode,
            permittedViews,
            session.BlockedColumns,
            policyOpts.Value.DefaultMaxRows,
            forceRefresh: input.ForceRefresh,
            ct: ct);

        if (res.IsReconcile && (!session.AllowsView("control_totals_v") || !session.AllowsView("manual_postings_v") || !session.AllowsView("trial_balance_v")))
        {
            res = new AiConfigureResult
            {
                IsReconcile = false,
                GuardrailBlocked = true,
                GuardrailCategory = "RbacDenied",
                MissingDataNote = "You do not have RBAC permission to run GL/Trial Balance reconciliation.",
                Explanation = "GL/Trial Balance reconciliation is not permitted for your role."
            };
        }
        else if (res.Request is not null)
        {
            var matchedView = permittedViews.FirstOrDefault(v =>
                v.ViewName.Equals(res.Request.ViewName, StringComparison.OrdinalIgnoreCase));
            if (matchedView is null)
            {
                res = new AiConfigureResult
                {
                    GuardrailBlocked = true,
                    GuardrailCategory = "RbacDenied",
                    MissingDataNote = $"Guardrail blocked unpermitted view '{res.Request.ViewName}'.",
                    Explanation = "Matched view is not permitted."
                };
            }
            else
            {
                var compiled = DynamicReportCompiler.Compile(
                    res.Request, session, matchedView, DateTime.UtcNow, policyOpts.Value.DefaultMaxRows);
                if (!compiled.Ok)
                {
                    var errMsg = "Guardrail rejected the generated report configuration: " + string.Join("; ", compiled.Errors);
                    res = new AiConfigureResult
                    {
                        GuardrailBlocked = true,
                        GuardrailCategory = "CompileGuardrail",
                        MissingDataNote = errMsg,
                        Explanation = errMsg
                    };
                }
            }
        }

        if (res.IsOutOfScope || res.GuardrailBlocked)
        {
            await audit.AppendAsync(new AuditEntry(
                session.UserName,
                $"[AI-Configure:{input.CompanyCode}] {input.Question}",
                res.IsOutOfScope ? "OutOfScope" : "GuardrailBlocked",
                null,
                null,
                0,
                JsonSerializer.Serialize(new[] { new { Code = res.GuardrailCategory ?? "Guardrail", Message = res.MissingDataNote ?? res.Explanation } }),
                (int)sw.ElapsedMilliseconds), ct);
        }

        return Results.Ok(res);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"AI planner could not parse response: {ex.Message}" });
    }
});

// ---- Deterministic C# SQL Compile + Execute + Audit (0 LLM Tokens) ----
static async Task<IResult> ExecuteReportRequestAsync(
    DynamicReportRequest req,
    ErpUserSession session,
    CompanyDataSourceFactory dsf,
    IAuditStore audit,
    int maxRows,
    string auditPrefix,
    CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    var isAll = req.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase);
    var refCompany = isAll
        ? session.AllowedCompanies.FirstOrDefault()
        : session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(req.CompanyCode, StringComparison.OrdinalIgnoreCase));

    if (refCompany is null) return Results.Forbid();

    var views = isAll
        ? await dsf.GetPermittedViewsAcrossCompaniesAsync(session, ct)
        : await dsf.GetPermittedViewsAsync(session, refCompany, ct);
    var viewMeta = views.FirstOrDefault(v => v.ViewName.Equals(req.ViewName, StringComparison.OrdinalIgnoreCase));
    if (viewMeta is null)
        return Results.BadRequest(new { error = $"View '{req.ViewName}' is not available or not permitted." });

    var compiled = DynamicReportCompiler.Compile(req, session, viewMeta, DateTime.UtcNow, maxRows);
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
        $"{auditPrefix} {JsonSerializer.Serialize(req)}",
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
}

api.MapPost("/reports/run", async (DynamicReportRequest req, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, IAuditStore audit, IOptions<PolicyOptions> policyOpts, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, req.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();
    return await ExecuteReportRequestAsync(req, session, dsf, audit, policyOpts.Value.DefaultMaxRows, $"[Dynamic:{req.ViewName}]", ct);
});

// ---- Saved Reports / Favorites (0-Token Deterministic Replay) ----
api.MapGet("/reports/saved", async (HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, ISavedReportStore savedStore, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    return Results.Ok(await savedStore.ListVisibleAsync(session, ct));
});

api.MapPost("/reports/saved", async (SaveReportInput input, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, ISavedReportStore savedStore, IOptions<PolicyOptions> policyOpts, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, input.Request?.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 160)
        return Results.BadRequest(new { error = "Report name is required (max 160 characters)." });
    if (input.Request is null || string.IsNullOrWhiteSpace(input.Request.ViewName))
        return Results.BadRequest(new { error = "A valid report configuration is required." });

    var req = input.Request;
    var isAll = req.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase);
    var refCompany = isAll
        ? session.AllowedCompanies.FirstOrDefault()
        : session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(req.CompanyCode, StringComparison.OrdinalIgnoreCase));
    if (refCompany is null) return Results.Forbid();

    var views = isAll
        ? await dsf.GetPermittedViewsAcrossCompaniesAsync(session, ct)
        : await dsf.GetPermittedViewsAsync(session, refCompany, ct);
    var viewMeta = views.FirstOrDefault(v => v.ViewName.Equals(req.ViewName, StringComparison.OrdinalIgnoreCase));
    if (viewMeta is null)
        return Results.BadRequest(new { error = $"View '{req.ViewName}' is not available or not permitted." });

    var compiled = DynamicReportCompiler.Compile(req, session, viewMeta, DateTime.UtcNow, policyOpts.Value.DefaultMaxRows);
    if (!compiled.Ok)
        return Results.BadRequest(new { error = "Cannot save invalid report: " + string.Join("; ", compiled.Errors) });

    var saved = await savedStore.SaveAsync(new CreateSavedReportInput(
        input.Name, input.Description, input.IsShared, req.CompanyCode, viewMeta.ViewName, input.PromptText, req),
        session.UserName,
        ct);
    return Results.Ok(saved);
});

api.MapPost("/reports/saved/{id:int}/run", async (int id, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, ISavedReportStore savedStore, IAuditStore audit, IOptions<PolicyOptions> policyOpts, CancellationToken ct) =>
{
    var baseSession = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (baseSession is null) return Results.Unauthorized();

    var saved = await savedStore.GetByIdAsync(id, ct);
    if (saved is null) return Results.NotFound(new { error = "Saved report not found." });
    if (!saved.IsShared && !saved.OwnerUser.Equals(baseSession.UserName, StringComparison.OrdinalIgnoreCase))
        return Results.Forbid();

    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, saved.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();

    var req = JsonSerializer.Deserialize<DynamicReportRequest>(saved.RequestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (req is null) return Results.BadRequest(new { error = "Saved report specification is corrupted." });

    var runResult = await ExecuteReportRequestAsync(req, session, dsf, audit, policyOpts.Value.DefaultMaxRows, $"[SavedReport:#{id}:{saved.Name}]", ct);
    await savedStore.RecordRunAsync(id, ct);
    return runResult;
});

api.MapDelete("/reports/saved/{id:int}", async (int id, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, ISavedReportStore savedStore, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();

    var saved = await savedStore.GetByIdAsync(id, ct);
    if (saved is null) return Results.NotFound();
    if (!session.IsAdmin && !saved.OwnerUser.Equals(session.UserName, StringComparison.OrdinalIgnoreCase))
        return Results.Forbid();

    await savedStore.DeleteAsync(id, session.UserName, session.IsAdmin, ct);
    return Results.NoContent();
});

// ---- Deterministic GL / Trial Balance Reconciliation Route ----
api.MapPost("/reconcile", async (ReconcileInput input, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, Presenter presenter, IAuditStore audit, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, input.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();

    if (!string.IsNullOrWhiteSpace(input.Question))
    {
        var decision = ScopeGuardrail.InspectInput(input.Question, session.BlockedColumns);
        if (!decision.Allowed)
        {
            await audit.AppendAsync(new AuditEntry(
                session.UserName,
                $"[Reconcile:{input.CompanyCode}] {input.Question}",
                "GuardrailBlocked",
                null,
                null,
                0,
                JsonSerializer.Serialize(new[] { new { Code = decision.Category, Message = decision.Reason } }),
                (int)sw.ElapsedMilliseconds), ct);
            return Results.BadRequest(new { error = decision.Reason, isOutOfScope = true });
        }
    }

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
api.MapPost("/reports/export-group-pack", async (HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, GroupReportPackService packSvc, IAuditStore audit, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (session.AllowedCompanies.Count == 0) return Results.Forbid();

    var output = await packSvc.BuildPdfAsync(session, ct);
    var totalRows = output.Sections.Sum(s => s.Result.Rows.Count);
    await audit.AppendAsync(new AuditEntry(
        session.UserName,
        "[GroupPack:ALL] Cross-Company Executive PDF Pack",
        "Exported",
        null,
        null,
        totalRows,
        JsonSerializer.Serialize(new { sections = output.Sections.Count }),
        (int)sw.ElapsedMilliseconds), ct);

    var fileName = $"TERO_CrossCompany_Group_Reports_{DateTime.UtcNow:yyyyMMddHHmm}.pdf";
    return Results.File(output.PdfBytes, "application/pdf", fileName);
});

api.MapPost("/reports/export-company-pack", async (string company, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, GroupReportPackService packSvc, IAuditStore audit, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, company, ct);
    if (session is null) return Results.Unauthorized();

    var comp = session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(company, StringComparison.OrdinalIgnoreCase));
    if (comp is null) return Results.Forbid();

    var output = await packSvc.BuildSingleCompanyPdfAsync(session, comp, ct);
    var totalRows = output.Sections.Sum(s => s.Result.Rows.Count);
    await audit.AppendAsync(new AuditEntry(
        session.UserName,
        $"[CompanyPack:{comp.Code}] Single-Company Report Pack PDF",
        "Exported",
        null,
        null,
        totalRows,
        JsonSerializer.Serialize(new { company = comp.Code, sections = output.Sections.Count }),
        (int)sw.ElapsedMilliseconds), ct);

    var fileName = $"TERO_SingleCompany_Reports_{comp.Code}_{DateTime.UtcNow:yyyyMMddHHmm}.pdf";
    return Results.File(output.PdfBytes, "application/pdf", fileName);
});

api.MapPost("/reports/export/{format}", async (string format, DynamicReportRequest req, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, IOptions<PolicyOptions> policyOpts, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, req.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();

    var isAll = req.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase);
    var refCompany = isAll
        ? session.AllowedCompanies.FirstOrDefault()
        : session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(req.CompanyCode, StringComparison.OrdinalIgnoreCase));
    if (refCompany is null) return Results.Forbid();

    var views = isAll
        ? await dsf.GetPermittedViewsAcrossCompaniesAsync(session, ct)
        : await dsf.GetPermittedViewsAsync(session, refCompany, ct);
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

// ---- Admin AI Plan Cache Stats & Invalidation ----
api.MapGet("/admin/ai-cache/stats", async (HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, AiPlanCache cache, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();
    return Results.Ok(cache.GetStats());
});

api.MapDelete("/admin/ai-cache", async (HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, AiPlanCache cache, CancellationToken ct) =>
{
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, null, ct);
    if (session is null) return Results.Unauthorized();
    if (!session.IsAdmin) return Results.Forbid();
    await cache.ClearAsync(ct);
    return Results.Ok(new { ok = true });
});

// ---- AI-Suggested View Drafts + Admin 1-Click Approve & Create ----
api.MapPost("/views/suggest-draft", async (AiConfigureInput input, HttpContext ctx, ErpSecurityProvider sec, CompanyDataSourceFactory dsf, AiReportAssistant ai, IViewStore viewStore, IAuditStore audit, IOptions<DesignerOptions> designerOpts, IOptions<PolicyOptions> policyOpts, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var session = await ResolveActiveSessionAsync(ctx, sec, dsf, input.CompanyCode, ct);
    if (session is null) return Results.Unauthorized();
    var comp = session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(input.CompanyCode, StringComparison.OrdinalIgnoreCase));
    if (comp is null) return Results.Forbid();

    var sources = await dsf.GetSourceTablesAsync(comp, designerOpts.Value.Schemas, ct);
    if (sources.Count == 0) return Results.BadRequest(new { error = "No source tables available for view design." });

    var existing = (await dsf.GetPermittedViewsAsync(session, comp, ct)).Select(v => v.ViewName).ToArray();
    var suggestion = await ai.SuggestViewDraftAsync(input.Question, sources, policyOpts.Value.BlockedColumns, ct);
    if (!suggestion.Ok || suggestion.Request is null)
    {
        await audit.AppendAsync(new AuditEntry(
            session.UserName,
            $"[SuggestDraft:{input.CompanyCode}] {input.Question}",
            suggestion.IsOutOfScope ? "OutOfScope" : "GuardrailBlocked",
            null,
            null,
            0,
            JsonSerializer.Serialize(new[] { new { Code = suggestion.IsOutOfScope ? "OutOfScope" : "ViewDraftGuardrail", Message = suggestion.RefusalReason ?? "View draft blocked." } }),
            (int)sw.ElapsedMilliseconds), ct);
        return Results.BadRequest(new { error = suggestion.RefusalReason, isOutOfScope = suggestion.IsOutOfScope });
    }

    var req = suggestion.Request;
    var areaName = suggestion.AreaName;
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

static async Task<ErpUserSession?> ResolveCliSessionAsync(ErpSecurityProvider sec, string? requestedUser)
{
    if (!string.IsNullOrWhiteSpace(requestedUser))
        return await sec.ResolveUserAsync(requestedUser.Trim(), null, CancellationToken.None);

    var users = await sec.ListUsersAsync(100, CancellationToken.None);
    var ordered = users.Where(u => u.IsActive).OrderByDescending(u => u.ViewAllFlags).Take(15);
    ErpUserSession? best = null;
    foreach (var u in ordered)
    {
        var cand = await sec.ResolveUserAsync(u.UserName, null, CancellationToken.None);
        if (cand is null || !cand.IsActive || cand.AllowedCompanies.Count == 0) continue;
        if (cand.ViewAllFlags && cand.AllowedCompanies.Count >= (best?.AllowedCompanies.Count ?? 0))
            return cand;
        if (best is null || cand.AllowedCompanies.Count > best.AllowedCompanies.Count)
            best = cand;
    }
    return best;
}

static async Task<CompanyAccessInfo?> SelectBestOperationalCompanyAsync(
    ErpUserSession session, CompanyDataSourceFactory dsf, string? requestedCompany)
{
    if (!string.IsNullOrWhiteSpace(requestedCompany))
        return session.AllowedCompanies.FirstOrDefault(c => c.Code.Equals(requestedCompany.Trim(), StringComparison.OrdinalIgnoreCase));

    var candidates = session.AllowedCompanies.Where(c => !c.Code.Equals("TGC", StringComparison.OrdinalIgnoreCase)).ToList();
    if (candidates.Count == 0) candidates = session.AllowedCompanies.ToList();

    CompanyAccessInfo? best = candidates.FirstOrDefault();
    long bestScore = -1;
    var seenConn = new HashSet<string>(StringComparer.Ordinal);
    foreach (var c in candidates.OrderByDescending(x => x.Code.Equals("TGTM", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
    {
        var cs = await dsf.ResolveConnectionStringAsync(c, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(cs) || !seenConn.Add(cs)) continue;
        try
        {
            var ds = dsf.GetOrCreateDataSource(cs);
            await using var conn = await ds.OpenConnectionAsync(CancellationToken.None);
            await using var cmd = new NpgsqlCommand(
                "SELECT (SELECT COUNT(*) FROM rpt.sales_invoice_item_v) + (SELECT COUNT(*) FROM rpt.ar_aging_v)", conn);
            var score = Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None));
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        catch (NpgsqlException)
        {
            // Skip DBs lacking sales_invoice_item_v / ar_aging_v or unreachable
        }
    }
    return best;
}

static string? GetCliArgValue(string[] allArgs, string flag)
{
    var idx = Array.IndexOf(allArgs, flag);
    return idx >= 0 && idx + 1 < allArgs.Length && !allArgs[idx + 1].StartsWith("--") ? allArgs[idx + 1] : null;
}

var catalogIdx = Array.IndexOf(args, "--export-query-catalog-pdfs");
if (catalogIdx >= 0)
{
    var dir = GetCliArgValue(args, "--export-query-catalog-pdfs") ?? "documents";
    var fullDir = Path.GetFullPath(dir);
    Directory.CreateDirectory(fullDir);

    var (groupPdf, groupEntries) = GroupReportPackService.BuildQueryCatalogPdf(singleCompany: false);
    var groupPath = Path.Combine(fullDir, "TERO_CrossCompany_Group_Reports.pdf");
    await File.WriteAllBytesAsync(groupPath, groupPdf);

    var (singlePdf, singleEntries) = GroupReportPackService.BuildQueryCatalogPdf(singleCompany: true);
    var singlePath = Path.Combine(fullDir, "TERO_SingleCompany_Reports.pdf");
    await File.WriteAllBytesAsync(singlePath, singlePdf);

    Console.WriteLine($"Exported query catalog PDFs (no live data):");
    Console.WriteLine($"- {groupPath} ({groupPdf.Length} bytes, {groupEntries.Count} queries)");
    Console.WriteLine($"- {singlePath} ({singlePdf.Length} bytes, {singleEntries.Count} queries)");
    return;
}

var exportGroupIdx = Array.IndexOf(args, "--export-group-pdf");
var exportCompIdx = Array.IndexOf(args, "--export-company-pdf");
if (exportGroupIdx >= 0 || exportCompIdx >= 0)
{
    using var scope = app.Services.CreateScope();
    var sec = scope.ServiceProvider.GetRequiredService<ErpSecurityProvider>();
    var dsf = scope.ServiceProvider.GetRequiredService<CompanyDataSourceFactory>();
    var packSvc = scope.ServiceProvider.GetRequiredService<GroupReportPackService>();

    var session = await ResolveCliSessionAsync(sec, GetCliArgValue(args, "--user"));
    if (session is null || !session.IsActive)
    {
        Console.Error.WriteLine("No active ERP user session could be resolved for PDF export.");
        Environment.ExitCode = 1;
        return;
    }

    GroupPackOutput output;
    string outPath;
    if (exportCompIdx >= 0)
    {
        outPath = GetCliArgValue(args, "--export-company-pdf") ?? "TERO_SingleCompany_Reports.pdf";
        var comp = await SelectBestOperationalCompanyAsync(session, dsf, GetCliArgValue(args, "--company"));
        if (comp is null)
        {
            Console.Error.WriteLine("No matching operational company found for single-company PDF export.");
            Environment.ExitCode = 1;
            return;
        }
        Console.WriteLine($"Company: {comp.Code} — {comp.Name}");
        output = await packSvc.BuildSingleCompanyPdfAsync(session, comp, CancellationToken.None);
    }
    else
    {
        outPath = GetCliArgValue(args, "--export-group-pdf") ?? "TERO_CrossCompany_Group_Reports.pdf";
        output = await packSvc.BuildPdfAsync(session, CancellationToken.None);
    }

    var fullPath = Path.GetFullPath(outPath);
    var dir = Path.GetDirectoryName(fullPath);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    await File.WriteAllBytesAsync(fullPath, output.PdfBytes);

    var totalRows = output.Sections.Sum(s => s.Result.Rows.Count);
    Console.WriteLine($"Exported {fullPath} ({output.PdfBytes.Length} bytes, {output.Sections.Count} sections, {totalRows} total rows)");
    foreach (var s in output.Sections)
        Console.WriteLine($"- {s.Title}: {s.Result.Rows.Count} rows{(string.IsNullOrWhiteSpace(s.Note) ? "" : $" [{s.Note}]")}");
    return;
}

app.Run();

public sealed record LoginRequest(string UserName, string? Password);
public sealed record AiConfigureInput(string Question, string CompanyCode, bool ForceRefresh = false);
public sealed record SaveReportInput(string Name, string? Description, bool IsShared, string? PromptText, DynamicReportRequest Request);
public sealed record ReconcileInput(string CompanyCode, string? Question, string[]? Areas);
