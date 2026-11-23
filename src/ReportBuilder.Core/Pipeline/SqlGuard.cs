using System.Text.RegularExpressions;

namespace ReportBuilder.Core.Pipeline;

public sealed record GuardResult(bool Ok, IReadOnlyList<string> Issues, IReadOnlySet<string> Views);

/// <summary>
/// Last line of defence for any SQL that is about to run. It accepts one read-only SELECT over rpt views
/// with an approved parameter list, and nothing else. It does not trust the model, the catalog or the compiler.
/// </summary>
public static class SqlGuard
{
    private static readonly Regex Forbidden = new(
        @"\b(insert|update|delete|drop|alter|create|truncate|merge|exec|execute|grant|revoke|into|copy|call|do|vacuum|union|intersect|except|pg_\w+|lo_\w+|dblink\w*|set_config|current_setting)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RptRef = new(@"\brpt\s*\.\s*(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BaseSchemaRef = new(
        @"\b(org|fin|inv|mfg|ar|ap|sal|pur|hr|cfg|cat|audit|common|public|finance|sales|trading|hrms|dbo|sys|pg_catalog|information_schema|pg_toast|pg_temp)\s*\.\s*\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SqlKeywordFromExpr = new(
        @"\b(?:extract|trim|substring)\s*\([^()]*\bfrom\b[^()]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FromJoinTarget = new(
        @"\b(?:from|join)\s+(?:(\()|(rpt\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)|([^\s,;()]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Param = new(@"(?<!@)@(\w+)", RegexOptions.Compiled);

    public static GuardResult Check(string sql, IReadOnlySet<string> knownViews, IReadOnlySet<string> allowedParams)
    {
        var issues = new List<string>();
        var text = (sql ?? "").Trim();
        if (text.Length == 0) issues.Add("empty SQL");
        if (text.TrimEnd(';').Contains(';')) issues.Add("multiple statements");
        if (!Regex.IsMatch(text, @"^\s*select\b", RegexOptions.IgnoreCase)) issues.Add("not a SELECT");
        if (Forbidden.IsMatch(text)) issues.Add("forbidden keyword");
        if (text.Contains("--") || text.Contains("/*")) issues.Add("comment in SQL");
        if (text.Contains("@@")) issues.Add("system variable");
        if (text.Contains('[') || text.Contains(']') || text.Contains('"')) issues.Add("quoted identifiers are not allowed");

        var views = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RptRef.Matches(text)) views.Add(m.Groups[1].Value);
        if (views.Count == 0) issues.Add("no rpt view referenced");
        foreach (var v in views)
            if (!knownViews.Contains(v)) issues.Add($"unknown view rpt.{v}");

        // Strip string literals and SQL function expressions containing FROM (EXTRACT/TRIM/SUBSTRING)
        var literalSafe = Regex.Replace(text, @"'[^']*'", "''");
        var targetScan = SqlKeywordFromExpr.Replace(literalSafe, " ");
        foreach (Match m in FromJoinTarget.Matches(targetScan))
        {
            if (m.Groups[1].Success)
            {
                issues.Add("subquery in FROM/JOIN is not allowed");
            }
            else if (m.Groups[2].Success)
            {
                var targetView = RptRef.Match(m.Groups[2].Value).Groups[1].Value;
                if (!knownViews.Contains(targetView))
                    issues.Add($"unknown FROM/JOIN view rpt.{targetView}");
            }
            else if (m.Groups[3].Success)
            {
                issues.Add($"invalid FROM/JOIN target '{m.Groups[3].Value}'");
            }
        }

        // base-table, catalog, audit or system access (aliases of rpt views are allowed)
        var withoutRpt = RptRef.Replace(literalSafe, " ");
        foreach (Match m in BaseSchemaRef.Matches(withoutRpt))
            issues.Add($"references {Regex.Replace(m.Value, @"\s+", "")}");

        foreach (Match m in Param.Matches(text))
            if (!allowedParams.Contains(m.Groups[1].Value)) issues.Add($"parameter @{m.Groups[1].Value} is not allowed");

        return new GuardResult(issues.Count == 0, issues, views);
    }
}
