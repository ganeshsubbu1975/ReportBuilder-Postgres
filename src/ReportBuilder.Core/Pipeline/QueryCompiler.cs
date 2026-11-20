using System.Text;
using System.Text.RegularExpressions;

namespace ReportBuilder.Core.Pipeline;

public sealed class PlanRejectedException : Exception
{
    public PlanRejectedException(string message) : base(message) { }
}

/// <summary>
/// Step 4. Builds the SQL from reviewed catalog parts and the validated plan. Identifiers are checked against the catalog and the live
/// view columns, values are always parameters. The model never writes SQL text.
/// </summary>
public sealed class QueryCompiler
{
    private static readonly Regex Ident = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex Measure = new(@"^(SUM|COUNT|AVG|MIN|MAX)\(\s*(\*|[A-Za-z_][A-Za-z0-9_]*(?:\s*[*+\-]\s*[A-Za-z_][A-Za-z0-9_]*)*)\s*\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArgSplit = new(@"\s*[*+\-]\s*", RegexOptions.Compiled);

    public static bool IsSimpleMeasure(string expr) => Measure.IsMatch((expr ?? "").Trim());
    public static bool IsIdentifier(string name) => Ident.IsMatch(name ?? "");

    public CompiledQuery Compile(MetricEntry metric, ReportPlan plan, string company, IReadOnlyDictionary<string, string[]> viewColumns)
    {
        if (!Ident.IsMatch(metric.ViewName)) throw new PlanRejectedException("The catalog view name is not a plain identifier.");
        if (!viewColumns.TryGetValue(metric.ViewName, out var cols)) throw new PlanRejectedException($"View rpt.{metric.ViewName} does not exist.");
        var colSet = cols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!colSet.Contains("company_code")) throw new PlanRejectedException($"View rpt.{metric.ViewName} has no company_code column.");

        var m = Measure.Match(metric.MeasureExpr.Trim());
        if (!m.Success) throw new PlanRejectedException("The catalog measure is not a simple aggregate.");
        if (m.Groups[2].Value != "*")
            foreach (var mc in ArgSplit.Split(m.Groups[2].Value.Trim()))
                if (!colSet.Contains(mc)) throw new PlanRejectedException($"Measure column {mc} is not in rpt.{metric.ViewName}.");

        var dims = plan.Dimensions.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var allowedDims = metric.Dimensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dims)
        {
            if (!Ident.IsMatch(d) || !allowedDims.Contains(d) || !colSet.Contains(d))
                throw new PlanRejectedException($"'{d}' is not an approved group-by column for {metric.Name}.");
        }

        var sql = new StringBuilder();
        sql.Append("SELECT ");
        if (dims.Count > 0) sql.Append(string.Join(", ", dims)).Append(", ");
        sql.Append(metric.MeasureExpr.Trim()).Append(" AS value FROM rpt.").Append(metric.ViewName).Append(" WHERE company_code = @company");

        var parameters = new Dictionary<string, object?> { ["company"] = company };
        var paramColumns = new Dictionary<string, string>();
        for (var i = 0; i < plan.Filters.Count; i++)
        {
            var f = plan.Filters[i];
            if (!Ident.IsMatch(f.Column) || !colSet.Contains(f.Column) || f.Column.Equals("company_code", StringComparison.OrdinalIgnoreCase))
                throw new PlanRejectedException($"'{f.Column}' cannot be used as a filter.");
            if (!ReportPlanner.AllowedOps.Contains(f.Op)) throw new PlanRejectedException($"Operator '{f.Op}' is not allowed.");
            sql.Append(" AND ").Append(f.Column).Append(' ').Append(f.Op).Append(" @f").Append(i);
            parameters["f" + i] = f.Value;
            paramColumns["f" + i] = f.Column;
        }

        var hasOrder = dims.Count > 0;
        if (dims.Count > 0) sql.Append(" GROUP BY ").Append(string.Join(", ", dims)).Append(" ORDER BY ").Append(string.Join(", ", dims));

        return new CompiledQuery
        {
            Sql = sql.ToString(),
            Parameters = parameters,
            ParameterColumns = paramColumns,
            ViewName = metric.ViewName,
            Columns = dims.Append("value").ToArray(),
            HasOrderBy = hasOrder
        };
    }
}
