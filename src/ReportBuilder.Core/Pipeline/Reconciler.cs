using System.Globalization;

namespace ReportBuilder.Core.Pipeline;

public sealed record AreaExplanation(string Area, decimal GlBalance, decimal SubledgerBalance, decimal Difference, string Explanation, bool Resolved);

/// <summary>
/// Answers "why do these numbers not agree". Reads the control totals, then looks for manual journals posted straight to the
/// control account, which is the usual reason a ledger and its sub-ledger differ.
/// </summary>
public sealed class Reconciler
{
    // area -> (GL accounts, +1 when the ledger balance is debit-positive and -1 when it is credit-positive)
    private static readonly Dictionary<string, (string[] Accounts, int Sign)> Map = new()
    {
        ["AR"] = (new[] { "1210" }, 1), ["AP"] = (new[] { "2100" }, -1), ["INVENTORY"] = (new[] { "1300", "1310" }, 1),
        ["GRNI"] = (new[] { "2150" }, -1), ["FIXED_ASSETS_COST"] = (new[] { "1500" }, 1), ["ACCUM_DEPRECIATION"] = (new[] { "1510" }, -1)
    };
    public static readonly string[] AreaCodes = Map.Keys.ToArray();

    private readonly IReconciliationSource _src;
    public Reconciler(IReconciliationSource src) => _src = src;

    public async Task<(decimal TrialBalanceNet, List<AreaExplanation> Areas)> ExplainAsync(string company, IReadOnlyCollection<string> areas, CancellationToken ct)
    {
        var wanted = (areas.Count == 0 ? AreaCodes : areas.Where(Map.ContainsKey).ToArray());
        var totals = await _src.GetControlTotalsAsync(company, ct);
        var tb = await _src.GetTrialBalanceNetAsync(company, ct);
        var list = new List<AreaExplanation>();
        foreach (var area in wanted)
        {
            var t = totals.FirstOrDefault(x => x.Area.Equals(area, StringComparison.OrdinalIgnoreCase));
            if (t is null) continue;
            if (Math.Abs(t.Difference) < 0.005m)
            {
                list.Add(new(area, t.GlBalance, t.SubledgerBalance, 0m, "Agrees with the general ledger.", true));
                continue;
            }
            var (accounts, sign) = Map[area];
            var postings = await _src.GetManualPostingsAsync(company, accounts, ct);
            var explained = postings.Sum(p => p.Net) * sign;
            var detail = string.Join("; ", postings.Take(5).Select(p =>
                $"{p.JournalNo} on {p.PostingDate:yyyy-MM-dd} for {(p.Net * sign).ToString("N2", CultureInfo.InvariantCulture)} ({p.Description})"));
            if (postings.Count > 0 && Math.Abs(t.Difference - explained) < 0.005m)
                list.Add(new(area, t.GlBalance, t.SubledgerBalance, t.Difference,
                    $"Fully explained by {postings.Count} manual journal(s) posted straight to the control account: {detail}. The sub-ledger has no matching document.", true));
            else if (postings.Count > 0)
                list.Add(new(area, t.GlBalance, t.SubledgerBalance, t.Difference,
                    $"Manual journals explain {explained.ToString("N2", CultureInfo.InvariantCulture)} of the {t.Difference.ToString("N2", CultureInfo.InvariantCulture)} difference ({detail}). " +
                    $"The remaining {(t.Difference - explained).ToString("N2", CultureInfo.InvariantCulture)} needs investigation.", false));
            else
                list.Add(new(area, t.GlBalance, t.SubledgerBalance, t.Difference,
                    "No manual journals on the control account. Check posting dates and documents that posted to only one side.", false));
        }
        return (tb, list);
    }
}
