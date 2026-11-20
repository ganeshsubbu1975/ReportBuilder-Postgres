namespace ReportBuilder.Core.Data;

public interface IAuditStore
{
    /// <summary>Appends one hash-chained row and returns its run id. Throws if the row cannot be stored.</summary>
    Task<long> AppendAsync(AuditEntry entry, CancellationToken ct);
    /// <summary>Recomputes the chain. Returns the first run_id whose hash does not match, or null when intact.</summary>
    Task<long?> VerifyChainAsync(CancellationToken ct);
    /// <summary>Newest first. userName null returns every user's runs.</summary>
    Task<IReadOnlyList<AuditRun>> GetRecentAsync(DateTime sinceUtc, string? userName, int max, CancellationToken ct);
}
