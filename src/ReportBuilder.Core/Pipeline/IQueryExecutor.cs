namespace ReportBuilder.Core.Pipeline;

public interface IQueryExecutor
{
    /// <summary>Runs a compiled, already guarded query as the given user and returns at most maxRows rows.</summary>
    Task<QueryResult> ExecuteAsync(CompiledQuery query, string userName, int maxRows, CancellationToken ct);
}

public sealed record ControlTotal(string Area, decimal GlBalance, decimal SubledgerBalance, decimal Difference);
public sealed record ManualPosting(string JournalNo, DateTime PostingDate, string GlAccount, decimal Net, string Description);

/// <summary>Read-only facts used by the validator and the reconciler. All come from rpt views.</summary>
public interface IReconciliationSource
{
    Task<IReadOnlyList<ControlTotal>> GetControlTotalsAsync(string company, CancellationToken ct);
    Task<IReadOnlyList<ManualPosting>> GetManualPostingsAsync(string company, IReadOnlyCollection<string> glAccounts, CancellationToken ct);
    Task<decimal> GetTrialBalanceNetAsync(string company, CancellationToken ct);
}

public sealed record DraftPreview(string[] Columns, List<string[]> SampleRows, int RowCount);

/// <summary>Lets the catalog generator test-run a draft without exposing the executor's other features.</summary>
public interface IDraftPreview
{
    Task<DraftPreview> PreviewAsync(string sql, string company, CancellationToken ct);
}
