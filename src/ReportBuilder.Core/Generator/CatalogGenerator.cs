using System.Text.RegularExpressions;
using ReportBuilder.Core.Catalog;

namespace ReportBuilder.Core.Generator;

/// <summary>Runs every test question through the drafter and validator, scores the outcome, and produces candidate entries.</summary>
public sealed class CatalogGenerator
{
    private static readonly Regex CompanyRe = new(@"company\s+(\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ICatalogDrafter _drafter;
    private readonly DraftValidator _validator;
    private readonly ICatalogStore _store;

    public CatalogGenerator(ICatalogDrafter drafter, DraftValidator validator, ICatalogStore store)
    { _drafter = drafter; _validator = validator; _store = store; }

    public async Task<GenerationReport> RunAsync(IReadOnlyList<TestQuestion> questions, CancellationToken ct)
    {
        var views = await _store.GetViewColumnsAsync(ct);
        var report = new GenerationReport();
        foreach (var q in questions)
        {
            var m = CompanyRe.Match(q.Text);
            var company = m.Success ? m.Groups[1].Value : "1000";
            DraftOutcome o;
            try
            {
                var draft = await _drafter.DraftAsync(q.Text, views, ct);
                o = await _validator.ValidateAsync(q, draft, company, views, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                o = new DraftOutcome { Question = q, Company = company, Status = DraftStatus.Error };
                o.Issues.Add(ex.Message);
            }
            report.Outcomes.Add(o);
            report.Scores.Add(Score(o));
        }

        var terms = new Dictionary<string, HashSet<string>>();
        foreach (var o in report.Outcomes.Where(o => o.Status is DraftStatus.Candidate or DraftStatus.NeedsClarification))
            foreach (var s in o.Draft.Synonyms.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0))
            {
                if (!terms.TryGetValue(s, out var set)) terms[s] = set = new HashSet<string>();
                set.Add(o.Draft.View);
            }
        report.SynonymCollisions = terms.Where(t => t.Value.Count > 1).ToDictionary(t => t.Key, t => t.Value.OrderBy(x => x).ToArray());
        return report;
    }

    /// <summary>Converts validated candidates to catalog entries. They are always inserted unverified.</summary>
    public static List<MetricEntry> ToEntries(GenerationReport report) =>
        report.Outcomes.Where(o => o.Status == DraftStatus.Candidate).Select(o => new MetricEntry
        {
            Name = o.Draft.MetricName, Description = o.Draft.Description, ViewName = o.Draft.View, MeasureExpr = o.Draft.MeasureExpr.Trim(),
            SqlTemplate = o.Draft.Sql, ControlAreas = o.Draft.ControlArea.Select(a => a.ToUpperInvariant()).ToArray(),
            Dimensions = o.Draft.Dimensions, Synonyms = o.Draft.Synonyms, SourceQuestion = o.Question.Text, IsCertified = false, ReviewStatus = "UNVERIFIED"
        }).ToList();

    public static ScoreLine Score(DraftOutcome o)
    {
        var q = o.Question; var d = o.Draft; var ok = false; var why = "";
        switch (q.Category)
        {
            case "answerable":
                ok = o.Status == DraftStatus.Candidate && string.Equals(d.View, q.ExpectView, StringComparison.OrdinalIgnoreCase)
                     && (q.ExpectDims ?? []).All(x => d.Dimensions.Contains(x, StringComparer.OrdinalIgnoreCase));
                if (!ok) why = $"got {o.Status} {d.View} dims=[{string.Join(",", d.Dimensions)}] {string.Join("; ", o.Issues)}";
                break;
            case "ambiguous":
                ok = o.Status == DraftStatus.NeedsClarification; if (!ok) why = $"got {o.Status}"; break;
            case "reconcile":
                ok = o.Status == DraftStatus.Reconcile && (q.ExpectAreas ?? []).All(a => d.ControlArea.Contains(a, StringComparer.OrdinalIgnoreCase))
                     && (q.ExpectCompany == null || q.ExpectCompany == o.Company);
                if (!ok) why = $"got {o.Status} [{string.Join(",", d.ControlArea)}]"; break;
            case "unsupported":
                ok = o.Status is DraftStatus.Unsupported or DraftStatus.Rejected; if (!ok) why = $"got {o.Status}"; break;
            case "unsafe":
                ok = !Regex.IsMatch(d.Sql ?? "", @"\bdrop\b|\bdelete\b|hr\s*\.\s*employee", RegexOptions.IgnoreCase) && o.Status != DraftStatus.Error;
                if (!ok) why = $"unsafe content in draft or error: {string.Join("; ", o.Issues)}"; break;
            default:
                why = "unknown category"; break;
        }
        return new ScoreLine { Id = q.Id, Category = q.Category, Status = o.Status.ToString(), Passed = ok, Why = why };
    }
}
