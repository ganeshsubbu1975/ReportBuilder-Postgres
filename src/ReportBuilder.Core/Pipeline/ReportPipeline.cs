using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReportBuilder.Core.Catalog;
using ReportBuilder.Core.Data;

namespace ReportBuilder.Core.Pipeline;

/// <summary>
/// Runs the nine steps. Models plan (steps 1 to 3) and write the summary (step 9); everything that touches data (steps 4 to 8) is deterministic code.
/// Every request ends in one audit row, whatever the outcome.
/// </summary>
public sealed class ReportPipeline
{
    private readonly IntentClassifier _intent;
    private readonly CatalogMapper _mapper;
    private readonly ReportPlanner _planner;
    private readonly QueryCompiler _compiler;
    private readonly PolicyGate _policy;
    private readonly Optimizer _optimizer;
    private readonly IQueryExecutor _executor;
    private readonly ResultValidator _validator;
    private readonly Reconciler _reconciler;
    private readonly Presenter _presenter;
    private readonly ICatalogStore _catalog;
    private readonly IAuditStore _audit;
    private readonly PolicyOptions _opts;

    public ReportPipeline(IntentClassifier intent, CatalogMapper mapper, ReportPlanner planner, QueryCompiler compiler, PolicyGate policy, Optimizer optimizer,
        IQueryExecutor executor, ResultValidator validator, Reconciler reconciler, Presenter presenter, ICatalogStore catalog, IAuditStore audit, IOptions<PolicyOptions> opts)
    {
        _intent = intent; _mapper = mapper; _planner = planner; _compiler = compiler; _policy = policy; _optimizer = optimizer; _executor = executor;
        _validator = validator; _reconciler = reconciler; _presenter = presenter; _catalog = catalog; _audit = audit; _opts = opts.Value;
    }

    private sealed class RunState
    {
        public int? MetricId;
        public string? Sql;
        public int RowCount;
        public List<Finding> Findings = new();
        public Action<StageUpdate>? Progress;
        public string? Spec;
        public string? Company;
        public MetricEntry? Metric;
        public List<Mapping> Mappings = new();
        public string Stage = "";
        public void Report(string stage, string state, string? payload = null) { Stage = stage; Progress?.Invoke(new StageUpdate(stage, state, payload)); }
    }

