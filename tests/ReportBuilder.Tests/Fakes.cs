using Microsoft.Extensions.Options;
using ReportBuilder.Core;
using ReportBuilder.Core.Catalog;
using ReportBuilder.Core.Data;
using ReportBuilder.Core.Generator;
using ReportBuilder.Core.Llm;
using ReportBuilder.Core.Pipeline;

namespace ReportBuilder.Tests;

/// <summary>Returns canned JSON for each schema name, parsed into whatever type the caller asks for.</summary>
public sealed class ScriptedLlm : ILlmClient
{
    public Dictionary<string, string> Json { get; } = new();
    public string Text { get; set; } = "";
    public bool TextThrows { get; set; }

    public Task<T> CompleteJsonAsync<T>(ModelRole role, string system, string user, string schemaName, string schemaJson, CancellationToken ct)
    {
        var doc = System.Text.Json.JsonSerializer.Deserialize<T>(Json[schemaName], new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Task.FromResult(doc!);
    }

    public Task<string> CompleteTextAsync(ModelRole role, string system, string user, CancellationToken ct)
        => TextThrows ? throw new InvalidOperationException("model offline") : Task.FromResult(Text);
}

public sealed class FakeCatalog : ICatalogStore
{
    public List<MetricEntry> Metrics { get; set; } = new();
    public Dictionary<string, string[]> Views { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Task<IReadOnlyList<MetricEntry>> GetMetricsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MetricEntry>>(Metrics);
    public Task<MetricEntry?> GetMetricAsync(int id, CancellationToken ct) => Task.FromResult(Metrics.FirstOrDefault(m => m.MetricId == id));
    public Task<IReadOnlyDictionary<string, string[]>> GetViewColumnsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, string[]>>(Views);
    public Task<IReadOnlyDictionary<string, ViewColumn[]>> GetViewSchemaAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, ViewColumn[]>>(new Dictionary<string, ViewColumn[]>());
    public Task<int> InsertUnverifiedAsync(IEnumerable<MetricEntry> entries, CancellationToken ct) => Task.FromResult(entries.Count());
    public Task UpdateMetricAsync(MetricEntry edited, bool definitionChanged, CancellationToken ct) => Task.CompletedTask;
    public Task SetReviewAsync(int id, bool approve, string reviewer, string? note, CancellationToken ct) => Task.CompletedTask;
}

public sealed class FakeData : IQueryExecutor, IReconciliationSource, IDraftPreview
{
    public QueryResult Result { get; set; } = new() { Columns = ["region", "value"], Rows = { new object?[] { "North", 100m }, new object?[] { "South", 50m } } };
    public List<ControlTotal> Totals { get; set; } = new();
    public List<ManualPosting> Manual { get; set; } = new();
    public decimal TbNet { get; set; }
    public CompiledQuery? LastQuery { get; private set; }
    public string? LastUser { get; private set; }
    public DraftPreview Preview { get; set; } = new(["region", "value"], new List<string[]> { new[] { "North", "1" } }, 1);

    public Task<QueryResult> ExecuteAsync(CompiledQuery q, string userName, int maxRows, CancellationToken ct) { LastQuery = q; LastUser = userName; return Task.FromResult(Result); }
    public Task<IReadOnlyList<ControlTotal>> GetControlTotalsAsync(string company, CancellationToken ct) => Task.FromResult<IReadOnlyList<ControlTotal>>(Totals);
    public Task<IReadOnlyList<ManualPosting>> GetManualPostingsAsync(string company, IReadOnlyCollection<string> accounts, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ManualPosting>>(Manual.Where(m => accounts.Contains(m.GlAccount)).ToList());
    public Task<decimal> GetTrialBalanceNetAsync(string company, CancellationToken ct) => Task.FromResult(TbNet);
    public Task<DraftPreview> PreviewAsync(string sql, string company, CancellationToken ct) => Task.FromResult(Preview);
}

public sealed class FakeAudit : IAuditStore
{
    public List<AuditEntry> Entries { get; } = new();
    public bool Fail { get; set; }
    public Task<long> AppendAsync(AuditEntry e, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("audit store down");
        Entries.Add(e); return Task.FromResult((long)Entries.Count);
    }
    public Task<long?> VerifyChainAsync(CancellationToken ct) => Task.FromResult<long?>(null);
    public Task<IReadOnlyList<AuditRun>> GetRecentAsync(DateTime sinceUtc, string? userName, int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditRun>>(new List<AuditRun>());
}

public static class Setup
{
    public static readonly Dictionary<string, string[]> Views = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar_aging_v"] = ["company_code", "region", "not_due", "d0_30", "d31_60", "d61_90", "d90_plus", "total_open"],
        ["ar_open_items_v"] = ["company_code", "invoice_no", "customer_name", "region", "open_amt", "days_overdue"],
        ["stock_valuation_v"] = ["company_code", "plant_code", "stock_value", "below_reorder"],
        ["control_totals_v"] = ["company_code", "area", "gl_balance", "subledger_balance", "difference"],
        ["payroll_summary_v"] = ["company_code", "department_name", "gross_pay", "monthly_salary"]
    };

