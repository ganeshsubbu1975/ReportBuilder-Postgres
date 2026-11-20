using ReportBuilder.Core.Catalog;
using ReportBuilder.Core.Views;
using Xunit;

namespace ReportBuilder.Tests;

public class ViewBuilderTests
{
    private static readonly Dictionary<string, ViewColumn[]> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hr.PayrollLine"] = [new("employee_id", "int"), new("run_id", "int"), new("net_pay", "decimal")],
        ["hr.Employee"] = [new("employee_id", "int"), new("company_code", "char"), new("department_id", "int"), new("monthly_salary", "decimal"), new("name", "nvarchar")],
        ["hr.Department"] = [new("department_id", "int"), new("name", "nvarchar")]
    };

    private static ViewRequest Req(Action<ViewRequest>? tweak = null)
    {
        var r = new ViewRequest
        {
            Name = "payroll_by_dept_v", Description = "Net pay per department.", BaseSource = "hr.PayrollLine",
            Joins = [new JoinSpec { Source = "hr.Employee", Type = "INNER", LeftSource = 0, LeftColumn = "employee_id", RightColumn = "employee_id" }],
            Company = new ViewColumnSpec { Source = 1, Column = "company_code" },
            Columns = [new ViewColumnSpec { Source = 0, Column = "net_pay", Alias = "net_pay" }, new ViewColumnSpec { Source = 1, Column = "name", Alias = "employee" }]
        };
        tweak?.Invoke(r);
        return r;
    }

    private static ViewBuildResult Build(ViewRequest r, params string[] existing) => ViewBuilder.Build(r, Sources, ["monthly_salary"], existing);

    [Fact]
    public void Assembles_a_select_with_company_code()
    {
        var res = Build(Req());
        Assert.True(res.Ok);
        Assert.Equal("SELECT t1.\"company_code\" AS company_code, t0.\"net_pay\" AS \"net_pay\", t1.\"name\" AS \"employee\" FROM \"hr\".\"PayrollLine\" t0 INNER JOIN \"hr\".\"Employee\" t1 ON t0.\"employee_id\" = t1.\"employee_id\"", res.Sql);
    }

    [Theory]
    [InlineData("Payroll")]            // not lower case / no _v
    [InlineData("x; DROP TABLE a_v")]  // injection attempt
    [InlineData("payroll")]            // missing _v
    public void Rejects_bad_names(string name) => Assert.False(Build(Req(r => r.Name = name)).Ok);

    [Fact]
    public void Will_not_overwrite_an_existing_view() => Assert.False(Build(Req(), "payroll_by_dept_v").Ok);

    [Fact]
    public void Refuses_blocked_columns()
        => Assert.False(Build(Req(r => r.Columns.Add(new ViewColumnSpec { Source = 1, Column = "monthly_salary", Alias = "pay" }))).Ok);

    [Fact]
    public void Rejects_unknown_table_or_column()
    {
        Assert.False(Build(Req(r => r.BaseSource = "sys.sql_logins")).Ok);
        Assert.False(Build(Req(r => r.Columns.Add(new ViewColumnSpec { Source = 0, Column = "nope", Alias = "nope" }))).Ok);
    }

    [Fact]
    public void Rejects_bad_alias_and_duplicates()
    {
        Assert.False(Build(Req(r => r.Columns[0].Alias = "x] FROM sys.sql_logins --")).Ok);
        Assert.False(Build(Req(r => r.Columns[1].Alias = "net_pay")).Ok);
    }

    [Fact]
    public void Join_columns_must_have_the_same_type()
        => Assert.False(Build(Req(r => r.Joins[0].RightColumn = "name")).Ok);

    [Fact]
    public void Needs_a_company_column()
        => Assert.False(Build(Req(r => r.Company = new ViewColumnSpec())).Ok);
}
