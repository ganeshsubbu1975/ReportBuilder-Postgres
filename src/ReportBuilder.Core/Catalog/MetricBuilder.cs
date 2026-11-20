using System.Text.RegularExpressions;
using ReportBuilder.Core.Pipeline;

namespace ReportBuilder.Core.Catalog;

public sealed record ViewColumn(string Name, string DataType);

/// <summary>What a catalog owner fills in on the New metric screen. Everything is picked from lists, nothing is typed as SQL.</summary>
public sealed class MetricRequest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string View { get; set; } = "";
    public string Aggregate { get; set; } = "SUM";          // SUM, AVG, MIN, MAX, COUNT, COUNT DISTINCT
    public string MeasureColumn { get; set; } = "";         // empty with COUNT means COUNT(*)
    public List<string> ExtraMeasureColumns { get; set; } = new();  // more columns combined with MeasureColumn, e.g. quantity * sold_price
    public string MeasureOperator { get; set; } = "*";      // *, + or - between the measured columns
    public List<string> Dimensions { get; set; } = new();
    public string Synonyms { get; set; } = "";              // comma or line separated
    public string ChartType { get; set; } = "auto";         // auto, bar, column, line, pie
}

public sealed record MetricBuildResult(MetricEntry? Entry, IReadOnlyList<string> Issues, string PreviewSql)
{
    public bool Ok => Entry is not null && Issues.Count == 0;
}

/// <summary>
/// Turns a MetricRequest into a catalog entry. The aggregate comes from a fixed list, every identifier must be a column of the chosen
/// rpt view, and the SQL is assembled here, so a user cannot smuggle SQL into the catalog. The result is still checked by SqlGuard.
/// </summary>
public static class MetricBuilder
{
    public static readonly string[] Aggregates = ["SUM", "AVG", "MIN", "MAX", "COUNT", "COUNT DISTINCT"];
    public static readonly string[] MeasureOperators = ["*", "+", "-"];
    public static readonly string[] ChartTypes = ["auto", "bar", "column", "line", "pie"];
    private static readonly HashSet<string> Numeric = new(StringComparer.OrdinalIgnoreCase)
        { "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney", "float", "real" };
    private static readonly Regex Ident = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static bool IsNumeric(string dataType) => Numeric.Contains(dataType);

