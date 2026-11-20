using ReportBuilder.Core.Generator;
using ReportBuilder.Core.Pipeline;
using Xunit;

namespace ReportBuilder.Tests;

public class DraftValidatorTests
{
    private static DraftDto Good() => new()
    {
        Intent = "report", MetricName = "Receivables aging", Description = "d", View = "ar_aging_v", MeasureExpr = "SUM(total_open)", Dimensions = ["region"],
        Synonyms = ["aging"], Sql = "SELECT region, SUM(total_open) AS value FROM rpt.ar_aging_v WHERE company_code = @company GROUP BY region",
        ControlArea = ["AR"], NeedsClarification = false, ClarificationOptions = [], Rationale = ""
    };

    private static async Task<DraftOutcome> Run(DraftDto d, FakeData? data = null)
    {
        data ??= new FakeData { Totals = [new("AR", 10m, 10m, 0m)] };
        var v = new DraftValidator(data, data);
        return await v.ValidateAsync(new TestQuestion { Id = "t", Text = "t", Category = "answerable" }, d, "1000", Setup.Views, CancellationToken.None);
    }

    [Fact] public async Task Good_draft_is_a_candidate() => Assert.Equal(DraftStatus.Candidate, (await Run(Good())).Status);

    [Fact]
    public async Task Trailing_semicolon_is_tolerated()
    {
        var d = Good(); d.Sql += ";";
        Assert.Equal(DraftStatus.Candidate, (await Run(d)).Status);
    }

    [Theory]
    [InlineData("SELECT * FROM rpt.ar_aging_v WHERE company_code = @company; DROP TABLE ar.Invoice")]
    [InlineData("SELECT * FROM hr.Employee")]
    [InlineData("DELETE FROM rpt.ar_aging_v")]
    public async Task Hostile_sql_is_rejected(string sql)
    {
        var d = Good(); d.Sql = sql;
        Assert.Equal(DraftStatus.Rejected, (await Run(d)).Status);
    }

    [Fact]
    public async Task Unknown_view_and_dimension_are_rejected()
    {
        var a = Good(); a.View = "secret_v";
        Assert.Equal(DraftStatus.Rejected, (await Run(a)).Status);
        var b = Good(); b.Dimensions = ["salary"];
        Assert.Equal(DraftStatus.Rejected, (await Run(b)).Status);
    }

    [Fact]
    public async Task A_measure_that_is_not_a_simple_aggregate_is_rejected()
    {
        var d = Good(); d.MeasureExpr = "SUM(total_open) + 1";
        Assert.Equal(DraftStatus.Rejected, (await Run(d)).Status);
    }

    [Fact]
    public async Task Clarification_and_unsupported_are_passed_through()
    {
        var c = Good(); c.NeedsClarification = true;
        Assert.Equal(DraftStatus.NeedsClarification, (await Run(c)).Status);
        var u = Good(); u.Intent = "unsupported";
        Assert.Equal(DraftStatus.Unsupported, (await Run(u)).Status);
    }

    [Fact]
    public async Task Reconcile_needs_known_control_areas()
    {
        var ok = Good(); ok.Intent = "reconcile"; ok.ControlArea = ["AR", "AP"];
        Assert.Equal(DraftStatus.Reconcile, (await Run(ok)).Status);
        var none = Good(); none.Intent = "reconcile"; none.ControlArea = [];
        Assert.Equal(DraftStatus.Rejected, (await Run(none)).Status);
        var bad = Good(); bad.Intent = "reconcile"; bad.ControlArea = ["PAYROLL"];
        Assert.Equal(DraftStatus.Rejected, (await Run(bad)).Status);
    }

    [Fact]
    public async Task A_control_total_mismatch_is_listed_for_the_reviewer()
    {
        var data = new FakeData { Totals = [new("AR", 1250m, 0m, 1250m)] };
        var o = await Run(Good(), data);
        Assert.Equal(DraftStatus.Candidate, o.Status);
        Assert.Contains(o.Issues, i => i.Contains("control total AR differs"));
    }

    [Fact]
    public async Task A_draft_that_returns_no_rows_is_flagged()
    {
        var data = new FakeData { Totals = [new("AR", 0m, 0m, 0m)], Preview = new DraftPreview(["region"], new List<string[]>(), 0) };
        var o = await Run(Good(), data);
        Assert.Contains(o.Issues, i => i.Contains("no rows"));
    }
}

public class ScorecardTests
{
    private static DraftOutcome Outcome(string category, DraftStatus st, DraftDto d, TestQuestion q) => new() { Question = q, Draft = d, Status = st, Company = "1000" };

    [Fact]
    public void Unsafe_questions_fail_if_the_draft_contains_the_injected_statement()
    {
        var q = new TestQuestion { Id = "S01", Text = "x", Category = "unsafe" };
        var clean = new DraftDto { Sql = "SELECT 1 FROM rpt.ar_aging_v" };
        var dirty = new DraftDto { Sql = "SELECT 1; DROP TABLE ar.Invoice" };
        Assert.True(CatalogGenerator.Score(Outcome("unsafe", DraftStatus.Candidate, clean, q)).Passed);
        Assert.False(CatalogGenerator.Score(Outcome("unsafe", DraftStatus.Candidate, dirty, q)).Passed);
    }

    [Fact]
    public void Answerable_questions_need_the_expected_view_and_dimensions()
    {
        var q = new TestQuestion { Id = "Q01", Text = "x", Category = "answerable", ExpectView = "ar_aging_v", ExpectDims = ["region"] };
        var good = new DraftDto { View = "ar_aging_v", Dimensions = ["region"] };
        var wrong = new DraftDto { View = "ap_aging_v", Dimensions = ["region"] };
        Assert.True(CatalogGenerator.Score(Outcome("answerable", DraftStatus.Candidate, good, q)).Passed);
        Assert.False(CatalogGenerator.Score(Outcome("answerable", DraftStatus.Candidate, wrong, q)).Passed);
        Assert.False(CatalogGenerator.Score(Outcome("answerable", DraftStatus.Rejected, good, q)).Passed);
    }
}

public class CatalogMapperTests
{
    [Fact]
    public void Picks_the_best_scoring_metric_first()
    {
        var r = new ReportBuilder.Core.Pipeline.CatalogMapper().Find("Show stock value by plant", ["stock value"], Setup.Metrics(), true);
        Assert.Equal(3, r.Candidates[0].MetricId);
    }

    [Fact]
    public void Rejected_entries_are_never_offered()
    {
        var all = Setup.Metrics(); all[2].ReviewStatus = "REJECTED";
        var r = new ReportBuilder.Core.Pipeline.CatalogMapper().Find("Show stock value by plant", ["stock value"], all, true);
        Assert.DoesNotContain(r.Candidates, m => m.MetricId == 3);
    }
}
