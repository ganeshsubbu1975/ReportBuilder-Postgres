namespace ReportBuilder.Core.Pipeline;

/// <summary>
/// Step 8. One generic engine. The checks come from catalog data (the metric's control areas) and from the shape of the result,
/// so a new report needs a catalog entry, not new code.
/// </summary>
public sealed class ResultValidator
{
    private readonly IReconciliationSource _recon;
    public ResultValidator(IReconciliationSource recon) => _recon = recon;

    public async Task<List<Finding>> ValidateAsync(MetricEntry metric, QueryResult result, string company, CancellationToken ct)
    {
        var f = new List<Finding>();
        if (result.Rows.Count == 0) f.Add(new("empty_result", Severity.Warning, "The report returned no rows for this company."));
        if (result.Truncated) f.Add(new("truncated", Severity.Warning, "The result was cut at the row limit. Narrow the question to see everything."));

        var valueIdx = Array.IndexOf(result.Columns, "value");
        if (valueIdx >= 0 && result.Rows.Any(r => r[valueIdx] is null))
            f.Add(new("null_measure", Severity.Warning, "Some rows have no value for the measure."));

        foreach (var area in metric.ControlAreas.Where(a => a.Length > 0))
        {
            var totals = await _recon.GetControlTotalsAsync(company, ct);
            var t = totals.FirstOrDefault(x => x.Area.Equals(area, StringComparison.OrdinalIgnoreCase));
            if (t is null) f.Add(new("control_unknown", Severity.Warning, $"No control total exists for {area}."));
            else if (Math.Abs(t.Difference) >= 0.005m)
                f.Add(new("control_break", Severity.Failure, $"{area} does not agree with the general ledger: ledger {t.GlBalance:N2}, sub-ledger {t.SubledgerBalance:N2}, difference {t.Difference:N2}."));
            else
                f.Add(new("control_ok", Severity.Info, $"{area} agrees with the general ledger (difference 0.00)."));
        }
        return f;
    }

    public static string LabelFor(MetricEntry m, IEnumerable<Finding> findings) =>
        m.IsCertified && findings.All(x => x.Severity != Severity.Failure) ? "Certified" : "Unverified";
}
