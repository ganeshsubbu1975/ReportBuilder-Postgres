using ReportBuilder.Core;
using ReportBuilder.Core.Pipeline;

namespace ReportBuilder.Web;

public sealed class StageView
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string DoneDescription { get; init; } = "";
    public string State { get; set; } = "queued";   // queued, running, done, failed
    public string StateLabel => State switch { "done" => "Done", "running" => "Running", "failed" => "Failed", _ => "Queued" };
}

/// <summary>Per-circuit state for the report being built, so the New report, In progress and Report screens share one run.</summary>
public sealed class ReportSession
{
    private readonly ReportPipeline _pipeline;
    private CancellationTokenSource? _cts;

    public ReportSession(ReportPipeline pipeline) => _pipeline = pipeline;

    public string Question { get; set; } = "";
    public string Output { get; set; } = "Table and chart";
    public bool Started { get; private set; }
    public bool Running { get; private set; }
    public bool Cancelled { get; private set; }
    public string? Spec { get; private set; }
    public string? PolicyFailure { get; private set; }
    public AskResponse? Response { get; private set; }
    public List<StageView> Stages { get; private set; } = NewStages();
    public event Action? Changed;

    // Screen order. The pipeline reports the optimizer before the validator because the query runs between them.
    private static List<StageView> NewStages() => new()
    {
        new() { Key = "intent", Title = "Intent agent", Description = "Parses the request into metric, dimension, filters and period.", DoneDescription = "Parsed the request into metric, dimension, filters and period." },
        new() { Key = "semantic", Title = "Semantic layer agent", Description = "Maps your words to governed terms, so no table or column names are needed.", DoneDescription = "Mapped your words to governed terms, so no table or column names were needed." },
        new() { Key = "planner", Title = "Planner agent", Description = "Drafts the report spec, guided by similar approved reports from the library.", DoneDescription = "Drafted the report spec, guided by similar approved reports from the library." },
        new() { Key = "compiler", Title = "Query compiler", Description = "Compiles SQL from the spec with templates. The model writes no free-form SQL.", DoneDescription = "Compiled SQL from the spec with templates. The model wrote no free-form SQL." },
        new() { Key = "policy", Title = "Policy gate", Description = "Checks every execution rule. Details on the right.", DoneDescription = "All execution rules passed. Details on the right." },
        new() { Key = "validator", Title = "Validator agent", Description = "Checks that totals tie to the general ledger control account.", DoneDescription = "Checked the totals against the general ledger control account." },
        new() { Key = "optimizer", Title = "Optimizer agent", Description = "Chooses the aggregate table and estimates cost before the query runs.", DoneDescription = "Chose the aggregate table and estimated cost before the query ran." },
        new() { Key = "presenter", Title = "Presentation agent", Description = "Picks the table and chart, and writes the summary with caveats.", DoneDescription = "Picked the table and chart, and wrote the summary with caveats." }
    };

    public bool NeedsAttention => Response is { Status: not (AskStatus.Answered or AskStatus.Reconciled) };

    public void Start(string question, UserContext user)
    {
        _cts?.Cancel();
        var mine = _cts = new CancellationTokenSource();
        var ct = mine.Token;
        Question = question.Trim();
        Started = true; Running = true; Cancelled = false;
        Response = null; Spec = null; PolicyFailure = null;
        Stages = NewStages();
        Changed?.Invoke();

        _ = Task.Run(async () =>
        {
            AskResponse? resp = null;
            try { resp = await _pipeline.AskAsync(Question, user, ct, u => { if (ReferenceEquals(mine, _cts)) OnStage(u); }); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { resp = new AskResponse { Status = AskStatus.Failed, Answer = "This report could not be produced. " + ex.Message }; }
            if (ReferenceEquals(mine, _cts)) Finish(resp);
        });
    }

    private void OnStage(StageUpdate u)
    {
        var s = Stages.FirstOrDefault(x => x.Key == u.Stage);
        if (s is null) return;
        s.State = u.State;
        if (u.Stage == "planner" && u.State == "done") Spec = u.Payload;
        if (u.Stage == "policy" && u.State == "failed") PolicyFailure = u.Payload;
        Changed?.Invoke();
    }

    private void Finish(AskResponse? resp)
    {
        Response = resp;
        Running = false;
        var failed = resp is null or { Status: AskStatus.Blocked or AskStatus.Failed };
        foreach (var s in Stages.Where(x => x.State == "running")) s.State = failed ? "failed" : "done";
        if (resp?.Spec is not null) Spec = resp.Spec;
        Changed?.Invoke();
    }

    public void Cancel()
    {
        if (!Running) return;
        Cancelled = true;
        _cts?.Cancel();
    }

    public void Reset()
    {
        _cts?.Cancel();
        Started = false; Running = false; Cancelled = false; Response = null; Spec = null; PolicyFailure = null;
        Stages = NewStages();
        Changed?.Invoke();
    }
}

public sealed record SavedReport(Guid Id, string UserName, string Title, string Question, DateTime SavedAtUtc, AskResponse Response);

/// <summary>Saved reports. Kept in memory for the life of the server process; they are not written to the database.</summary>
public sealed class ReportLibrary
{
    private readonly List<SavedReport> _items = new();
    private readonly object _gate = new();

    public SavedReport Save(string user, string title, string question, AskResponse response)
    {
        var item = new SavedReport(Guid.NewGuid(), user, title, question, DateTime.UtcNow, response);
        lock (_gate) _items.Insert(0, item);
        return item;
    }

    public IReadOnlyList<SavedReport> For(string user)
    {
        lock (_gate) return _items.Where(i => i.UserName.Equals(user, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public SavedReport? Get(string user, Guid id)
    {
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id && i.UserName.Equals(user, StringComparison.OrdinalIgnoreCase));
    }
}
