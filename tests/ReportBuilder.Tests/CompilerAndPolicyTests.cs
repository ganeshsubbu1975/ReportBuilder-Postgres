using Microsoft.Extensions.Options;
using ReportBuilder.Core;
using ReportBuilder.Core.Pipeline;
using Xunit;

namespace ReportBuilder.Tests;

public class QueryCompilerTests
{
    private readonly QueryCompiler _c = new();
    private static MetricEntry Aging => Setup.Metrics()[0];
    private static IReadOnlyDictionary<string, string[]> Views => Setup.Views;

    [Fact]
    public void Builds_a_grouped_parameterised_query()
    {
        var q = _c.Compile(Aging, new ReportPlan { MetricId = 1, Dimensions = ["region"] }, "1000", Views);
        Assert.Equal("SELECT region, SUM(total_open) AS value FROM rpt.ar_aging_v WHERE company_code = @company GROUP BY region ORDER BY region", q.Sql);
        Assert.Equal("1000", q.Parameters["company"]);
        Assert.Equal(new[] { "region", "value" }, q.Columns);
    }

    [Fact]
    public void Filter_values_are_always_parameters()
    {
        var plan = new ReportPlan { MetricId = 1, Dimensions = [], Filters = { new FilterSpec { Column = "region", Op = "=", Value = "N' OR 1=1 --" } } };
        var q = _c.Compile(Aging, plan, "1000", Views);
        Assert.DoesNotContain("OR 1=1", q.Sql);
        Assert.Contains("region = @f0", q.Sql);
        Assert.Equal("N' OR 1=1 --", q.Parameters["f0"]);
    }

    [Theory]
    [InlineData("region; DROP TABLE x")]
    [InlineData("salary")]
    [InlineData("d0_30")]          // exists in the view but is not an approved group-by column for this metric
    [InlineData("region, 1")]
    public void Rejects_dimensions_that_are_not_approved(string dim)
        => Assert.Throws<PlanRejectedException>(() => _c.Compile(Aging, new ReportPlan { MetricId = 1, Dimensions = [dim] }, "1000", Views));

    [Theory]
    [InlineData("company_code")]
    [InlineData("nope")]
    [InlineData("region OR 1=1")]
    public void Rejects_bad_filter_columns(string col)
    {
        var plan = new ReportPlan { MetricId = 1, Filters = { new FilterSpec { Column = col, Op = "=", Value = "x" } } };
        Assert.Throws<PlanRejectedException>(() => _c.Compile(Aging, plan, "1000", Views));
    }

    [Fact]
    public void Rejects_unknown_operator()
    {
        var plan = new ReportPlan { MetricId = 1, Filters = { new FilterSpec { Column = "region", Op = "LIKE", Value = "x" } } };
        Assert.Throws<PlanRejectedException>(() => _c.Compile(Aging, plan, "1000", Views));
    }

    [Fact]
    public void Rejects_a_catalog_measure_that_is_not_a_simple_aggregate()
    {
        var m = Setup.Metrics()[0]; m.MeasureExpr = "SUM(total_open); DROP TABLE x";
        Assert.Throws<PlanRejectedException>(() => _c.Compile(m, new ReportPlan(), "1000", Views));
    }

    [Fact]
    public void Optimizer_caps_rows_only_when_the_query_is_ordered()
    {
        var o = new Optimizer();
        var grouped = o.Optimize(_c.Compile(Aging, new ReportPlan { Dimensions = ["region"] }, "1000", Views), 50);
        Assert.EndsWith("LIMIT @maxRows", grouped.Sql);
        Assert.Equal(51, grouped.Parameters["maxRows"]);
        var single = o.Optimize(_c.Compile(Aging, new ReportPlan(), "1000", Views), 50);
        Assert.DoesNotContain("FETCH", single.Sql);
    }
}

public class PolicyGateTests
{
    private static PolicyGate Gate() => new(Options.Create(Setup.Policy()));
    private static CompiledQuery Q(string view, string sql, params string[] cols) => new()
    {
        Sql = sql, ViewName = view, Columns = cols, Parameters = new() { ["company"] = "1000" }
    };
    private static readonly HashSet<string> Known = new(Setup.Views.Keys, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Roles_are_unioned_and_unknown_roles_ignored()
    {
        var p = Gate().Resolve(Setup.User("FIN1000", "PLANNER", "NOT-CONFIGURED"));
        Assert.True(p.AllowsCompany("2000"));       // from PLANNER
        Assert.True(p.AllViews);                     // from FIN1000
        Assert.False(p.CatalogOwner);
    }

    [Fact]
    public void A_user_without_a_configured_role_gets_nothing()
        => Assert.Empty(Gate().Resolve(Setup.User("random")).Companies);

    [Fact]
    public void Denies_other_companies()
    {
        var d = Gate().Evaluate(Gate().Resolve(Setup.User("FIN1000")), "2000", Q("ar_aging_v", "SELECT region FROM rpt.ar_aging_v WHERE company_code = @company", "region"), Known);
        Assert.False(d.Allowed);
    }

    [Fact]
    public void Denies_views_outside_the_role()
    {
        var d = Gate().Evaluate(Gate().Resolve(Setup.User("PLANNER")), "1000", Q("ar_aging_v", "SELECT region FROM rpt.ar_aging_v WHERE company_code = @company", "region"), Known);
        Assert.False(d.Allowed);
    }

    [Fact]
    public void Denies_restricted_columns_even_when_the_view_is_allowed()
    {
        var d = Gate().Evaluate(Gate().Resolve(Setup.User("FIN1000")), "1000",
            Q("payroll_summary_v", "SELECT monthly_salary FROM rpt.payroll_summary_v WHERE company_code = @company", "monthly_salary"), Known);
        Assert.False(d.Allowed);
        Assert.Contains("restricted", d.Reason);
    }

    [Fact]
    public void Allows_a_clean_query()
    {
        var d = Gate().Evaluate(Gate().Resolve(Setup.User("FIN1000")), "1000", Q("ar_aging_v", "SELECT region FROM rpt.ar_aging_v WHERE company_code = @company", "region"), Known);
        Assert.True(d.Allowed);
    }
}
