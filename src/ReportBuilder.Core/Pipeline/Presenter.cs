using System.Globalization;
using System.Text.Json;
using ReportBuilder.Core.Llm;

namespace ReportBuilder.Core.Pipeline;

/// <summary>
/// Step 9. Writes the plain-language summary. The model receives validated facts (names, counts, one total, findings),
/// never the rows. If the model is unavailable, a fixed template is used.
/// </summary>
public sealed class Presenter
{
    private readonly ILlmClient _llm;
    public Presenter(ILlmClient llm) => _llm = llm;

    public async Task<string> SummarizeReportAsync(string question, MetricEntry metric, QueryResult result, string label, IReadOnlyList<Finding> findings, CancellationToken ct)
    {
        var total = TotalOf(result);
        var facts = new
        {
            metric = metric.Name, label, row_count = result.Rows.Count, truncated = result.Truncated,
            group_by = result.Columns.Where(c => c != "value").ToArray(), total = total?.ToString("N2", CultureInfo.InvariantCulture),
            findings = findings.Where(f => f.Severity != Severity.Info).Select(f => f.Message).ToArray()
        };
        try
        {
            var system = "Write two or three plain sentences summarising a report for a business user. Use only the facts given. Do not invent numbers or causes. " +
                         "If findings are listed, state them. Say whether the result is Certified or Unverified.";
            var text = await _llm.CompleteTextAsync(ModelRole.Presenter, system, $"Question: {question}\nFacts: {JsonSerializer.Serialize(facts)}", ct);
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* fall back to the template below */ }
        return Fallback(metric, result, label, total, findings);
    }

    public static string Fallback(MetricEntry metric, QueryResult result, string label, decimal? total, IReadOnlyList<Finding> findings)
    {
        var s = $"{metric.Name}: {result.Rows.Count} row(s)" + (total.HasValue ? $", total {total.Value.ToString("N2", CultureInfo.InvariantCulture)}" : "") + $". Result is {label}.";
        var issues = findings.Where(f => f.Severity != Severity.Info).Select(f => f.Message).ToArray();
        return issues.Length == 0 ? s : s + " " + string.Join(" ", issues);
    }

    public async Task<string> SummarizeReconciliationAsync(string question, string company, decimal tbNet, IReadOnlyList<AreaExplanation> areas, CancellationToken ct)
    {
        var fallback = Reconciliation(company, tbNet, areas);
        try
        {
            var facts = new
            {
                company, trial_balance_net = tbNet.ToString("N2", CultureInfo.InvariantCulture),
                areas = areas.Select(a => new { a.Area, difference = a.Difference.ToString("N2", CultureInfo.InvariantCulture), a.Explanation, a.Resolved })
            };
            var system = "Explain a ledger reconciliation to a finance user in at most five sentences. Use only the facts given. Do not invent causes or numbers.";
            var text = await _llm.CompleteTextAsync(ModelRole.Presenter, system, $"Question: {question}\nFacts: {JsonSerializer.Serialize(facts)}", ct);
            return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return fallback; }
    }

    public static string Reconciliation(string company, decimal tbNet, IReadOnlyList<AreaExplanation> areas)
    {
        var parts = new List<string> { tbNet == 0m ? $"The trial balance for company {company} balances (net 0.00)." : $"The trial balance for company {company} is out by {tbNet:N2}." };
        foreach (var a in areas) parts.Add($"{a.Area}: {a.Explanation}");
        return string.Join(" ", parts);
    }

    private static decimal? TotalOf(QueryResult r)
    {
        var i = Array.IndexOf(r.Columns, "value");
        if (i < 0 || r.Rows.Count == 0) return null;
        decimal sum = 0;
        foreach (var row in r.Rows)
        {
            if (row[i] is null) continue;
            try { sum += Convert.ToDecimal(row[i], CultureInfo.InvariantCulture); } catch { return null; }
        }
        return sum;
    }
}
