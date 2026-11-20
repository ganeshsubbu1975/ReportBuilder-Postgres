using System.Text.RegularExpressions;

namespace ReportBuilder.Core.Pipeline;

public sealed record MapResult(IReadOnlyList<MetricEntry> Candidates, string[] AmbiguityOptions);

/// <summary>
/// Step 2, the semantic layer. Finds catalog metrics for the user's words using synonyms, names and descriptions.
/// If one term is a synonym of certified metrics in different views, it asks instead of guessing.
/// </summary>
public sealed class CatalogMapper
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        { "the","a","an","of","for","by","show","me","what","is","are","to","in","on","and","my","our","all","list","total","please","company","with","from","how","many","much" };

    public MapResult Find(string question, IEnumerable<string> terms, IReadOnlyList<MetricEntry> catalog, bool allowUnverified)
    {
        var pool = catalog.Where(m => m.ReviewStatus != "REJECTED" && (allowUnverified || m.IsCertified)).ToList();
        var q = question.ToLowerInvariant();
        var tokens = Tokens(q).Concat(terms.SelectMany(t => Tokens(t))).ToHashSet();

        var scored = pool.Select(m =>
        {
            double score = 0;
            foreach (var s in m.Synonyms) if (s.Length > 0 && q.Contains(s.ToLowerInvariant())) score += 3;
            if (q.Contains(m.Name.ToLowerInvariant())) score += 4;
            var nameTokens = Tokens(m.Name + " " + m.Description).ToHashSet();
            score += tokens.Count(t => nameTokens.Contains(t));
            foreach (var d in m.Dimensions) if (tokens.Contains(d.ToLowerInvariant().Replace("_", " "))) score += 0.5;
            if (m.IsCertified) score += 0.5;
            return (m, score);
        }).Where(x => x.score > 0.5).OrderByDescending(x => x.score).Take(5).ToList();

        // ambiguity: one term, several certified metrics in different views, and the user did not name one of them
        var options = new List<string>();
        foreach (var term in terms.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct())
        {
            var hits = pool.Where(m => m.IsCertified && m.Synonyms.Any(s => s.Trim().ToLowerInvariant() == term)).ToList();
            if (hits.Select(h => h.ViewName).Distinct().Count() < 2) continue;
            if (hits.Any(h => q.Contains(h.Name.ToLowerInvariant()))) continue;
            options.AddRange(hits.Select(h => h.Name));
        }
        return new MapResult(scored.Select(x => x.m).ToList(), options.Distinct().ToArray());
    }

    private static IEnumerable<string> Tokens(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9]+").Select(m => m.Value).Where(t => t.Length > 1 && !Stop.Contains(t));
}