    public static List<MetricEntry> Metrics() => new()
    {
        new MetricEntry { MetricId = 1, Name = "Receivables aging", Description = "Open receivables in aging buckets by region", ViewName = "ar_aging_v", MeasureExpr = "SUM(total_open)",
            Dimensions = ["region"], Synonyms = ["aging", "overdue", "receivables aging"], ControlAreas = ["AR"], IsCertified = true, ReviewStatus = "CERTIFIED" },
        new MetricEntry { MetricId = 2, Name = "Overdue invoice list", Description = "Open customer invoices with days overdue", ViewName = "ar_open_items_v", MeasureExpr = "SUM(open_amt)",
            Dimensions = ["customer_name", "region"], Synonyms = ["overdue", "outstanding invoices"], ControlAreas = ["AR"], IsCertified = true, ReviewStatus = "CERTIFIED" },
        new MetricEntry { MetricId = 3, Name = "Stock value", Description = "Stock valuation by plant", ViewName = "stock_valuation_v", MeasureExpr = "SUM(stock_value)",
            Dimensions = ["plant_code"], Synonyms = ["stock value", "inventory value"], ControlAreas = ["INVENTORY"], IsCertified = false, ReviewStatus = "UNVERIFIED" }
    };

    public static PolicyOptions Policy(bool allowUnverified = true) => new()
    {
        AllowUnverified = allowUnverified, DefaultMaxRows = 100, KnownCompanies = ["1000", "2000"], BlockedColumns = ["monthly_salary"],
        Roles = new()
        {
            ["FIN1000"] = new RolePolicy { Companies = ["1000"], Views = ["*"] },
            ["PLANNER"] = new RolePolicy { Companies = ["1000", "2000"], Views = ["stock_valuation_v"] },
            ["OWNER"] = new RolePolicy { Companies = ["*"], Views = ["*"], CatalogOwner = true }
        }
    };

    public static UserContext User(params string[] roles) => new() { UserName = "tester", Roles = roles };

    public sealed class Rig
    {
        public required ReportPipeline Pipeline { get; init; }
        public required ScriptedLlm Llm { get; init; }
        public required FakeData Data { get; init; }
        public required FakeAudit Audit { get; init; }
        public required FakeCatalog Catalog { get; init; }
    }

    public static Rig Build(PolicyOptions? policy = null)
    {
        var llm = new ScriptedLlm { Text = "Summary text." };
        var data = new FakeData();
        var audit = new FakeAudit();
        var catalog = new FakeCatalog { Metrics = Metrics(), Views = new Dictionary<string, string[]>(Views, StringComparer.OrdinalIgnoreCase) };
        var opts = Options.Create(policy ?? Policy());
        var pipeline = new ReportPipeline(new IntentClassifier(llm), new CatalogMapper(), new ReportPlanner(llm), new QueryCompiler(), new PolicyGate(opts), new Optimizer(),
            data, new ResultValidator(data), new Reconciler(data), new Presenter(llm), catalog, audit, opts);
        return new Rig { Pipeline = pipeline, Llm = llm, Data = data, Audit = audit, Catalog = catalog };
    }

    public static string Intent(string kind, string company, string[] terms, string[]? areas = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { intent = kind, company_code = company, terms, reconcile_areas = areas ?? Array.Empty<string>() });

    public static string Plan(int id, string[] dims, bool clarify = false, string[]? options = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { metric_id = id, dimensions = dims, filters = Array.Empty<object>(), needs_clarification = clarify, clarification_options = options ?? Array.Empty<string>() });
}
