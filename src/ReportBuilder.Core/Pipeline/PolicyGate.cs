using Microsoft.Extensions.Options;

namespace ReportBuilder.Core.Pipeline;

public sealed record EffectivePolicy(IReadOnlySet<string> Companies, IReadOnlySet<string> Views, int MaxRows, bool CatalogOwner)
{
    public bool AllCompanies => Companies.Contains("*");
    public bool AllViews => Views.Contains("*");
    public bool AllowsCompany(string c) => AllCompanies || Companies.Contains(c);
    public bool AllowsView(string v) => AllViews || Views.Contains(v, StringComparer.OrdinalIgnoreCase);
}

public sealed record PolicyDecision(bool Allowed, string Reason);

/// <summary>Step 5. Deterministic access rules. Decides from the user's roles and the compiled query, never from model output.</summary>
public sealed class PolicyGate
{
    private readonly PolicyOptions _o;
    public PolicyGate(IOptions<PolicyOptions> options) => _o = options.Value;

    public EffectivePolicy Resolve(UserContext user)
    {
        var companies = new HashSet<string>();
        var views = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owner = false;
        int? max = null;
        foreach (var role in user.Roles)
        {
            if (!_o.Roles.TryGetValue(role, out var p)) continue;
            foreach (var c in p.Companies) companies.Add(c);
            foreach (var v in p.Views) views.Add(v);
            owner |= p.CatalogOwner;
            if (p.MaxRows.HasValue) max = Math.Max(max ?? 0, p.MaxRows.Value);
        }
        return new EffectivePolicy(companies, views, max ?? _o.DefaultMaxRows, owner);
    }

    public IReadOnlyList<string> VisibleCompanies(EffectivePolicy p) =>
        p.AllCompanies ? _o.KnownCompanies : _o.KnownCompanies.Where(p.AllowsCompany).ToArray();

    public PolicyDecision Evaluate(EffectivePolicy policy, string company, CompiledQuery query, IReadOnlySet<string> knownViews)
    {
        if (!policy.AllowsCompany(company)) return new(false, $"You do not have access to company {company}.");
        if (!policy.AllowsView(query.ViewName)) return new(false, $"You do not have access to the data behind this report (rpt.{query.ViewName}).");

        var blocked = _o.BlockedColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hit = query.Columns.FirstOrDefault(blocked.Contains);
        if (hit != null) return new(false, $"Column {hit} is restricted.");

        var guard = SqlGuard.Check(query.Sql, knownViews, new HashSet<string>(query.Parameters.Keys, StringComparer.OrdinalIgnoreCase));
        if (!guard.Ok) return new(false, "The generated SQL failed the safety check: " + string.Join("; ", guard.Issues));
        foreach (var b in blocked)
            if (System.Text.RegularExpressions.Regex.IsMatch(query.Sql, $@"\b{System.Text.RegularExpressions.Regex.Escape(b)}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return new(false, $"Column {b} is restricted.");
        return new(true, "");
    }
}
