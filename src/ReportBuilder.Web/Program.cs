using System.Security.Claims;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using ReportBuilder.Core;
using ReportBuilder.Core.Catalog;
using ReportBuilder.Core.Data;
using ReportBuilder.Core.Generator;
using ReportBuilder.Core.Pipeline;
using ReportBuilder.Web;
using ReportBuilder.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Deployment profile: set Profile=Ollama or Profile=Azure (env var or --Profile=) to layer appsettings.<Profile>.json over the base settings.
var profile = builder.Configuration["Profile"];
if (!string.IsNullOrWhiteSpace(profile))
    builder.Configuration.AddJsonFile($"appsettings.{profile}.json", optional: false, reloadOnChange: true);

// Optional Key Vault (Azure). Secret names use "--" for ":" (for example Llm--ApiKey). Uses the managed identity in Azure, your developer sign-in locally.
var vaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(vaultUri))
    builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());

// Sign-in: "Windows" (on-prem, Kerberos/NTLM against Active Directory) or "EntraId" (Azure, OpenID Connect). Same code, chosen by config.
var authMode = builder.Configuration["Auth:Mode"] ?? "Windows";
builder.Services.AddReportBuilder(builder.Configuration);
builder.Services.AddSingleton<UserContextFactory>();
builder.Services.AddScoped<ReportSession>();
builder.Services.AddSingleton<ReportLibrary>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
if (authMode.Equals("EntraId", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
    // Roles come from Entra app roles assigned to users or groups; the "roles" claim values must match the keys under Policy:Roles.
    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
    {
        o.TokenValidationParameters.RoleClaimType = "roles";
        o.TokenValidationParameters.NameClaimType = "preferred_username";
    });
}
else
{
    builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
}
builder.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error");

app.UseStaticFiles();
app.UseAuthentication();

// Local development without Active Directory: set Auth:DevUser and Auth:DevRoles in user-secrets. Ignored outside Development.
var devUser = app.Configuration["Auth:DevUser"];
if (app.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(devUser))
{
    var devRoles = app.Configuration.GetSection("Auth:DevRoles").Get<string[]>() ?? Array.Empty<string>();
    app.Use((ctx, next) =>
    {
        var claims = devRoles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimTypes.Name, devUser));
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Dev", ClaimTypes.Name, ClaimTypes.Role));
        return next();
    });
}

app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// ---- JSON API (same pipeline as the UI). Bodies must be application/json, which also blocks cross-site form posts. ----
var api = app.MapGroup("/api");

api.MapPost("/ask", async (AskRequest req, HttpContext ctx, ReportPipeline pipeline, UserContextFactory users, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Question) || req.Question.Length > 1000) return Results.BadRequest(new { error = "Question must be 1 to 1000 characters." });
    return Results.Ok(await pipeline.AskAsync(req.Question.Trim(), users.From(ctx.User), ct));
});

static bool IsOwner(HttpContext ctx, UserContextFactory users, PolicyGate gate) => gate.Resolve(users.From(ctx.User)).CatalogOwner;

api.MapGet("/catalog", async (HttpContext ctx, ICatalogStore store, UserContextFactory users, PolicyGate gate, CancellationToken ct) =>
    IsOwner(ctx, users, gate) ? Results.Ok(await store.GetMetricsAsync(ct)) : Results.Forbid());

api.MapPost("/catalog/{id:int}/review", async (int id, ReviewRequest req, HttpContext ctx, ICatalogStore store, UserContextFactory users, PolicyGate gate, CancellationToken ct) =>
{
    if (!IsOwner(ctx, users, gate)) return Results.Forbid();
    await store.SetReviewAsync(id, req.Approve, users.From(ctx.User).UserName, req.Note, ct);
    return Results.NoContent();
});

api.MapPost("/catalog/generate", async (bool? load, HttpContext ctx, CatalogGenerator gen, ICatalogStore store, UserContextFactory users, PolicyGate gate, IConfiguration cfg, CancellationToken ct) =>
{
    if (!IsOwner(ctx, users, gate)) return Results.Forbid();
    var path = Path.Combine(AppContext.BaseDirectory, cfg["Catalog:QuestionsFile"] ?? "questions.json");
    var questions = System.Text.Json.JsonSerializer.Deserialize<List<TestQuestion>>(await File.ReadAllTextAsync(path, ct)) ?? new();
    var report = await gen.RunAsync(questions, ct);
    var inserted = load == true ? await store.InsertUnverifiedAsync(CatalogGenerator.ToEntries(report), ct) : 0;
    return Results.Ok(new { report.Passed, Total = report.Scores.Count, Inserted = inserted, report.Scores, report.SynonymCollisions });
});

api.MapGet("/audit/verify", async (HttpContext ctx, IAuditStore audit, UserContextFactory users, PolicyGate gate, CancellationToken ct) =>
{
    if (!IsOwner(ctx, users, gate)) return Results.Forbid();
    var broken = await audit.VerifyChainAsync(ct);
    return Results.Ok(new { intact = broken is null, firstBrokenRunId = broken });
});

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();   // for App Service / load balancer probes
app.MapGet("/error", () => Results.Problem("Something went wrong. The incident has been logged.")).AllowAnonymous();
app.Run();

public sealed record AskRequest(string Question);
public sealed record ReviewRequest(bool Approve, string? Note);
