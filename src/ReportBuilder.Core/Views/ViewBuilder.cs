using System.Text;
using System.Text.RegularExpressions;
using ReportBuilder.Core.Catalog;

namespace ReportBuilder.Core.Views;

public sealed class JoinSpec
{
    public string Source { get; set; } = "";       // schema.Table
    public string Type { get; set; } = "INNER";    // INNER or LEFT
    public int LeftSource { get; set; }            // index into the sources so far: 0 is the base table
    public string LeftColumn { get; set; } = "";
    public string RightColumn { get; set; } = "";  // column of Source
}

public sealed class ViewColumnSpec
{
    public int Source { get; set; }                // 0 is the base table, 1 is the first join, ...
    public string Column { get; set; } = "";
    public string Alias { get; set; } = "";
}

/// <summary>What a catalog owner fills in on the New view screen. Tables and columns are picked from lists, nothing is typed as SQL.</summary>
public sealed class ViewRequest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string BaseSource { get; set; } = "";
    public List<JoinSpec> Joins { get; set; } = new();
    public ViewColumnSpec Company { get; set; } = new();
    public List<ViewColumnSpec> Columns { get; set; } = new();
    /// <summary>True when this request changes an existing rpt view (approval then runs ALTER VIEW instead of CREATE VIEW).</summary>
    public bool ReplaceExisting { get; set; }
}

public sealed record ViewBuildResult(string Sql, IReadOnlyList<string> Issues)
{
    public bool Ok => Issues.Count == 0 && Sql.Length > 0;
}

/// <summary>
/// Turns a ViewRequest into one SELECT. Every table must be in the source list (the allowed ERP schemas), every column must exist on its table,
/// blocked columns are refused, and every identifier is matched against a strict pattern, so nothing the user types is ever placed in the SQL as free text.
/// </summary>
public static class ViewBuilder
{
    public static readonly string[] JoinTypes = ["INNER", "LEFT"];
    private static readonly Regex Ident = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex ViewName = new(@"^[a-z][a-z0-9_]{1,58}_v$", RegexOptions.Compiled);
    private const int MaxSources = 4, MaxColumns = 30;

    public static bool IsValidViewName(string name) => ViewName.IsMatch(name ?? "");

    public static ViewBuildResult Build(ViewRequest req, IReadOnlyDictionary<string, ViewColumn[]> sources, IEnumerable<string> blockedColumns, IEnumerable<string> existingViews)
    {
        var issues = new List<string>();
        var blocked = blockedColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = (req.Name ?? "").Trim();
        if (!ViewName.IsMatch(name)) issues.Add("Name the view in lower case letters, digits and underscores, ending in _v (for example payroll_by_cost_centre_v).");
        else if (req.ReplaceExisting)
        {
            if (!existingViews.Contains(name, StringComparer.OrdinalIgnoreCase)) issues.Add($"rpt.{name} no longer exists, so it cannot be edited.");
        }
        else if (existingViews.Contains(name, StringComparer.OrdinalIgnoreCase)) issues.Add($"A view called rpt.{name} already exists. Choose another name.");
        var description = (req.Description ?? "").Trim();
        if (description.Length == 0) issues.Add("Add a short description so the reviewer knows what the view is for.");
        if (description.Length > 400) issues.Add("The description is longer than 400 characters.");

        // sources, in order: 0 is the base table
        var order = new List<string>();
        if (!sources.ContainsKey(req.BaseSource ?? "")) return new("", issues.Append("Choose the main table from the list.").ToList());
        order.Add(req.BaseSource!);
        if ((req.Joins?.Count ?? 0) + 1 > MaxSources) issues.Add($"Use at most {MaxSources} tables in one view.");

        var sql = new StringBuilder();
        ViewColumn? Find(int src, string col) => src >= 0 && src < order.Count ? sources[order[src]].FirstOrDefault(c => c.Name.Equals(col, StringComparison.OrdinalIgnoreCase)) : null;
        string Ref(int src, ViewColumn c) => $"t{src}.\"{c.Name}\"";

        var from = new StringBuilder($"{Quote(order[0])} t0");
        foreach (var j in req.Joins ?? new())
        {
            if (!sources.ContainsKey(j.Source ?? "")) { issues.Add($"Table {j.Source} is not available."); continue; }
            var type = JoinTypes.FirstOrDefault(t => t.Equals(j.Type, StringComparison.OrdinalIgnoreCase));
            if (type is null) { issues.Add("Choose a join type from the list."); continue; }
            var left = Find(j.LeftSource, j.LeftColumn);
            order.Add(j.Source!);
            var idx = order.Count - 1;
            var right = Find(idx, j.RightColumn);
            if (left is null || right is null) { issues.Add($"Choose both columns to join {j.Source} on."); continue; }
            if (blocked.Contains(left.Name) || blocked.Contains(right.Name)) { issues.Add("A restricted column cannot be used to join."); continue; }
            if (!left.DataType.Equals(right.DataType, StringComparison.OrdinalIgnoreCase))
                { issues.Add($"{left.Name} ({left.DataType}) and {right.Name} ({right.DataType}) have different types and cannot be joined."); continue; }
            from.Append($" {type} JOIN {Quote(j.Source!)} t{idx} ON {Ref(j.LeftSource, left)} = {Ref(idx, right)}");
        }

        var company = Find(req.Company?.Source ?? -1, req.Company?.Column ?? "");
        if (company is null) issues.Add("Choose which column holds the company code. Reports are always restricted by company.");
        else if (blocked.Contains(company.Name)) issues.Add("The company column cannot be a restricted column.");

        var select = new List<string>();
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "company_code" };
        if (company is not null) select.Add($"{Ref(req.Company!.Source, company)} AS company_code");

        var cols = req.Columns ?? new();
        if (cols.Count == 0) issues.Add("Tick at least one column to include.");
        if (cols.Count > MaxColumns) issues.Add($"Choose at most {MaxColumns} columns.");
        foreach (var c in cols)
        {
            var col = Find(c.Source, c.Column);
            var alias = (c.Alias ?? "").Trim();
            if (alias.Length == 0 && col is not null)
            {
                // no alias typed: use the column name, and if another column already has it, prefix the table name (company_code -> company_company_code)
                alias = col.Name;
                if (aliases.Contains(alias))
                {
                    var table = order[c.Source].Split('.').Last().ToLowerInvariant();
                    var candidate = $"{table}_{col.Name}";
                    for (var n = 2; aliases.Contains(candidate); n++) candidate = $"{table}_{col.Name}_{n}";
                    alias = candidate;
                }
            }
            if (col is null) issues.Add($"Column {c.Column} is not on the chosen table.");
            else if (blocked.Contains(col.Name)) issues.Add($"Column {col.Name} is restricted and cannot be included.");
            else if (!Ident.IsMatch(alias)) issues.Add($"'{alias}' is not a valid column name. Use letters, digits and underscores.");
            else if (!aliases.Add(alias)) issues.Add($"The name {alias} is used twice. Rename one of the columns.");
            else select.Add($"{Ref(c.Source, col)} AS \"{alias}\"");
        }

        if (issues.Count > 0) return new("", issues);
        return new($"SELECT {string.Join(", ", select)} FROM {from}", issues);
    }

    private static string Quote(string source)
    {
        var parts = source.Split('.');
        return string.Join(".", parts.Select(p => $"\"{p}\""));
    }
}
