using ReportBuilder.Core.Catalog;

namespace ReportBuilder.Core.Views;

public sealed record ViewDraft(int DraftId, string Name, string Description, string DefinitionJson, string SqlText, string Status,
    string CreatedBy, DateTime CreatedAt, string? ReviewedBy, DateTime? ReviewedAt, string? ReviewNote);

public sealed record ViewPreview(string[] Columns, List<string?[]> Rows);

public interface IViewStore
{
    /// <summary>Base tables in the allowed ERP schemas, keyed by schema.Table, with column names and types.</summary>
    Task<IReadOnlyDictionary<string, ViewColumn[]>> GetSourcesAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetExistingViewsAsync(CancellationToken ct);
    /// <summary>First rows of the view SQL for one company, so the owner can check it before saving.</summary>
    Task<ViewPreview> PreviewAsync(string sql, string company, CancellationToken ct);
    Task<int> SaveDraftAsync(string name, string description, string definitionJson, string sql, string createdBy, CancellationToken ct);
    /// <summary>Changes a draft that is still waiting for approval. Throws if it has already been reviewed.</summary>
    Task UpdateDraftAsync(int draftId, string name, string description, string definitionJson, string sql, CancellationToken ct);
    Task<IReadOnlyList<ViewDraft>> GetDraftsAsync(CancellationToken ct);
    /// <summary>Approving creates rpt.&lt;name&gt; with the designer login. Throws if the reviewer is not allowed or the view cannot be created.</summary>
    Task ReviewAsync(int draftId, bool approve, string reviewer, string? note, CancellationToken ct);
}
