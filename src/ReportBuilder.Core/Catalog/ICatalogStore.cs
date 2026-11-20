namespace ReportBuilder.Core.Catalog;

public interface ICatalogStore
{
    /// <summary>All catalog entries, including their dimensions and synonyms. Rejected entries are included; callers filter.</summary>
    Task<IReadOnlyList<MetricEntry>> GetMetricsAsync(CancellationToken ct);
    Task<MetricEntry?> GetMetricAsync(int metricId, CancellationToken ct);
    /// <summary>Column names of every view in the rpt schema (names only, no data).</summary>
    Task<IReadOnlyDictionary<string, string[]>> GetViewColumnsAsync(CancellationToken ct);
    /// <summary>Column names and SQL data types of every view in the rpt schema.</summary>
    Task<IReadOnlyDictionary<string, ViewColumn[]>> GetViewSchemaAsync(CancellationToken ct);
    /// <summary>Inserts entries as UNVERIFIED and skips any whose (view, measure, dimensions) already exist. Returns the number inserted.</summary>
    Task<int> InsertUnverifiedAsync(IEnumerable<MetricEntry> entries, CancellationToken ct);
    /// <summary>
    /// Saves an edited metric: name, description, view, measure, SQL, chart type, dimensions and synonyms. When definitionChanged is true
    /// (the view, measure or group-by columns moved) the metric goes back to UNVERIFIED so it is reviewed again. Throws if another metric already has the same definition.
    /// </summary>
    Task UpdateMetricAsync(MetricEntry edited, bool definitionChanged, CancellationToken ct);
    Task SetReviewAsync(int metricId, bool approve, string reviewer, string? note, CancellationToken ct);
}
