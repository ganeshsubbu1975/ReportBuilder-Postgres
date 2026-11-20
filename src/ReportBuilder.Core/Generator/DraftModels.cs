using System.Text.Json.Serialization;
using ReportBuilder.Core.Pipeline;

namespace ReportBuilder.Core.Generator;

/// <summary>A catalog entry proposed by a model. Never trusted until DraftValidator has run it.</summary>
public sealed class DraftDto
{
    [JsonPropertyName("intent")] public string Intent { get; set; } = "";                 // report | reconcile | unsupported
    [JsonPropertyName("metric_name")] public string MetricName { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("view")] public string View { get; set; } = "";
    [JsonPropertyName("measure_expr")] public string MeasureExpr { get; set; } = "";
    [JsonPropertyName("dimensions")] public string[] Dimensions { get; set; } = [];
    [JsonPropertyName("synonyms")] public string[] Synonyms { get; set; } = [];
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("control_area")] public string[] ControlArea { get; set; } = [];
    [JsonPropertyName("needs_clarification")] public bool NeedsClarification { get; set; }
    [JsonPropertyName("clarification_options")] public string[] ClarificationOptions { get; set; } = [];
    [JsonPropertyName("rationale")] public string Rationale { get; set; } = "";
}

public sealed class TestQuestion
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";   // answerable | ambiguous | reconcile | unsupported | unsafe
    [JsonPropertyName("expect_view")] public string? ExpectView { get; set; }
    [JsonPropertyName("expect_dims")] public string[]? ExpectDims { get; set; }
    [JsonPropertyName("expect_areas")] public string[]? ExpectAreas { get; set; }
    [JsonPropertyName("expect_company")] public string? ExpectCompany { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

public enum DraftStatus { Candidate, NeedsClarification, Reconcile, Unsupported, Rejected, Error }

public sealed class DraftOutcome
{
    public TestQuestion Question { get; set; } = new();
    public string Company { get; set; } = "1000";
    public DraftDto Draft { get; set; } = new();
    public DraftStatus Status { get; set; }
    public List<string> Issues { get; set; } = new();
    public DraftPreview? Preview { get; set; }
}

public sealed class ScoreLine
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Passed { get; set; }
    public string Why { get; set; } = "";
}

public sealed class GenerationReport
{
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public List<DraftOutcome> Outcomes { get; set; } = new();
    public List<ScoreLine> Scores { get; set; } = new();
    public Dictionary<string, string[]> SynonymCollisions { get; set; } = new();
    public int Passed => Scores.Count(s => s.Passed);
}