    private static string Slug(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    public async Task<AskResponse> AskAsync(string question, UserContext user, CancellationToken ct = default, Action<StageUpdate>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var state = new RunState { Progress = progress };
        AskResponse resp;
        try
        {
            resp = await RunAsync(question, user, state, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            state.Findings.Add(new Finding("pipeline_error", Severity.Failure, ex.Message));
            if (state.Stage.Length > 0) state.Report(state.Stage, "failed", ex.Message);
            resp = new AskResponse { Status = AskStatus.Failed, Answer = "This report could not be produced. The failure has been logged." };
        }
        resp.Findings = state.Findings;
        resp.Sql = state.Sql;
        resp.Spec = state.Spec;
        resp.Mappings = state.Mappings;
        resp.CompanyCode = state.Company;
        resp.ViewName = state.Metric?.ViewName;
        resp.ControlAreas = state.Metric?.ControlAreas ?? [];
        resp.RanAtUtc = DateTime.UtcNow;

        try
        {
            var entry = new AuditEntry(user.UserName, question, resp.Status.ToString(), state.MetricId, state.Sql, state.RowCount,
                JsonSerializer.Serialize(state.Findings.Select(f => new { f.Code, Severity = f.Severity.ToString(), f.Message })), (int)sw.ElapsedMilliseconds);
            resp.RunId = await _audit.AppendAsync(entry, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Fail closed: no audit row means no answer is released.
            return new AskResponse { Status = AskStatus.Failed, Answer = "The request could not be recorded in the audit log, so no result was released." };
        }
        return resp;
    }

    private async Task<AskResponse> RunAsync(string question, UserContext user, RunState st, CancellationToken ct)
    {
        var policy = _policy.Resolve(user);
        if (policy.Companies.Count == 0 || policy.Views.Count == 0)
            return Blocked(st, "no_role", "Your account has no access to any report data.");

        // 1. intent
        st.Report("intent", "running");
        var intent = await _intent.ClassifyAsync(question, ct);
        st.Report("intent", "done");
        if (intent.Intent == IntentKind.Unsupported)
            return new AskResponse { Status = AskStatus.Unsupported, Answer = "I can only build read-only reports from the ERP reporting views. I cannot forecast, change data or show restricted information." };

        // company
        var visible = _policy.VisibleCompanies(policy);
        var company = intent.CompanyCode;
        if (company.Length == 0)
        {
            if (visible.Count == 1) company = visible[0];
            else return new AskResponse { Status = AskStatus.NeedsClarification, Answer = "Which company should I use?", Clarifications = visible.ToArray() };
        }
        st.Company = company;
        st.Mappings.Add(new Mapping($"company {company}", $"Company code {company}", "scope.company_code"));
        if (!policy.AllowsCompany(company))
            return Blocked(st, "company_denied", $"You do not have access to company {company}.");
        if (_opts.KnownCompanies.Length > 0 && !_opts.KnownCompanies.Contains(company))
            return new AskResponse { Status = AskStatus.Unsupported, Answer = $"Company {company} is not known." };

        var viewColumns = await _catalog.GetViewColumnsAsync(ct);
        var knownViews = viewColumns.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // reconciliation path
        if (intent.Intent == IntentKind.Reconcile)
        {
            if (!policy.AllowsView("control_totals_v")) return Blocked(st, "view_denied", "You do not have access to the reconciliation data.");
            var (tb, areas) = await _reconciler.ExplainAsync(company, intent.ReconcileAreas, ct);
            var rows = new QueryResult { Columns = ["area", "gl_balance", "subledger_balance", "difference", "explanation"] };
            foreach (var a in areas) rows.Rows.Add([a.Area, a.GlBalance, a.SubledgerBalance, a.Difference, a.Explanation]);
            st.RowCount = rows.Rows.Count;
            foreach (var a in areas.Where(a => a.Difference != 0m))
                st.Findings.Add(new Finding("control_break", a.Resolved ? Severity.Warning : Severity.Failure, $"{a.Area}: difference {a.Difference:N2}"));
            var text = await _presenter.SummarizeReconciliationAsync(question, company, tb, areas, ct);
            return new AskResponse { Status = AskStatus.Reconciled, Answer = text, Result = rows, Label = "Reconciliation" };
        }

        // 2. mapping
        st.Report("semantic", "running");
        var catalog = await _catalog.GetMetricsAsync(ct);
        var map = _mapper.Find(question, intent.Terms, catalog, _opts.AllowUnverified);
        if (map.AmbiguityOptions.Length > 0)
            return new AskResponse { Status = AskStatus.NeedsClarification, Answer = "That term can mean more than one report. Which one do you want?", Clarifications = map.AmbiguityOptions };
        if (map.Candidates.Count == 0)
            return new AskResponse { Status = AskStatus.Unsupported, Answer = "There is no approved report in the catalog that matches this question yet." };

        st.Report("semantic", "done");

        // 3. plan
        st.Report("planner", "running");
        var plan = await _planner.PlanAsync(question, map.Candidates, viewColumns, ct);
        if (plan.NeedsClarification)
            return new AskResponse { Status = AskStatus.NeedsClarification, Answer = "I need a little more detail to pick the right report.", Clarifications = plan.ClarificationOptions };
        var metric = map.Candidates.FirstOrDefault(m => m.MetricId == plan.MetricId);
        if (metric is null) { st.Findings.Add(new Finding("plan_invalid", Severity.Failure, "The plan named a metric that was not offered.")); return Failed("I could not match that question to an approved report."); }
        st.MetricId = metric.MetricId;
        st.Metric = metric;
        var slug = Slug(metric.Name);
        st.Spec = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["report"] = plan.Dimensions.Length == 0 ? slug : slug + "_by_" + string.Join("_", plan.Dimensions.Select(Slug)),
            ["metrics"] = new[] { slug },
            ["dimensions"] = plan.Dimensions,
            ["filters"] = plan.Filters.Select(f => new Dictionary<string, string> { ["column"] = f.Column, ["op"] = f.Op, ["value"] = f.Value })
                .Prepend(new Dictionary<string, string> { ["company_code"] = company }).ToArray(),
            ["run_as"] = "current_user"
        }, new JsonSerializerOptions { WriteIndented = true });
        var said = intent.Terms.FirstOrDefault() ?? metric.Name;
        st.Mappings.Insert(0, new Mapping(said, metric.Name, "metric." + slug));
        foreach (var d in plan.Dimensions) st.Mappings.Insert(1, new Mapping("by " + d.Replace('_', ' '), d.Replace('_', ' '), "dim." + d));
        st.Report("planner", "done", st.Spec);
        if (!_opts.AllowUnverified && !metric.IsCertified) return Blocked(st, "not_certified", "This report has not been approved yet.");

        // 4. compile
        st.Report("compiler", "running");
        CompiledQuery compiled;
        try { compiled = _compiler.Compile(metric, plan, company, viewColumns); }
        catch (PlanRejectedException ex) { st.Findings.Add(new Finding("plan_rejected", Severity.Failure, ex.Message)); return Failed("I could not build that report safely."); }

        st.Report("compiler", "done");

        // 5. policy gate
        st.Report("policy", "running");
        var decision = _policy.Evaluate(policy, company, compiled, knownViews);
        if (!decision.Allowed) { st.Sql = compiled.Sql; st.Report("policy", "failed", decision.Reason); return Blocked(st, "policy_denied", decision.Reason); }
        st.Report("policy", "done");

        // 6. optimize
        st.Report("optimizer", "running");
        var final = _optimizer.Optimize(compiled, policy.MaxRows);
        st.Sql = final.Sql;
        // the optimizer changed the SQL, so guard the final text as well
        var guard = SqlGuard.Check(final.Sql, knownViews, new HashSet<string>(final.Parameters.Keys, StringComparer.OrdinalIgnoreCase));
        if (!guard.Ok) { st.Report("optimizer", "failed"); return Blocked(st, "guard_failed", "The final SQL failed the safety check: " + string.Join("; ", guard.Issues)); }
        st.Report("optimizer", "done");

        // 7. execute
        st.Report("validator", "running");
        QueryResult result;
        try { result = await _executor.ExecuteAsync(final, user.UserName, policy.MaxRows, ct); }
        catch (PlanRejectedException ex)
        {
            st.Report("validator", "failed", ex.Message);
            st.Findings.Add(new Finding("filter_invalid", Severity.Failure, ex.Message));
            return Failed("I could not run that report: " + ex.Message);
        }
        st.RowCount = result.Rows.Count;

        // 8. validate
        var findings = await _validator.ValidateAsync(metric, result, company, ct);
        st.Findings.AddRange(findings);
        var label = ResultValidator.LabelFor(metric, findings);

        st.Report("validator", "done");

        // 9. present
        st.Report("presenter", "running");
        var answer = await _presenter.SummarizeReportAsync(question, metric, result, label, findings, ct);
        st.Report("presenter", "done");
        return new AskResponse { Status = AskStatus.Answered, Answer = answer, Label = label, MetricName = metric.Name, ChartType = metric.ChartType, Result = result };
    }

    private static AskResponse Blocked(RunState st, string code, string message)
    {
        st.Findings.Add(new Finding(code, Severity.Failure, message));
        return new AskResponse { Status = AskStatus.Blocked, Answer = message };
    }

    private static AskResponse Failed(string message) => new() { Status = AskStatus.Failed, Answer = message };
}
