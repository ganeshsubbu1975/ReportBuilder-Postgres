using ReportBuilder.Core.Pipeline;
using Xunit;

namespace ReportBuilder.Tests;

public class SqlGuardTests
{
    private static readonly HashSet<string> Views = new(StringComparer.OrdinalIgnoreCase) { "ar_aging_v", "control_totals_v" };
    private static readonly HashSet<string> Params = new(StringComparer.OrdinalIgnoreCase) { "company" };

    private static GuardResult Check(string sql) => SqlGuard.Check(sql, Views, Params);

    [Fact]
    public void Accepts_a_plain_select_over_an_rpt_view()
        => Assert.True(Check("SELECT region, SUM(total_open) AS value FROM rpt.ar_aging_v WHERE company_code = @company GROUP BY region").Ok);

    [Fact]
    public void Accepts_string_literals_that_look_like_codes()
        => Assert.True(Check("SELECT area FROM rpt.control_totals_v WHERE company_code = @company AND area IN ('AR', 'AP')").Ok);

    [Theory]
    [InlineData("SELECT * FROM rpt.ar_aging_v WHERE company_code = @company; DROP TABLE ar.Invoice")]
    [InlineData("DELETE FROM rpt.ar_aging_v")]
    [InlineData("SELECT * FROM hr.Employee WHERE company_code = @company")]
    [InlineData("SELECT a.region FROM rpt.ar_aging_v a JOIN ar.Customer c ON 1 = 1 WHERE a.company_code = @company")]
    [InlineData("SELECT * FROM rpt.ar_aging_v WHERE company_code = @company -- hidden")]
    [InlineData("SELECT * FROM rpt.ar_aging_v /* x */ WHERE company_code = @company")]
    [InlineData("SELECT * FROM rpt.secret_v WHERE company_code = @company")]
    [InlineData("SELECT * FROM rpt.ar_aging_v WHERE region = @region")]
    [InlineData("SELECT region INTO #t FROM rpt.ar_aging_v WHERE company_code = @company")]
    [InlineData("EXEC sp_who")]
    [InlineData("SELECT * FROM rpt.ar_aging_v WHERE company_code = @company AND @@SERVERNAME IS NOT NULL")]
    [InlineData("SELECT name FROM sys.tables")]
    [InlineData("SELECT * FROM cat.Metric")]
    [InlineData("SELECT * FROM audit.ReportRun")]
    [InlineData("SELECT * FROM [rpt].[ar_aging_v]")]
    [InlineData("")]
    public void Rejects_hostile_or_wrong_sql(string sql)
        => Assert.False(Check(sql).Ok);

    [Fact]
    public void Reports_the_reason()
        => Assert.Contains(Check("DELETE FROM rpt.ar_aging_v").Issues, i => i.Contains("not a SELECT"));
}
