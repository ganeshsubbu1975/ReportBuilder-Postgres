using Npgsql;
using ReportBuilder.Core.Pipeline;

namespace ReportBuilder.Core.Generator;

/// <summary>Deterministic checks on a model draft. Runs the draft on the real database (read-only) before anyone reviews it.</summary>
public sealed class DraftValidator
{
    private readonly IReconciliationSource _recon;
    private readonly IDraftPreview _preview;
    public DraftValidator(IReconciliationSource recon, IDraftPreview preview) { _recon = recon; _preview = preview; }

    public async Task<DraftOutcome> ValidateAsync(TestQuestion q, DraftDto d, string company, IReadOnlyDictionary<string, string[]> views, CancellationToken ct)
    {
        var o = new DraftOutcome { Question = q, Draft = d, Company = company };
        if (string.Equals(d.Intent, "unsupported", StringComparison.OrdinalIgnoreCase))
        {
            o.Status = DraftStatus.Unsupported; o.Issues.Add(string.IsNullOrWhiteSpace(d.Rationale) ? "no matching data" : d.Rationale); return o;
        }

        d.Sql = (d.Sql ?? "").Trim().TrimEnd(';').Trim();
        var knownViews = views.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var guard = SqlGuard.Check(d.Sql, knownViews, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "company" });
        o.Issues.AddRange(guard.Issues);

        if (!views.TryGetValue(d.View ?? "", out var cols)) o.Issues.Add($"unknown view '{d.View}'");
        else
        {
            foreach (var dim in d.Dimensions)
                if (!QueryCompiler.IsIdentifier(dim) || !cols.Contains(dim, StringComparer.OrdinalIgnoreCase)) o.Issues.Add($"dimension {dim} not in {d.View}");
            if (!cols.Contains("company_code", StringComparer.OrdinalIgnoreCase)) o.Issues.Add($"{d.View} has no company_code column");
        }
        if (!string.Equals(d.Intent, "reconcile", StringComparison.OrdinalIgnoreCase) && !QueryCompiler.IsSimpleMeasure(d.MeasureExpr))
            o.Issues.Add("measure_expr is not a simple aggregate such as SUM(column)");
        if (o.Issues.Count > 0) { o.Status = DraftStatus.Rejected; return o; }

        try
        {
            o.Preview = await _preview.PreviewAsync(d.Sql, company, ct);
            if (o.Preview.RowCount == 0) o.Issues.Add("returns no rows for the test company");
        }
        catch (PostgresException ex)
        {
            o.Status = DraftStatus.Rejected; o.Issues.Add("SQL failed: " + ex.Message); return o;
        }

        if (string.Equals(d.Intent, "reconcile", StringComparison.OrdinalIgnoreCase))
        {
            if (d.ControlArea.Length == 0) { o.Status = DraftStatus.Rejected; o.Issues.Add("reconcile intent without control_area"); return o; }
            var bad = d.ControlArea.Where(a => !Reconciler.AreaCodes.Contains(a.ToUpperInvariant())).ToArray();
            if (bad.Length > 0) { o.Status = DraftStatus.Rejected; o.Issues.Add("unknown control area: " + string.Join(", ", bad)); return o; }
            o.Status = DraftStatus.Reconcile; return o;
        }
        if (d.NeedsClarification) { o.Status = DraftStatus.NeedsClarification; return o; }

        if (d.ControlArea.Length > 0)
        {
            var totals = await _recon.GetControlTotalsAsync(company, ct);
            foreach (var a in d.ControlArea)
            {
                var t = totals.FirstOrDefault(x => x.Area.Equals(a, StringComparison.OrdinalIgnoreCase));
                if (t is null) o.Issues.Add($"control area {a} unknown");
                else if (Math.Abs(t.Difference) >= 0.005m) o.Issues.Add($"control total {a} differs by {t.Difference:N2}");
            }
        }
        o.Status = DraftStatus.Candidate;
        return o;
    }
}
