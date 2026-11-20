using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReportBuilder.Core;

namespace ReportBuilder.Web;

public sealed record Kpi(string Label, string Value, string Sub, string Tone = "");

/// <summary>Works out how to draw a generic query result: label columns, measure columns, a total column and the stacked chart series.</summary>
public sealed class ReportView
{
    public QueryResult Result { get; }
    public List<int> LabelCols { get; } = new();
    public List<int> NumCols { get; } = new();
    public int? TotalCol { get; }
    public List<int> StackCols { get; } = new();
    public decimal Scale { get; }
    /// <summary>False when the measures are not parts of one total (a reconciliation shows GL, sub-ledger and difference side by side).</summary>
    public bool Additive { get; }
    /// <summary>Label columns that hold a point in time (year, period, month, date), earliest unit first.</summary>
    public List<int> TimeCols { get; } = new();
    public List<int> ShortLabelCols { get; } = new();
    public HashSet<int> LongTextCols { get; } = new();

    public ReportView(QueryResult r, bool additive = true)
    {
        Result = r;
        Additive = additive;
        for (var c = 0; c < r.Columns.Length; c++)
        {
            var vals = r.Rows.Select(row => row[c]).Where(v => v is not null).ToList();
            if (vals.Count > 0 && vals.All(IsNum) && !DimensionName.IsMatch(r.Columns[c])) NumCols.Add(c); else LabelCols.Add(c);
        }
        foreach (var c in LabelCols)
        {
            var longest = r.Rows.Select(row => Text(row[c]).Length).DefaultIfEmpty(0).Max();
            if (longest > 40) LongTextCols.Add(c); else ShortLabelCols.Add(c);
        }
        if (ShortLabelCols.Count == 0 && LabelCols.Count > 0) ShortLabelCols.Add(LabelCols[0]);
        TimeCols = LabelCols.Where(c => TimeName.IsMatch(r.Columns[c]) || (r.Rows.Count > 0 && r.Rows.All(row => row[c] is DateTime or null)))
            .OrderBy(c => TimeRank(r.Columns[c])).ToList();
        TotalCol = NumCols.Cast<int?>().LastOrDefault(c => r.Columns[c!.Value].Contains("total", StringComparison.OrdinalIgnoreCase));
        StackCols = NumCols.Where(c => c != TotalCol).ToList();
        var max = NumCols.SelectMany(c => r.Rows.Select(row => Math.Abs(Num(row[c])))).DefaultIfEmpty(0m).Max();
        Scale = additive && max >= 1_000_000m ? 1_000_000m : 1m;
    }

    /// <summary>One measure that has both gains and losses (a profit and loss, a variance). Drawn as bars either side of zero.</summary>
    public bool IsSigned => Additive && StackCols.Count == 1 && Result.Rows.Count >= 2 && Result.Rows.Any(r => Num(r[StackCols[0]]) < 0);
    private static int TimeRank(string n) => n.Contains("year", StringComparison.OrdinalIgnoreCase) ? 0 : n.Contains("quarter", StringComparison.OrdinalIgnoreCase) ? 1
        : n.Contains("period", StringComparison.OrdinalIgnoreCase) || n.Contains("month", StringComparison.OrdinalIgnoreCase) || n.Contains("week", StringComparison.OrdinalIgnoreCase) ? 2 : 3;

    public sealed record TrendSeries(string Name, decimal?[] Values, string Color);
    public sealed record TrendModel(List<string> XLabels, List<TrendSeries> Series, decimal[] Totals);

    private static readonly string[] Palette = { "#1d4ed8", "#0f7a55", "#c2570c", "#7c3aed", "#be185d", "#0891b2" };

    private string SortKey(object?[] row) => string.Join("|", TimeCols.Select(c => row[c] switch
    {
        DateTime t => t.ToString("yyyyMMdd"),
        null => "",
        var v when IsNum(v) => Num(v).ToString("0000000000.##", CultureInfo.InvariantCulture),
        var v => v.ToString()
    }));

    private string TimeLabel(object?[] row) => string.Join(" ", TimeCols.Select(c =>
    {
        var name = Result.Columns[c];
        var t = Text(row[c]);
        return name.Contains("period", StringComparison.OrdinalIgnoreCase) ? "P" + t : name.Contains("month", StringComparison.OrdinalIgnoreCase) && t.Length <= 2 ? "M" + t : t;
    }));

