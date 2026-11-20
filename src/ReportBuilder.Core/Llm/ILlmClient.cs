namespace ReportBuilder.Core.Llm;

public interface ILlmClient
{
    /// <summary>Returns the model's answer parsed into T. The schema is sent as a strict JSON schema.</summary>
    Task<T> CompleteJsonAsync<T>(ModelRole role, string system, string user, string schemaName, string schemaJson, CancellationToken ct);
    Task<string> CompleteTextAsync(ModelRole role, string system, string user, CancellationToken ct);
}
