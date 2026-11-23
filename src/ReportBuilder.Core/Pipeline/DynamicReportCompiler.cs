using System.Text;
using System.Text.RegularExpressions;
using ReportBuilder.Core.Data;
using ReportBuilder.Core.Security;

namespace ReportBuilder.Core.Pipeline;

public sealed class AggregationSpec
{
    public string Column { get; set; } = "";
    public string Function { get; set; } = "SUM"; // SUM, COUNT, AVG, MIN, MAX
    public string Alias { get; set; } = "";
}

public sealed class SortSpec
{
    public string Column { get; set; } = "";
    public string Direction { get; set; } = "DESC"; // ASC or DESC
}

public sealed class HavingFilterSpec
{
    public string Alias { get; set; } = "";
    public string Op { get; set; } = ">";
    public decimal Value { get; set; }
}

public sealed class DynamicReportRequest
{
    public string ViewName { get; set; } = "";
    public string CompanyCode { get; set; } = "";
    public List<string> SelectedColumns { get; set; } = [];
    public List<AggregationSpec> Aggregations { get; set; } = [];
    public List<FilterSpec> Filters { get; set; } = [];
    public List<HavingFilterSpec> HavingFilters { get; set; } = [];
    public string? RelativePeriod { get; set; } // e.g. last_3_quarters, last_6_months, last_30_days, ytd, last_year
    public string? DateColumn { get; set; }
    public List<string> GroupBy { get; set; } = [];
    public List<SortSpec> OrderBy { get; set; } = [];
    public int Limit { get; set; } = 500;
    public string ChartType { get; set; } = "auto";
}

public sealed record DynamicCompileResult(bool Ok, CompiledQuery? Query, IReadOnlyList<string> Errors)
{
    public static DynamicCompileResult Fail(params string[] errors) => new(false, null, errors);
    public static DynamicCompileResult Success(CompiledQuery q) => new(true, q, []);
}

