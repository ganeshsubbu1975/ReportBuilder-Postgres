using ReportBuilder.Core;
using ReportBuilder.Core.Data;
using ReportBuilder.Core.Pipeline;
using Xunit;

namespace ReportBuilder.Tests;

public class PipelineTests
{
    private static ControlTotal Ar(decimal gl, decimal sub) => new("AR", gl, sub, gl - sub);

    [Fact]
    public async Task Answers_a_certified_report_and_audits_it()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("report", "1000", ["aging", "receivables"]);
        rig.Llm.Json["plan"] = Setup.Plan(1, ["region"]);
        rig.Data.Totals = [Ar(500m, 500m)];

        var r = await rig.Pipeline.AskAsync("Show receivables aging by region for company 1000", Setup.User("FIN1000"));

        Assert.Equal(AskStatus.Answered, r.Status);
        Assert.Equal("Certified", r.Label);
        Assert.Equal(2, r.Result!.Rows.Count);
        Assert.Contains("LIMIT @maxRows", r.Sql);
        Assert.Equal("tester", rig.Data.LastUser);                       // the query ran as the end user
        var audit = Assert.Single(rig.Audit.Entries);
        Assert.Equal("Answered", audit.Status);
        Assert.Equal(1, audit.MetricId);
        Assert.Equal(1, r.RunId);
    }

    [Fact]
    public async Task A_control_break_downgrades_the_label_to_unverified()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("report", "1000", ["aging"]);
        rig.Llm.Json["plan"] = Setup.Plan(1, ["region"]);
        rig.Data.Totals = [Ar(1250m, 0m)];

        var r = await rig.Pipeline.AskAsync("Show receivables aging by region for company 1000", Setup.User("FIN1000"));

        Assert.Equal(AskStatus.Answered, r.Status);
        Assert.Equal("Unverified", r.Label);
        Assert.Contains(r.Findings, f => f.Code == "control_break" && f.Severity == Severity.Failure);
    }

    [Fact]
    public async Task An_ambiguous_term_asks_instead_of_guessing()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("report", "1000", ["overdue"]);

        var r = await rig.Pipeline.AskAsync("Show overdue invoices for company 1000", Setup.User("FIN1000"));

        Assert.Equal(AskStatus.NeedsClarification, r.Status);
        Assert.Contains("Receivables aging", r.Clarifications);
        Assert.Contains("Overdue invoice list", r.Clarifications);
        Assert.Null(rig.Data.LastQuery);                                  // nothing ran
    }

    [Fact]
    public async Task Naming_one_of_the_options_resolves_the_ambiguity()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("report", "1000", ["overdue"]);
        rig.Llm.Json["plan"] = Setup.Plan(2, ["customer_name"]);
        rig.Data.Totals = [Ar(0m, 0m)];

        var r = await rig.Pipeline.AskAsync("Show overdue invoices for company 1000 (Overdue invoice list)", Setup.User("FIN1000"));

        Assert.Equal(AskStatus.Answered, r.Status);
        Assert.Contains("rpt.ar_open_items_v", r.Sql);
    }

    [Fact]
    public async Task Another_companys_data_is_blocked_and_still_audited()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("report", "2000", ["aging"]);

        var r = await rig.Pipeline.AskAsync("Show receivables aging for company 2000", Setup.User("FIN1000"));

        Assert.Equal(AskStatus.Blocked, r.Status);
        Assert.Null(rig.Data.LastQuery);
        Assert.Equal("Blocked", Assert.Single(rig.Audit.Entries).Status);
    }

    [Fact]
    public async Task A_user_with_no_role_is_blocked_before_any_model_call()
    {
        var rig = Setup.Build();      // no scripted JSON: a model call would throw KeyNotFound and produce Failed
        var r = await rig.Pipeline.AskAsync("anything", Setup.User("nobody"));
        Assert.Equal(AskStatus.Blocked, r.Status);
    }

    [Fact]
    public async Task Unsupported_intent_is_refused()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("unsupported", "", []);
        var r = await rig.Pipeline.AskAsync("Predict next quarter's sales", Setup.User("FIN1000"));
        Assert.Equal(AskStatus.Unsupported, r.Status);
        Assert.Null(rig.Data.LastQuery);
    }

    [Fact]
    public async Task Missing_company_is_asked_when_the_user_can_see_several()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("report", "", ["aging"]);
        var r = await rig.Pipeline.AskAsync("Show receivables aging", Setup.User("OWNER"));
        Assert.Equal(AskStatus.NeedsClarification, r.Status);
        Assert.Equal(new[] { "1000", "2000" }, r.Clarifications);
    }

    [Fact]
    public async Task Missing_company_defaults_to_the_only_one_the_user_may_see()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("report", "", ["aging"]);
        rig.Llm.Json["plan"] = Setup.Plan(1, []);
        rig.Data.Totals = [Ar(0m, 0m)];
        var r = await rig.Pipeline.AskAsync("Show receivables aging", Setup.User("FIN1000"));
        Assert.Equal(AskStatus.Answered, r.Status);
        Assert.Equal("1000", rig.Data.LastQuery!.Parameters["company"]);
    }

    [Fact]
    public async Task A_plan_naming_a_metric_that_was_not_offered_fails_without_running_anything()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("report", "1000", ["aging"]);
        rig.Llm.Json["plan"] = Setup.Plan(99, ["region"]);
        var r = await rig.Pipeline.AskAsync("Show receivables aging for company 1000", Setup.User("FIN1000"));
        Assert.Equal(AskStatus.Failed, r.Status);
        Assert.Null(rig.Data.LastQuery);
    }

    [Fact]
    public async Task A_plan_with_an_unapproved_dimension_is_rejected()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("report", "1000", ["aging"]);
        rig.Llm.Json["plan"] = Setup.Plan(1, ["d0_30"]);
        var r = await rig.Pipeline.AskAsync("Show receivables aging for company 1000", Setup.User("FIN1000"));
        Assert.Equal(AskStatus.Failed, r.Status);
        Assert.Null(rig.Data.LastQuery);
        Assert.Contains(rig.Audit.Entries, e => e.Findings.Contains("plan_rejected"));
    }

    [Fact]
    public async Task Unverified_metrics_are_labelled_when_allowed_and_hidden_when_not()
    {
        var allowed = Setup.Build();
        allowed.Llm.Json["intent"] = Setup.Intent("report", "1000", ["stock value"]);
        allowed.Llm.Json["plan"] = Setup.Plan(3, ["plant_code"]);
        allowed.Data.Totals = [new ControlTotal("INVENTORY", 10m, 10m, 0m)];
        var r1 = await allowed.Pipeline.AskAsync("Show stock value by plant for company 1000", Setup.User("FIN1000"));
        Assert.Equal(AskStatus.Answered, r1.Status);
        Assert.Equal("Unverified", r1.Label);

        var strict = Setup.Build(Setup.Policy(allowUnverified: false));
        strict.Llm.Json["intent"] = Setup.Intent("report", "1000", ["stock value"]);
        var r2 = await strict.Pipeline.AskAsync("Show stock value by plant for company 1000", Setup.User("FIN1000"));
        Assert.Equal(AskStatus.Unsupported, r2.Status);
    }

    [Fact]
    public async Task Reconciliation_names_the_manual_journal_behind_the_difference()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("reconcile", "2000", [], ["AR"]);
        rig.Data.Totals = [Ar(240649.28m, 239399.28m)];
        rig.Data.Manual = [new ManualPosting("JV0001057", new DateTime(2026, 10, 1), "1210", 1250m, "Unreconciled manual journal")];
        rig.Data.TbNet = 0m;

        var r = await rig.Pipeline.AskAsync("Why does receivables not agree with the general ledger for company 2000?", Setup.User("OWNER"));

        Assert.Equal(AskStatus.Reconciled, r.Status);
        var row = Assert.Single(r.Result!.Rows);
        Assert.Contains("Fully explained", (string)row[4]!);
        Assert.Contains("JV0001057", (string)row[4]!);
    }

    [Fact]
    public async Task Reconciliation_admits_when_it_cannot_explain_the_difference()
    {
        var rig = Setup.Build();
        rig.Llm.Json["intent"] = Setup.Intent("reconcile", "1000", [], ["AR"]);
        rig.Data.Totals = [Ar(900m, 500m)];
        rig.Data.TbNet = 0m;
        var r = await rig.Pipeline.AskAsync("Why is AR different for company 1000?", Setup.User("FIN1000"));
        Assert.Equal(AskStatus.Reconciled, r.Status);
        Assert.Contains("No manual journals", (string)r.Result!.Rows[0][4]!);
        Assert.Contains(r.Findings, f => f.Severity == Severity.Failure);
    }

    [Fact]
    public async Task If_the_audit_log_cannot_be_written_no_result_is_released()
    {
        var rig = Setup.Build();
        rig.Audit.Fail = true;
        rig.Llm.Json["intent"] = Setup.Intent("report", "1000", ["aging"]);
        rig.Llm.Json["plan"] = Setup.Plan(1, ["region"]);
        rig.Data.Totals = [Ar(0m, 0m)];
        var r = await rig.Pipeline.AskAsync("Show receivables aging for company 1000", Setup.User("FIN1000"));
        Assert.Equal(AskStatus.Failed, r.Status);
        Assert.Null(r.Result);
    }

    [Fact]
    public async Task The_summary_falls_back_to_a_template_when_the_model_is_down()
    {
        var rig = Setup.Build();
        rig.Llm.TextThrows = true;
        rig.Llm.Json["intent"] = Setup.Intent("report", "1000", ["aging"]);
        rig.Llm.Json["plan"] = Setup.Plan(1, ["region"]);
        rig.Data.Totals = [Ar(0m, 0m)];
        var r = await rig.Pipeline.AskAsync("Show receivables aging by region for company 1000", Setup.User("FIN1000"));
        Assert.Equal(AskStatus.Answered, r.Status);
        Assert.Contains("Receivables aging", r.Answer);
        Assert.Contains("150.00", r.Answer);                               // 100 + 50
    }
}

public class AuditHasherTests
{
    private static readonly AuditEntry E = new("u", "q", "Answered", 1, "SELECT 1", 2, "[]", 15);
    private static readonly DateTime T = new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Same_input_gives_the_same_hash()
        => Assert.Equal(AuditHasher.Compute("", E, T), AuditHasher.Compute("", E, T));

    [Fact]
    public void Any_change_to_the_row_or_the_previous_hash_changes_the_hash()
    {
        var baseHash = AuditHasher.Compute("", E, T);
        Assert.NotEqual(baseHash, AuditHasher.Compute("x", E, T));
        Assert.NotEqual(baseHash, AuditHasher.Compute("", E with { RowCount = 3 }, T));
        Assert.NotEqual(baseHash, AuditHasher.Compute("", E with { UserName = "other" }, T));
        Assert.NotEqual(baseHash, AuditHasher.Compute("", E, T.AddMilliseconds(1)));
    }

    [Fact]
    public void Hash_is_64_hex_characters()
        => Assert.Matches("^[0-9a-f]{64}$", AuditHasher.Compute("", E, T));
}