    /// <summary>A line chart needs one measure, a time column and at least two points in time.</summary>
    public TrendModel? Trend()
    {
        if (!Additive || StackCols.Count != 1 || TimeCols.Count == 0) return null;
        var m = StackCols[0];
        var points = Result.Rows.GroupBy(SortKey).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (points.Count < 2) return null;
        var cat = ShortLabelCols.Where(c => !TimeCols.Contains(c)).Select(c => (int?)c).FirstOrDefault();
        var names = cat is { } cc ? Result.Rows.Select(r => Text(r[cc])).Distinct().ToList() : new List<string>();
        var series = new List<TrendSeries>();
        if (cat is { } cx && names.Count is >= 2 and <= 6)
        {
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                decimal?[] vals = points.Select(g => { var hit = g.Where(r => Text(r[cx]) == name).ToList(); return hit.Count == 0 ? (decimal?)null : hit.Sum(r => Num(r[m])); }).ToArray();
                series.Add(new TrendSeries(name, vals, Palette[i]));
            }
        }
        else series.Add(new TrendSeries(Header(m), points.Select(g => (decimal?)g.Sum(r => Num(r[m]))).ToArray(), Palette[0]));
        var totals = points.Select(g => g.Sum(r => Num(r[m]))).ToArray();
        return new TrendModel(points.Select(g => TimeLabel(g.First())).ToList(), series, totals);
    }

    public bool IsTrend => Trend() is not null;

    /// <summary>A line through the rows in the order returned, one line per measure, for a line chart requested on data with no time column.</summary>
    public TrendModel? CategoryLine()
    {
        if (!CanChart || Result.Rows.Count < 2) return null;
        var series = StackCols.Take(Palette.Length).Select((c, i) =>
            new TrendSeries(Header(c), Result.Rows.Select(r => (decimal?)Num(r[c])).ToArray(), Palette[i])).ToList();
        return new TrendModel(Result.Rows.Select(ChartLabel).ToList(), series, Result.Rows.Select(RowTotal).ToArray());
    }

    /// <summary>Axis bounds and step that include zero and land on round numbers.</summary>
    public static (decimal Lo, decimal Hi, decimal Step) NiceAxis(decimal min, decimal max, int ticks = 4)
    {
        min = Math.Min(min, 0m); max = Math.Max(max, 0m);
        if (max == min) max = min + 1m;
        var raw = (double)(max - min) / ticks;
        var pow = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / pow;
        var step = (decimal)((f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * pow);
        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
    }

    public static string Compact(decimal v)
    {
        var a = Math.Abs(v);
        var sign = v < 0 ? "-" : "";
        return a >= 1_000_000m ? sign + (a / 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "M"
             : a >= 1_000m ? sign + (a / 1_000m).ToString("0.#", CultureInfo.InvariantCulture) + "K"
             : v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public string ScaleNote => Scale == 1_000_000m ? "Amounts in millions" : "";
    public bool CanChart => StackCols.Count >= 1 && LabelCols.Count >= 1 && Result.Rows.Count >= 1;
    public int? HighlightCol => Additive && StackCols.Count >= 3 ? StackCols[^1] : null;

    private static readonly Regex DimensionName = new(@"^(fiscal_)?year$|^period$|^month$|^quarter$|^week$|(^|_)id$|(^|_)code$|(^|_)no$", RegexOptions.IgnoreCase);
    private static readonly Regex TimeName = new(@"(^|_)(year|quarter|period|month|week|date|day)$", RegexOptions.IgnoreCase);

    public static bool IsNum(object? v) => v is decimal or double or float or int or long or short or byte;
    public static decimal Num(object? v) => v switch { decimal d => d, double d => (decimal)d, float f => (decimal)f, int i => i, long l => l, short s => s, byte b => b, _ => 0m };

    public string Label(object?[] row) => string.Join(" / ", LabelCols.Select(c => Text(row[c])));
    /// <summary>Short label for chart rows: long explanation text stays in the table.</summary>
    public string ChartLabel(object?[] row) => string.Join(" / ", ShortLabelCols.Select(c => Text(row[c])));
    public bool ShowTotal => Additive && (StackCols.Count > 1 || TotalCol is not null);
    public decimal RowTotal(object?[] row) => TotalCol is { } t ? Num(row[t]) : StackCols.Sum(c => Num(row[c]));
    public decimal ColTotal(int col) => Result.Rows.Sum(r => Num(r[col]));
    public decimal GrandTotal => Result.Rows.Sum(RowTotal);

    public string Fmt(decimal v) => (v / Scale).ToString(Additive ? "N1" : "N2", CultureInfo.InvariantCulture);
    public static string Text(object? v) => v switch { null => "", DateTime t => t.ToString("yyyy-MM-dd"), decimal d => d.ToString("N2", CultureInfo.InvariantCulture), _ => v.ToString() ?? "" };

    public string Header(int col) => Humanize(Result.Columns[col]);

    public static string Humanize(string name)
    {
        var m = Regex.Match(name, @"^d?(\d+)_(\d+)$");
        if (m.Success) return $"{m.Groups[1].Value}-{m.Groups[2].Value}";
        m = Regex.Match(name, @"^d?(\d+)_?(plus|p)$", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value + "+";
        var words = string.Join(" ", name.Replace('_', ' ').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Equals("gl", StringComparison.OrdinalIgnoreCase) || w.Equals("ar", StringComparison.OrdinalIgnoreCase) || w.Equals("ap", StringComparison.OrdinalIgnoreCase) ? w.ToUpperInvariant() : w));
        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }

    public List<Kpi> Kpis(IReadOnlyList<Finding> findings, string[] controlAreas)
    {
        var list = new List<Kpi>();
        var rows = Result.Rows;
        if (NumCols.Count == 0 || rows.Count == 0) return list;
        if (Trend() is { } tr)
        {
            var t = tr.Totals; var last = t[^1]; var prev = t[^2];
            var peak = Array.IndexOf(t, t.Max());
            var change = prev == 0 ? (decimal?)null : (last - prev) / Math.Abs(prev) * 100m;
            list.Add(new Kpi("Latest (" + tr.XLabels[^1] + ")", Fmt(last), "Previous: " + Fmt(prev) + " (" + tr.XLabels[^2] + ")"));
            list.Add(new Kpi("Change vs previous", change is null ? "n/a" : (change >= 0 ? "+" : "") + change.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%", tr.XLabels[^2] + " to " + tr.XLabels[^1], change is null ? "" : change >= 0 ? "pos" : "neg"));
            list.Add(new Kpi("Peak", Fmt(t[peak]), tr.XLabels[peak]));
            list.Add(new Kpi("Total over period", Fmt(t.Sum()), $"{t.Length} points, average {Fmt(t.Average())}"));
            return list;
        }
        if (IsSigned)
        {
            var vals = rows.Select(r => Num(r[StackCols[0]])).ToList();
            var net = vals.Sum(); var inflow = vals.Where(v => v > 0).Sum(); var outflow = vals.Where(v => v < 0).Sum();
            var largest = rows.OrderByDescending(r => Math.Abs(Num(r[StackCols[0]]))).First();
            list.Add(new Kpi(net < 0 ? "Net result (loss)" : "Net result (profit)", Fmt(net), "Sum of all lines", net < 0 ? "neg" : "pos"));
            list.Add(new Kpi("Total in", Fmt(inflow), $"{vals.Count(v => v > 0)} positive {(vals.Count(v => v > 0) == 1 ? "line" : "lines")}", "pos"));
            list.Add(new Kpi("Total out", Fmt(outflow), $"{vals.Count(v => v < 0)} negative {(vals.Count(v => v < 0) == 1 ? "line" : "lines")}", "neg"));
            list.Add(new Kpi("Result vs total in", inflow == 0 ? "n/a" : $"{net / inflow * 100m:0.0}%", "Net result as a share of positive lines, largest line: " + ChartLabel(largest), net < 0 ? "neg" : "pos"));
            return list;
        }
        var total = GrandTotal;
        var unit = Scale == 1_000_000m ? "millions, " : "";
        list.Add(new Kpi("Total", Fmt(total), $"{unit}{rows.Count} {(rows.Count == 1 ? "row" : "rows")}"));

        var focus = HighlightCol ?? StackCols.LastOrDefault(-1);
        if (focus >= 0 && StackCols.Count > 1)
        {
            var part = ColTotal(focus);
            var pct = total == 0 ? 0m : part / total * 100m;
            list.Add(new Kpi("Over " + Header(focus), Fmt(part), $"{pct:0.0}% of total"));
            var top = rows.OrderByDescending(r => Num(r[focus])).First();
            list.Add(new Kpi($"Largest {Header(focus)}", Label(top), $"{Fmt(Num(top[focus]))}"));
        }
        else if (rows.Count > 1)
        {
            var top = rows.OrderByDescending(RowTotal).First();
            list.Add(new Kpi("Largest", Label(top), Fmt(RowTotal(top))));
        }

        var breaks = findings.FirstOrDefault(f => f.Code == "control_break");
        list.Add(controlAreas.Length == 0
            ? new Kpi("Control total check", "n/a", "No ledger control for this metric")
            : breaks is not null ? new Kpi("Control total check", "Break", "Does not agree with the general ledger")
            : findings.Any(f => f.Code == "control_ok") ? new Kpi("Control total check", "0.00", "Difference to " + string.Join(", ", controlAreas) + " in the ledger")
            : new Kpi("Control total check", "Unknown", "No control total was available"));
        return list;
    }

    public string Csv()
    {
        static string Esc(string s) => s.Contains('"') || s.Contains(',') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Result.Columns.Select(Esc)));
        foreach (var row in Result.Rows)
            sb.AppendLine(string.Join(",", row.Select(v => Esc(v switch { null => "", decimal d => d.ToString(CultureInfo.InvariantCulture), DateTime t => t.ToString("yyyy-MM-dd"), _ => v.ToString() ?? "" }))));
        return sb.ToString();
    }
}

public static class UiText
{
    /// <summary>"CONTOSO\RB-Finance-1000" becomes "Finance 1000".</summary>
    public static string RoleLabel(IEnumerable<string> roles)
    {
        var r = roles.FirstOrDefault();
        if (r is null) return "No role";
        var name = r[(r.LastIndexOf('\\') + 1)..];
        if (name.StartsWith("RB-", StringComparison.OrdinalIgnoreCase)) name = name[3..];
        return name.Replace('-', ' ');
    }

    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var day = local.Date == DateTime.Now.Date ? "Today" : local.Date == DateTime.Now.Date.AddDays(-1) ? "Yesterday" : local.ToString("d MMM");
        return $"{day} {local:HH:mm}";
    }

    public static string Short(string hash) => hash.Length > 10 ? hash[..6] + "…" + hash[^4..] : hash;
}