/// <summary>
/// Deterministic C# SQL builder. Validates every view, column, aggregation, operator, and company scope
/// against live rpt.* view metadata and the user's ErpUserSession, computes relative date ranges in C#,
/// and checks the final SQL with SqlGuard before execution.
/// </summary>
public static class DynamicReportCompiler
{
    private static readonly string[] AllowedAggs = ["SUM", "COUNT", "AVG", "MIN", "MAX"];
    private static readonly string[] AllowedOps = ["=", "<>", ">", ">=", "<", "<=", "ILIKE"];
    private static readonly Regex SafeIdent = new(@"^[a-z_][a-z0-9_]{0,63}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RelativeRegex = new(@"^(?:last[_\s]+)?(\d+)[_\s]+(day|week|month|quarter|year)s?(?:[_\s]+ago)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DynamicCompileResult Compile(
        DynamicReportRequest req,
        ErpUserSession session,
        ErpAreaViewMeta viewMeta,
        DateTime? todayUtc = null,
        int maxAllowedRows = 5000)
    {
        if (!session.IsActive)
            return Fail("User account is inactive.");

        if (string.IsNullOrWhiteSpace(req.ViewName) || !viewMeta.ViewName.Equals(req.ViewName, StringComparison.OrdinalIgnoreCase))
            return Fail("Selected reporting view was not found.");

        if (!session.AllowsView(viewMeta.ViewName))
            return Fail($"Access denied: your RBAC permissions do not grant access to rpt.{viewMeta.ViewName}.");

        var isAllCompanies = req.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase);
        if (!isAllCompanies && !session.AllowsCompany(req.CompanyCode))
            return Fail($"Access denied: you do not have permission for company {req.CompanyCode}.");
        if (isAllCompanies && session.AllowedCompanies.Count == 0)
            return Fail("Access denied: your account has no assigned companies.");

        var colSet = viewMeta.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        // 1. Validate SELECT columns & aggregations
        var selectParts = new List<string>();
        var outputCols = new List<string>();
        var nonAggCols = new List<string>();
        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var aggCols = req.Aggregations.Select(a => a.Column).Where(c => !string.IsNullOrWhiteSpace(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aggExprByAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in req.SelectedColumns.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (req.Aggregations.Count > 0 && aggCols.Contains(col)) continue;
            if (session.BlockedColumns.Contains(col)) { errors.Add($"Column '{col}' is restricted by RBAC."); continue; }
            if (!colSet.Contains(col)) { errors.Add($"Column '{col}' does not exist on rpt.{viewMeta.ViewName}."); continue; }
            selectParts.Add(col);
            outputCols.Add(col);
            nonAggCols.Add(col);
            usedAliases.Add(col);
        }

        foreach (var agg in req.Aggregations)
        {
            var fn = (agg.Function ?? "").Trim().ToUpperInvariant();
            if (!AllowedAggs.Contains(fn)) { errors.Add($"Aggregation '{agg.Function}' is not allowed."); continue; }
            if (fn != "COUNT" && !colSet.Contains(agg.Column)) { errors.Add($"Aggregation column '{agg.Column}' does not exist."); continue; }
            if (!string.IsNullOrWhiteSpace(agg.Column) && agg.Column != "*" && session.BlockedColumns.Contains(agg.Column))
                { errors.Add($"Column '{agg.Column}' is restricted by RBAC."); continue; }
            if ((fn == "SUM" || fn == "AVG") && !string.IsNullOrWhiteSpace(agg.Column))
            {
                var aggColMeta = viewMeta.Columns.FirstOrDefault(c => c.Name.Equals(agg.Column, StringComparison.OrdinalIgnoreCase));
                if (aggColMeta is not null && !GuardrailOutputVerifier.IsNumericType(aggColMeta.DataType))
                    { errors.Add($"Aggregation '{fn}' cannot be applied to non-numeric column '{agg.Column}' ({aggColMeta.DataType})."); continue; }
            }

            var colExpr = (fn == "COUNT" && string.IsNullOrWhiteSpace(agg.Column)) ? "*" : agg.Column;
            var alias = string.IsNullOrWhiteSpace(agg.Alias)
                ? $"{fn.ToLowerInvariant()}_{(colExpr == "*" ? "rows" : colExpr)}"
                : agg.Alias.Trim();
            if (!SafeIdent.IsMatch(alias)) { errors.Add($"Invalid alias '{alias}'."); continue; }
            if (!usedAliases.Add(alias)) { errors.Add($"Duplicate output column '{alias}'."); continue; }

            var expr = $"{fn}({colExpr})";
            aggExprByAlias[alias] = expr;
            selectParts.Add($"{expr} AS {alias}");
            outputCols.Add(alias);
        }

        if (selectParts.Count == 0 && errors.Count == 0)
        {
            var fallback = viewMeta.DefaultFields.Where(colSet.Contains).Where(c => !session.BlockedColumns.Contains(c)).ToList();
            if (fallback.Count == 0) fallback = viewMeta.Columns.Take(6).Select(c => c.Name).ToList();
            foreach (var c in fallback) { selectParts.Add(c); outputCols.Add(c); nonAggCols.Add(c); usedAliases.Add(c); }
        }

        // 2. Validate GROUP BY (deterministically include every non-aggregated column in SELECT, GroupBy, and OrderBy)
        var groupCols = new List<string>();
        if (req.Aggregations.Count > 0)
        {
            var orderRawCols = req.OrderBy
                .Select(o => o.Column)
                .Where(c => !string.IsNullOrWhiteSpace(c) && colSet.Contains(c) && !aggExprByAlias.ContainsKey(c));
            var explicitGroup = nonAggCols.Concat(req.GroupBy).Concat(orderRawCols);
            foreach (var g in explicitGroup.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!colSet.Contains(g)) { errors.Add($"Group-by column '{g}' does not exist."); continue; }
                if (session.BlockedColumns.Contains(g)) { errors.Add($"Column '{g}' is restricted by RBAC."); continue; }
                groupCols.Add(g);
            }
        }

        // 3. Build WHERE clauses + parameters
        var whereParts = new List<string>();
        var parameters = new Dictionary<string, object?>();
        var paramCols = new Dictionary<string, string>();
        var pIdx = 0;

        string AddParam(string colName, object? value)
        {
            var name = $"p{pIdx++}";
            parameters[name] = value;
            paramCols[name] = colName;
            return "@" + name;
        }

        if (colSet.Contains("company_code") && !isAllCompanies)
        {
            var pComp = AddParam("company_code", req.CompanyCode);
            whereParts.Add($"(company_code = {pComp} OR company_code = 'ALL')");
        }

        foreach (var f in req.Filters)
        {
            if (string.IsNullOrWhiteSpace(f.Column) || string.IsNullOrWhiteSpace(f.Value)) continue;
            if (!colSet.Contains(f.Column)) { errors.Add($"Filter column '{f.Column}' does not exist."); continue; }
            if (session.BlockedColumns.Contains(f.Column)) { errors.Add($"Filter column '{f.Column}' is restricted by RBAC."); continue; }
            var op = (f.Op ?? "=").Trim().ToUpperInvariant();
            if (!AllowedOps.Contains(op)) { errors.Add($"Filter operator '{f.Op}' is not allowed."); continue; }

            var val = op == "ILIKE" && !f.Value.Contains('%') ? $"%{f.Value.Trim()}%" : f.Value.Trim();
            var colMeta = viewMeta.Columns.First(c => c.Name.Equals(f.Column, StringComparison.OrdinalIgnoreCase));
            if ((colMeta.DataType.Contains("date", StringComparison.OrdinalIgnoreCase) || colMeta.DataType.Contains("timestamp", StringComparison.OrdinalIgnoreCase))
                && !DateTime.TryParse(val, out _)
                && ResolveRelativeStart(val, (todayUtc ?? DateTime.UtcNow).Date) is { } relDate)
            {
                if (op == "=" && (val.Equals("today", StringComparison.OrdinalIgnoreCase) || val.Equals("as of today", StringComparison.OrdinalIgnoreCase)))
                    op = "<=";
                val = relDate.ToString("yyyy-MM-dd");
            }
            var pRef = AddParam(f.Column, val);
            whereParts.Add($"{f.Column} {op} {pRef}");
        }

        if (!string.IsNullOrWhiteSpace(req.RelativePeriod) && !req.RelativePeriod.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var start = ResolveRelativeStart(req.RelativePeriod, (todayUtc ?? DateTime.UtcNow).Date);
            if (start is null)
            {
                errors.Add($"Relative period '{req.RelativePeriod}' is not supported.");
            }
            else
            {
                var dateCol = !string.IsNullOrWhiteSpace(req.DateColumn) && colSet.Contains(req.DateColumn)
                    ? req.DateColumn
                    : viewMeta.DateColumn ?? viewMeta.Columns.FirstOrDefault(c => c.DataType.Contains("date", StringComparison.OrdinalIgnoreCase) || c.DataType.Contains("timestamp", StringComparison.OrdinalIgnoreCase))?.Name;

                if (dateCol is not null)
                {
                    var pStart = AddParam(dateCol, start.Value.ToString("yyyy-MM-dd"));
                    whereParts.Add($"{dateCol} >= {pStart}");
                }
            }
        }

        // 4. Validate ORDER BY
        var orderParts = new List<string>();
        var orderSpecs = new List<(string Column, bool Desc)>();
        foreach (var s in req.OrderBy)
        {
            if (string.IsNullOrWhiteSpace(s.Column)) continue;
            if (!usedAliases.Contains(s.Column) && !colSet.Contains(s.Column))
                { errors.Add($"Sort column '{s.Column}' is not valid."); continue; }
            if (session.BlockedColumns.Contains(s.Column))
                { errors.Add($"Sort column '{s.Column}' is restricted by RBAC."); continue; }
            var dir = s.Direction.Equals("ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            orderParts.Add($"{s.Column} {dir}");
            orderSpecs.Add((s.Column, dir == "DESC"));
        }

        // 3b. Validate HAVING filters (only against defined aggregation aliases)
        var havingParts = new List<string>();
        foreach (var h in req.HavingFilters)
        {
            if (string.IsNullOrWhiteSpace(h.Alias)) continue;
            if (!aggExprByAlias.TryGetValue(h.Alias.Trim(), out var aggExpr))
                { errors.Add($"HAVING alias '{h.Alias}' must match an aggregation alias."); continue; }
            var op = (h.Op ?? ">").Trim();
            if (!new[] { "=", "<>", ">", ">=", "<", "<=" }.Contains(op))
                { errors.Add($"HAVING operator '{h.Op}' is not allowed."); continue; }
            var pName = $"p{pIdx++}";
            parameters[pName] = h.Value;
            havingParts.Add($"{aggExpr} {op} @{pName}");
        }

        if (errors.Count > 0) return new(false, null, errors);

        var limit = Math.Clamp(req.Limit <= 0 ? 500 : req.Limit, 1, maxAllowedRows);
        var sql = new StringBuilder($"SELECT {string.Join(", ", selectParts)} FROM rpt.{viewMeta.ViewName}");
        if (whereParts.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", whereParts));
        if (groupCols.Count > 0) sql.Append(" GROUP BY ").Append(string.Join(", ", groupCols));
        if (havingParts.Count > 0) sql.Append(" HAVING ").Append(string.Join(" AND ", havingParts));
        if (orderParts.Count > 0) sql.Append(" ORDER BY ").Append(string.Join(", ", orderParts));
        sql.Append($" LIMIT {limit}");

        var knownViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { viewMeta.ViewName };
        var guard = SqlGuard.Check(sql.ToString(), knownViews, new HashSet<string>(parameters.Keys, StringComparer.OrdinalIgnoreCase));
        if (!guard.Ok)
            return new(false, null, guard.Issues);

        return Success(new CompiledQuery
        {
            Sql = sql.ToString(),
            Parameters = parameters,
            ParameterColumns = paramCols,
            ViewName = viewMeta.ViewName,
            Columns = outputCols.ToArray(),
            HasOrderBy = orderParts.Count > 0,
            OrderSpecs = orderSpecs,
            FetchLimit = limit
        });
    }

    public static DateTime? ResolveRelativeStart(string period, DateTime today)
    {
        var p = period.Trim().ToLowerInvariant();
        if (p == "today" || p == "as of today") return today;
        if (p == "yesterday") return today.AddDays(-1);
        if (p == "ytd") return new DateTime(today.Year, 1, 1);
        if (p == "last_year" || p == "last year" || p == "1 year ago") return today.AddYears(-1);
        var m = RelativeRegex.Match(p);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n) || n <= 0) return null;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "day" => today.AddDays(-n),
            "week" => today.AddDays(-7 * n),
            "month" => today.AddMonths(-n),
            "quarter" => today.AddMonths(-3 * n),
            "year" => today.AddYears(-n),
            _ => null
        };
    }

    private static DynamicCompileResult Fail(string msg) => DynamicCompileResult.Fail(msg);
    private static DynamicCompileResult Success(CompiledQuery q) => DynamicCompileResult.Success(q);
}