    /// <summary>The checks shared by creating and editing: name, description, chart type and synonyms.</summary>
    private static (string Name, string Description, string? Chart, List<string> Synonyms) Common(MetricRequest req, List<string> issues)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length < 3 || name.Length > 100) issues.Add("Give the metric a name of 3 to 100 characters.");
        var description = (req.Description ?? "").Trim();
        if (description.Length == 0) issues.Add("Add a short description so reviewers know what the metric means.");
        if (description.Length > 400) issues.Add("The description is longer than 400 characters.");

        var wanted = (req.ChartType ?? "").Trim();
        var chart = ChartTypes.FirstOrDefault(c => c.Equals(wanted.Length == 0 ? "auto" : wanted, StringComparison.OrdinalIgnoreCase));
        if (chart is null) issues.Add("Choose a chart type from the list.");

        var synonyms = (req.Synonyms ?? "").Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).Append(name.ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        if (synonyms.Any(s => s.Length > 100)) issues.Add("A synonym is longer than 100 characters.");
        if (synonyms.Count > 15) issues.Add("Use at most 15 synonyms.");
        return (name, description, chart, synonyms);
    }

    private static readonly Regex MeasureShape = new(@"^(SUM|AVG|MIN|MAX|COUNT)\((DISTINCT )?([A-Za-z_][A-Za-z0-9_]*|\*)(?:\s*([*+\-])\s*([A-Za-z_][A-Za-z0-9_]*(?:\s*\4\s*[A-Za-z_][A-Za-z0-9_]*)*))?\)$", RegexOptions.Compiled);

    /// <summary>
    /// Turns a saved metric back into the form the New metric screen fills in. DefinitionEditable is true only when rebuilding the request gives
    /// exactly the SQL that is saved, so a metric whose SQL came from somewhere else (the generator) keeps its SQL and only its labels can change.
    /// </summary>
    public static (MetricRequest Request, bool DefinitionEditable) ToRequest(MetricEntry e, IReadOnlyDictionary<string, ViewColumn[]> schema, IEnumerable<string> blockedColumns)
    {
        var req = new MetricRequest
        {
            Name = e.Name, Description = e.Description, View = e.ViewName, Dimensions = e.Dimensions.ToList(), ChartType = e.ChartType,
            Synonyms = string.Join(", ", e.Synonyms.Where(s => !s.Equals(e.Name, StringComparison.OrdinalIgnoreCase)))
        };
        var m = MeasureShape.Match(e.MeasureExpr);
        if (!m.Success) return (req, false);
        var distinct = m.Groups[2].Success;
        if (distinct && m.Groups[1].Value != "COUNT") return (req, false);
        req.Aggregate = distinct ? "COUNT DISTINCT" : m.Groups[1].Value;
        req.MeasureColumn = m.Groups[3].Value == "*" ? "" : m.Groups[3].Value;
        if (m.Groups[4].Success)
        {
            if (distinct || m.Groups[3].Value == "*") return (req, false);
            req.MeasureOperator = m.Groups[4].Value;
            req.ExtraMeasureColumns = Regex.Split(m.Groups[5].Value.Trim(), @"\s*" + Regex.Escape(req.MeasureOperator) + @"\s*").ToList();
        }
        var rebuilt = Build(req, schema, blockedColumns, "edit");
        return (req, rebuilt.Entry?.SqlTemplate == e.SqlTemplate);
    }

    private static Regex TemplateShape(string measureExpr) => new(
        @"^SELECT (?:(?<dims>.+?), )?" + Regex.Escape(measureExpr) + @" AS value FROM rpt\.(?<view>[A-Za-z_][A-Za-z0-9_]*) WHERE company_code = :company(?: GROUP BY (?<group>.+))?$",
        RegexOptions.Compiled);

    /// <summary>True when the saved SQL has the standard shape the builder produces, so its group-by columns can be swapped without touching the measure.</summary>
    public static bool CanEditDimensions(MetricEntry e) => TemplateShape(e.MeasureExpr).IsMatch(e.SqlTemplate);

    /// <summary>
    /// An edit to a metric whose SQL was not built on the screen: the name, description, synonyms, chart type and group-by columns can change.
    /// The measure and view stay as saved, and the SELECT and GROUP BY parts are rebuilt from the chosen columns after the same checks Build applies.
    /// </summary>
    public static MetricBuildResult ApplyEdit(MetricEntry existing, MetricRequest req, IReadOnlyDictionary<string, ViewColumn[]> schema, IEnumerable<string> blockedColumns)
    {
        var issues = new List<string>();
        var (name, description, chart, synonyms) = Common(req, issues);

        var requested = (req.Dimensions ?? new()).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dims = existing.Dimensions;
        var template = existing.SqlTemplate;
        if (!requested.Order().SequenceEqual(existing.Dimensions.Order(), StringComparer.OrdinalIgnoreCase))
        {
            if (!CanEditDimensions(existing) || !schema.TryGetValue(existing.ViewName, out var columns))
                issues.Add("This metric's SQL is not in the standard shape, so its group-by columns cannot be changed here.");
            else
            {
                var blocked = blockedColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var measured = Regex.Matches(existing.MeasureExpr, @"[A-Za-z_][A-Za-z0-9_]*").Select(m => m.Value)
                    .Where(v => !Aggregates.Concat(["DISTINCT"]).Contains(v, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var picked = new List<string>();
                foreach (var d in requested)
                {
                    var dc = columns.FirstOrDefault(c => c.Name.Equals(d, StringComparison.OrdinalIgnoreCase));
                    if (dc is null) issues.Add($"Group-by column {d} is not in rpt.{existing.ViewName}.");
                    else if (dc.Name.Equals("company_code", StringComparison.OrdinalIgnoreCase) || blocked.Contains(dc.Name)) issues.Add($"Column {dc.Name} is restricted and cannot be used to group.");
                    else if (measured.Contains(dc.Name)) issues.Add($"{dc.Name} is a measured column, so it cannot also be a group-by column.");
                    else picked.Add(dc.Name);
                }
                if (picked.Count > 5) issues.Add("Choose at most 5 group-by columns.");
                if (issues.Count == 0)
                {
                    var select = picked.Count == 0 ? "" : string.Join(", ", picked) + ", ";
                    template = $"SELECT {select}{existing.MeasureExpr} AS value FROM rpt.{existing.ViewName} WHERE company_code = :company"
                        + (picked.Count > 0 ? " GROUP BY " + string.Join(", ", picked) : "");
                    var guard = SqlGuard.Check(template.Replace(":company", "@company"), new HashSet<string>(schema.Keys, StringComparer.OrdinalIgnoreCase), new HashSet<string>(["company"], StringComparer.OrdinalIgnoreCase));
                    if (!guard.Ok) issues.AddRange(guard.Issues.Select(i => "The SQL failed the safety check: " + i));
                    dims = picked.ToArray();
                }
            }
        }
        if (issues.Count > 0) return new(null, issues, "");
        var entry = new MetricEntry
        {
            MetricId = existing.MetricId, Name = name, Description = description, ViewName = existing.ViewName, MeasureExpr = existing.MeasureExpr,
            SqlTemplate = template, Dimensions = dims, Synonyms = synonyms.ToArray(), ControlAreas = existing.ControlAreas,
            ReviewStatus = existing.ReviewStatus, IsCertified = existing.IsCertified, SourceQuestion = existing.SourceQuestion, ChartType = chart!
        };
        return new(entry, issues, template.Replace(":company", "@company"));
    }

    public static MetricBuildResult Build(MetricRequest req, IReadOnlyDictionary<string, ViewColumn[]> schema, IEnumerable<string> blockedColumns, string createdBy)
    {
        var issues = new List<string>();
        var blocked = blockedColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var (name, description, chart, synonyms) = Common(req, issues);

        if (!Ident.IsMatch(req.View ?? "") || !schema.TryGetValue(req.View!, out var columns))
            return new(null, issues.Append("Choose a reporting view from the list.").ToList(), "");
        if (!columns.Any(c => c.Name.Equals("company_code", StringComparison.OrdinalIgnoreCase)))
            issues.Add($"View rpt.{req.View} has no company_code column, so it cannot be restricted by company.");

        ViewColumn? Find(string col) => columns.FirstOrDefault(c => c.Name.Equals(col, StringComparison.OrdinalIgnoreCase));
        bool Usable(string col) => !col.Equals("company_code", StringComparison.OrdinalIgnoreCase) && !blocked.Contains(col);

        var agg = Aggregates.FirstOrDefault(a => a.Equals((req.Aggregate ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (agg is null) issues.Add("Choose how to combine the values: sum, average, minimum, maximum or count.");

        string measure = "";
        var measuredCols = new List<string>();
        var measureCol = (req.MeasureColumn ?? "").Trim();
        if (agg is not null)
        {
            if (measureCol.Length == 0)
            {
                if (agg == "COUNT") measure = "COUNT(*)";
                else issues.Add("Choose the column to measure.");
            }
            else
            {
                var mc = Find(measureCol);
                var extras = (req.ExtraMeasureColumns ?? new()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToList();
                var op = MeasureOperators.FirstOrDefault(o => o == (req.MeasureOperator ?? "*").Trim());
                if (mc is null) issues.Add($"Column {measureCol} is not in rpt.{req.View}.");
                else if (!Usable(mc.Name)) issues.Add($"Column {mc.Name} is restricted and cannot be used.");
                else if (agg is "SUM" or "AVG" && !IsNumeric(mc.DataType)) issues.Add($"{agg} needs a numeric column, and {mc.Name} is {mc.DataType}.");
                else if (extras.Count > 0 && (agg is "COUNT" or "COUNT DISTINCT")) issues.Add("Counting uses a single column. Remove the extra measure columns.");
                else if (extras.Count > 0 && op is null) issues.Add("Choose how to combine the columns: multiply, add or subtract.");
                else if (extras.Count > 4) issues.Add("Combine at most 5 columns in one measure.");
                else
                {
                    var parts = new List<string> { mc.Name };
                    foreach (var x in extras)
                    {
                        var xc = Find(x);
                        if (xc is null) issues.Add($"Column {x} is not in rpt.{req.View}.");
                        else if (!Usable(xc.Name)) issues.Add($"Column {xc.Name} is restricted and cannot be used.");
                        else if (!IsNumeric(xc.DataType)) issues.Add($"Combined columns must be numeric, and {xc.Name} is {xc.DataType}.");
                        else if (parts.Contains(xc.Name, StringComparer.OrdinalIgnoreCase)) issues.Add($"{xc.Name} is used twice in the measure.");
                        else parts.Add(xc.Name);
                    }
                    if (issues.Count == 0)
                    {
                        var arg = string.Join($" {op} ", parts);
                        measure = agg == "COUNT DISTINCT" ? $"COUNT(DISTINCT {arg})" : $"{agg}({arg})";
                        measuredCols = parts;
                    }
                }
                measureCol = mc?.Name ?? measureCol;
            }
        }

        var dims = new List<string>();
        foreach (var d in (req.Dimensions ?? new()).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dc = Find(d);
            if (dc is null) issues.Add($"Group-by column {d} is not in rpt.{req.View}.");
            else if (!Usable(dc.Name)) issues.Add($"Column {dc.Name} is restricted and cannot be used to group.");
            else if (measuredCols.Contains(dc.Name, StringComparer.OrdinalIgnoreCase) || dc.Name.Equals(measureCol, StringComparison.OrdinalIgnoreCase)) issues.Add($"{dc.Name} is a measured column, so it cannot also be a group-by column.");
            else dims.Add(dc.Name);
        }
        if (dims.Count > 5) issues.Add("Choose at most 5 group-by columns.");

        if (issues.Count > 0) return new(null, issues, "");

        var select = dims.Count == 0 ? "" : string.Join(", ", dims) + ", ";
        var template = $"SELECT {select}{measure} AS value FROM rpt.{req.View} WHERE company_code = :company" + (dims.Count > 0 ? " GROUP BY " + string.Join(", ", dims) : "");
        var previewSql = template.Replace(":company", "@company");

        var guard = SqlGuard.Check(previewSql, new HashSet<string>(schema.Keys, StringComparer.OrdinalIgnoreCase), new HashSet<string>(["company"], StringComparer.OrdinalIgnoreCase));
        if (!guard.Ok) return new(null, guard.Issues.Select(i => "The SQL failed the safety check: " + i).ToList(), previewSql);

        var entry = new MetricEntry
        {
            Name = name, Description = description, ViewName = req.View!, MeasureExpr = measure, SqlTemplate = template,
            Dimensions = dims.ToArray(), Synonyms = synonyms.ToArray(), ControlAreas = [],
            ReviewStatus = "UNVERIFIED", IsCertified = false, ChartType = chart!, SourceQuestion = "Created on the New metric screen by " + createdBy
        };
        return new(entry, issues, previewSql);
    }
}
