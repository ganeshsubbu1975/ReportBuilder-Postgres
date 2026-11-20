using ReportBuilder.Core;
using ReportBuilder.Web;
using Xunit;

namespace ReportBuilder.Tests;

public class ReportViewTests
{
    private static QueryResult Monthly(params decimal[] values)
    {
        var r = new QueryResult { Columns = ["fiscal_year", "period", "value"] };
        for (var i = 0; i < values.Length; i++) r.Rows.Add([(short)2026, (byte)(i + 1), values[i]]);
        return r;
    }

    [Fact]
    public void Year_and_period_are_dimensions_not_measures()
    {
        var v = new ReportView(Monthly(1m, 2m));
        Assert.Equal([2], v.NumCols);
        Assert.Equal(2, v.TimeCols.Count);
    }

    [Fact]
    public void A_series_over_time_becomes_a_trend_in_time_order()
    {
        var r = Monthly(300m, 900m, 450m);
        r.Rows.Reverse();                                   // arrive out of order
        var t = new ReportView(r).Trend();
        Assert.NotNull(t);
        Assert.Equal(["2026 P1", "2026 P2", "2026 P3"], t!.XLabels);
        Assert.Equal(new decimal?[] { 300m, 900m, 450m }, t.Series.Single().Values);
    }

    [Fact]
    public void Years_sort_before_periods()
    {
        var r = new QueryResult { Columns = ["fiscal_year", "period", "value"] };
        r.Rows.Add([(short)2026, (byte)1, 5m]); r.Rows.Add([(short)2025, (byte)12, 4m]);
        Assert.Equal(["2025 P12", "2026 P1"], new ReportView(r).Trend()!.XLabels);
    }

    [Fact]
    public void One_point_in_time_is_not_a_trend()
    {
        var r = new QueryResult { Columns = ["fiscal_year", "value"] };
        r.Rows.Add([(short)2026, 5m]);
        Assert.Null(new ReportView(r).Trend());
    }

    [Fact]
    public void Trend_kpis_report_latest_change_and_peak()
    {
        var k = new ReportView(Monthly(100m, 200m, 150m)).Kpis([], []);
        Assert.StartsWith("Latest", k[0].Label);
        Assert.Equal("-25.0%", k[1].Value);
        Assert.Equal("neg", k[1].Tone);
        Assert.Equal("2026 P2", k[2].Sub);
    }

    [Fact]
    public void Axis_includes_zero_and_lands_on_round_steps()
    {
        var (lo, hi, step) = ReportView.NiceAxis(204093m, 919593m);
        Assert.Equal(0m, lo);
        Assert.True(hi >= 919593m);
        Assert.Equal(0m, hi % step);
    }

    [Fact]
    public void Reconciliation_style_results_are_never_trends()
    {
        var r = new QueryResult { Columns = ["area", "gl_balance", "difference"] };
        r.Rows.Add(["AR", 10m, 1m]); r.Rows.Add(["AP", 5m, 0m]);
        Assert.Null(new ReportView(r, additive: false).Trend());
    }
}
