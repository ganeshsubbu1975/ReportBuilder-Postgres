using ReportBuilder.Core;
using ReportBuilder.Core.Catalog;
using Xunit;

namespace ReportBuilder.Tests;

public class MetricBuilderTests
{
    private static readonly Dictionary<string, ViewColumn[]> Schema = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sales_v"] = [new("company_code", "char"), new("item", "nvarchar"), new("region", "char"), new("amount", "decimal"), new("quantity", "int"), new("monthly_salary", "decimal")]
    };

    private static MetricRequest Req(Action<MetricRequest>? tweak = null)
    {
        var r = new MetricRequest { Name = "Sales amount", Description = "Total sales.", View = "sales_v", Aggregate = "SUM", MeasureColumn = "amount", Dimensions = ["item"], Synonyms = "revenue, takings" };
        tweak?.Invoke(r);
        return r;
    }

    private static MetricBuildResult Build(MetricRequest r) => MetricBuilder.Build(r, Schema, ["monthly_salary"], "tester");

    [Fact]
    public void Combines_several_columns_in_one_measure()
    {
        var res = Build(Req(r => { r.MeasureColumn = "quantity"; r.ExtraMeasureColumns = ["amount"]; r.MeasureOperator = "*"; }));
        Assert.True(res.Ok);
        Assert.Equal("SUM(quantity * amount)", res.Entry!.MeasureExpr);
        var back = MetricBuilder.ToRequest(res.Entry, Schema, ["monthly_salary"]);
        Assert.True(back.DefinitionEditable);
        Assert.Equal(["amount"], back.Request.ExtraMeasureColumns);
    }

    [Theory]
    [InlineData("item")]            // not numeric
    [InlineData("monthly_salary")]  // blocked
    [InlineData("nope")]            // not in the view
    public void Rejects_bad_extra_measure_columns(string extra)
        => Assert.False(Build(Req(r => { r.MeasureColumn = "quantity"; r.ExtraMeasureColumns = [extra]; })).Ok);

    [Fact]
    public void Extra_measure_column_cannot_also_be_a_group_by()
        => Assert.False(Build(Req(r => { r.MeasureColumn = "quantity"; r.ExtraMeasureColumns = ["amount"]; r.Dimensions = ["amount"]; })).Ok);

    [Fact]
    public void Builds_an_unverified_entry_with_assembled_sql()
    {
        var res = Build(Req());
        Assert.True(res.Ok);
        var e = res.Entry!;
        Assert.Equal("SUM(amount)", e.MeasureExpr);
        Assert.Equal("SELECT item, SUM(amount) AS value FROM rpt.sales_v WHERE company_code = :company GROUP BY item", e.SqlTemplate);
        Assert.False(e.IsCertified);
        Assert.Equal("UNVERIFIED", e.ReviewStatus);
        Assert.Contains("sales amount", e.Synonyms);          // the name is always a synonym
        Assert.Contains("takings", e.Synonyms);
    }

    [Theory]
    [InlineData("line", "line")]
    [InlineData("PIE", "pie")]
    [InlineData("", "auto")]
    public void Chart_type_is_kept_on_the_entry(string requested, string expected)
        => Assert.Equal(expected, Build(Req(r => r.ChartType = requested)).Entry!.ChartType);

    [Fact]
    public void A_metric_built_here_round_trips_into_an_editable_request()
    {
        var entry = Build(Req(r => { r.ChartType = "column"; r.Name = "Sales amount"; })).Entry!;
        var (req, editable) = MetricBuilder.ToRequest(entry, Schema, ["monthly_salary"]);
        Assert.True(editable);
        Assert.Equal("SUM", req.Aggregate);
        Assert.Equal("amount", req.MeasureColumn);
        Assert.Equal("column", req.ChartType);
        Assert.DoesNotContain("sales amount", req.Synonyms);   // the name is re-added on save
        Assert.Equal(entry.SqlTemplate, Build(req).Entry!.SqlTemplate);
    }

    [Fact]
    public void A_metric_with_foreign_sql_is_edited_without_touching_its_sql()
    {
        var entry = new MetricEntry { MetricId = 7, Name = "Aging", Description = "d", ViewName = "sales_v", MeasureExpr = "SUM(CASE WHEN amount > 0 THEN amount END)",
            SqlTemplate = "SELECT 1", Dimensions = ["item"], Synonyms = ["aging"], ReviewStatus = "CERTIFIED", IsCertified = true };
        var (req, editable) = MetricBuilder.ToRequest(entry, Schema, []);
        Assert.False(editable);
        req.ChartType = "pie"; req.Name = "Aging total";
        var res = MetricBuilder.ApplyEdit(entry, req, Schema, []);
        Assert.True(res.Ok);
        Assert.Equal("SELECT 1", res.Entry!.SqlTemplate);
        Assert.Equal("pie", res.Entry.ChartType);
        Assert.Equal(7, res.Entry.MetricId);
        Assert.Equal("CERTIFIED", res.Entry.ReviewStatus);
    }

    [Fact]
    public void Group_by_columns_of_a_standard_shape_metric_can_be_changed_even_when_not_built_on_screen()
    {
        var entry = new MetricEntry { MetricId = 8, Name = "Sales", Description = "d", ViewName = "sales_v", MeasureExpr = "SUM(amount)",
            SqlTemplate = "SELECT item, SUM(amount) AS value FROM rpt.sales_v WHERE company_code = :company GROUP BY item", Dimensions = ["item"], Synonyms = ["sales"] };
        Assert.True(MetricBuilder.CanEditDimensions(entry));
        var (req, _) = MetricBuilder.ToRequest(entry, Schema, []);
        req.Dimensions = ["region", "item"];
        var res = MetricBuilder.ApplyEdit(entry, req, Schema, ["monthly_salary"]);
        Assert.True(res.Ok);
        Assert.Equal("SELECT region, item, SUM(amount) AS value FROM rpt.sales_v WHERE company_code = :company GROUP BY region, item", res.Entry!.SqlTemplate);
        req.Dimensions = ["amount"];
        Assert.Contains(MetricBuilder.ApplyEdit(entry, req, Schema, []).Issues, i => i.Contains("measured column"));
        req.Dimensions = ["monthly_salary"];
        Assert.False(MetricBuilder.ApplyEdit(entry, req, Schema, ["monthly_salary"]).Ok);
        req.Dimensions = [];
        Assert.Equal("SELECT SUM(amount) AS value FROM rpt.sales_v WHERE company_code = :company", MetricBuilder.ApplyEdit(entry, req, Schema, []).Entry!.SqlTemplate);
    }

    [Fact]
    public void Unknown_chart_type_is_rejected()
    {
        var res = Build(Req(r => r.ChartType = "radar"));
        Assert.False(res.Ok);
        Assert.Contains(res.Issues, i => i.Contains("chart type"));
    }

    [Fact]
    public void Count_with_no_column_counts_rows_and_needs_no_grouping()
    {
        var res = Build(Req(r => { r.Aggregate = "COUNT"; r.MeasureColumn = ""; r.Dimensions = []; }));
        Assert.True(res.Ok);
        Assert.Equal("COUNT(*)", res.Entry!.MeasureExpr);
        Assert.Equal("SELECT COUNT(*) AS value FROM rpt.sales_v WHERE company_code = :company", res.Entry.SqlTemplate);
    }

    [Fact]
    public void Count_distinct_is_supported()
        => Assert.Equal("COUNT(DISTINCT item)", Build(Req(r => { r.Aggregate = "COUNT DISTINCT"; r.MeasureColumn = "item"; r.Dimensions = []; })).Entry!.MeasureExpr);

    [Theory]
    [InlineData("amount); DROP TABLE x;--")]
    [InlineData("nope")]
    [InlineData("company_code")]
    [InlineData("monthly_salary")]
    public void Unknown_or_restricted_measure_columns_are_rejected(string column)
        => Assert.False(Build(Req(r => r.MeasureColumn = column)).Ok);

    [Fact]
    public void Sum_needs_a_numeric_column() => Assert.Contains(Build(Req(r => { r.MeasureColumn = "item"; r.Dimensions = []; })).Issues, i => i.Contains("numeric"));

    [Fact]
    public void Unknown_aggregates_are_rejected() => Assert.False(Build(Req(r => r.Aggregate = "SUM(amount) FROM x --")).Ok);

    [Fact]
    public void Restricted_or_unknown_group_columns_are_rejected()
    {
        Assert.False(Build(Req(r => r.Dimensions = ["monthly_salary"])).Ok);
        Assert.False(Build(Req(r => r.Dimensions = ["nope"])).Ok);
        Assert.False(Build(Req(r => r.Dimensions = ["amount"])).Ok);       // the measured column
    }

    [Fact]
    public void A_view_that_does_not_exist_is_rejected() => Assert.False(Build(Req(r => r.View = "secret_v")).Ok);

    [Fact]
    public void Name_and_description_are_required()
    {
        Assert.False(Build(Req(r => r.Name = "ab")).Ok);
        Assert.False(Build(Req(r => r.Description = " ")).Ok);
    }
}
