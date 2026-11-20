namespace ReportBuilder.Core;

public enum IntentKind { Report, Reconcile, Unsupported }
public enum AskStatus { Answered, NeedsClarification, Reconciled, Unsupported, Blocked, Failed }
public enum Severity { Info, Warning, Failure }
public enum ModelRole { Fast, Planner, Presenter, Drafter }

public sealed record Finding(string Code, Severity Severity, string Message);

/// <summary>One governed metric in the catalog (cat.Metric plus its dimensions and synonyms).</summary>
public sealed class MetricEntry
{
    public int MetricId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ViewName { get; set; } = "";
    public string MeasureExpr { get; set; } = "";
    public string SqlTemplate { get; set; } = "";
    public string[] ControlAreas { get; set; } = [];
    public bool IsCertified { get; set; }
    public string ReviewStatus { get; set; } = "UNVERIFIED";   // UNVERIFIED, CERTIFIED, REJECTED
    public string? SourceQuestion { get; set; }
    public string[] Dimensions { get; set; } = [];
    public string[] Synonyms { get; set; } = [];
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    /// <summary>How reports on this metric are drawn: auto, bar, column, line or pie. Auto picks from the shape of the result.</summary>
    public string ChartType { get; set; } = "auto";
}

public sealed class FilterSpec
{
    public string Column { get; set; } = "";
    public string Op { get; set; } = "=";
    public string Value { get; set; } = "";
}

public sealed class ReportPlan
{
    public int MetricId { get; set; }
    public string[] Dimensions { get; set; } = [];
    public List<FilterSpec> Filters { get; set; } = new();
    public bool NeedsClarification { get; set; }
    public string[] ClarificationOptions { get; set; } = [];
}

public sealed class IntentResult
{
    public IntentKind Intent { get; set; }
    public string CompanyCode { get; set; } = "";
    public string[] Terms { get; set; } = [];
    public string[] ReconcileAreas { get; set; } = [];
}

public sealed class CompiledQuery
{
    public string Sql { get; init; } = "";
    public Dictionary<string, object?> Parameters { get; init; } = new();
    /// <summary>Filter parameter name to the view column it is compared with, so the executor can type the value to the column.</summary>
    public Dictionary<string, string> ParameterColumns { get; init; } = new();
    public string ViewName { get; init; } = "";
    public string[] Columns { get; init; } = [];
    public bool HasOrderBy { get; init; }
    public int CommandTimeoutSeconds { get; init; } = 30;
    public int FetchLimit { get; init; }   // 0 = not capped
}

public sealed class QueryResult
{
    public string[] Columns { get; set; } = [];
    public List<object?[]> Rows { get; set; } = new();
    public bool Truncated { get; set; }
}

/// <summary>Pipeline stage keys: intent, semantic, planner, compiler, policy, optimizer, validator, presenter. States: running, done, failed. Payload carries the report spec (planner) or a reason (failed).</summary>
public sealed record StageUpdate(string Stage, string State, string? Payload = null);

/// <summary>How a phrase from the question maps to a governed term.</summary>
public sealed record Mapping(string Said, string Term, string Code);

public sealed record AuditRun(long RunId, DateTime RunAtUtc, string UserName, string Question, string Status, int? MetricId, string? Sql, int RowCount, string Findings, int DurationMs, string PrevHash, string RowHash);

public sealed class AskResponse
{
    public string? Spec { get; set; }                   // JSON the planner produced; SQL is compiled from this
    public List<Mapping> Mappings { get; set; } = new();
    public string[] ControlAreas { get; set; } = [];
    public string? CompanyCode { get; set; }
    public string? ViewName { get; set; }
    public DateTime RanAtUtc { get; set; } = DateTime.UtcNow;
    public AskStatus Status { get; set; }
    public string Answer { get; set; } = "";
    public string Label { get; set; } = "";            // Certified, Unverified, or empty
    public string? MetricName { get; set; }
    public string ChartType { get; set; } = "auto";    // from the metric definition
    public QueryResult? Result { get; set; }
    public string? Sql { get; set; }
    public List<Finding> Findings { get; set; } = new();
    public string[] Clarifications { get; set; } = [];
    public long RunId { get; set; }
}

public sealed record AuditEntry(string UserName, string Question, string Status, int? MetricId, string? Sql, int RowCount, string Findings, int DurationMs);
