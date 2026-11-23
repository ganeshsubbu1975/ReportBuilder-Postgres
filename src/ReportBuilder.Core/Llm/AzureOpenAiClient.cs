using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using ReportBuilder.Core.Data;

namespace ReportBuilder.Core.Llm;

/// <summary>Chat completions client for Azure OpenAI / Foundry deployments, or for Ollama and other OpenAI-compatible servers (Llm:Provider). With Ollama nothing leaves your network.</summary>
public sealed class AzureOpenAiClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly LlmOptions _o;
    private readonly ITokenUsageStore? _tokenStore;
    // Used only when no ApiKey is configured: managed identity in Azure, developer sign-in locally. Tokens are cached by the credential.
    private static readonly Lazy<TokenCredential> Credential = new(() => new DefaultAzureCredential());
    private static readonly string[] Scope = ["https://cognitiveservices.azure.com/.default"];

    public AzureOpenAiClient(HttpClient http, IOptions<LlmOptions> options, ITokenUsageStore? tokenStore = null)
    {
        _http = http;
        _o = options.Value;
        _tokenStore = tokenStore;
    }

    public async Task<T> CompleteJsonAsync<T>(ModelRole role, string system, string user, string schemaName, string schemaJson, CancellationToken ct)
    {
        var content = await PostAsync(role, system, user, schemaName, schemaJson, ct);
        return JsonSerializer.Deserialize<T>(content, JsonOpts)
               ?? throw new InvalidOperationException("The model returned an empty JSON document.");
    }

    public Task<string> CompleteTextAsync(ModelRole role, string system, string user, CancellationToken ct)
        => PostAsync(role, system, user, null, null, ct);

    private async Task<string> PostAsync(ModelRole role, string system, string user, string? schemaName, string? schemaJson, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var key = role.ToString().ToLowerInvariant();
        if (!_o.Deployments.TryGetValue(key, out var deployment) || string.IsNullOrWhiteSpace(deployment))
            throw new InvalidOperationException($"No model deployment configured for role '{key}' (Llm:Deployments:{key}).");

        var body = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user })
        };
        if (_o.Temperature.HasValue) body["temperature"] = _o.Temperature.Value;
        if (schemaJson != null)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = schemaName, ["strict"] = true, ["schema"] = JsonNode.Parse(schemaJson) }
            };
        }

        var ollama = TokenPricing.IsLocalProvider(_o.Provider);
        string url;
        if (ollama)
        {
            // Ollama / OpenAI-compatible: the "deployment" is the model tag (for example qwen2.5:14b-instruct). Needs a model that supports structured outputs.
            body["model"] = deployment;
            body["stream"] = false;
            url = $"{_o.Endpoint.TrimEnd('/')}/v1/chat/completions";
        }
        else
            url = $"{_o.Endpoint.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={_o.ApiVersion}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (ollama)
        {
            if (!string.IsNullOrWhiteSpace(_o.ApiKey))   // Ollama needs no key; set one only if a reverse proxy in front of it does
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _o.ApiKey);
        }
        else if (!string.IsNullOrWhiteSpace(_o.ApiKey))
            req.Headers.Add("api-key", _o.ApiKey);
        else
        {
            var token = await Credential.Value.GetTokenAsync(new TokenRequestContext(Scope), ct);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        }

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Model call failed ({(int)resp.StatusCode}): {Truncate(text, 300)}");

        var doc = JsonNode.Parse(text);
        var content = doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                      ?? throw new InvalidOperationException("The model response had no content.");

        var usageNode = doc?["usage"];
        var promptTokens = ReadInt(usageNode?["prompt_tokens"]);
        var completionTokens = ReadInt(usageNode?["completion_tokens"]);
        var cachedTokens = ReadInt(usageNode?["prompt_tokens_details"]?["cached_tokens"]);
        var totalTokens = ReadInt(usageNode?["total_tokens"]);
        var isEstimated = false;
        if (promptTokens <= 0 && completionTokens <= 0)
        {
            isEstimated = true;
            promptTokens = Math.Max(1, (system.Length + user.Length) / 4);
            completionTokens = Math.Max(1, content.Length / 4);
        }
        if (totalTokens <= 0)
            totalTokens = promptTokens + completionTokens;

        var costUsd = TokenPricing.EstimateCostUsd(_o.Provider, deployment, promptTokens, completionTokens, cachedTokens);
        var scope = TokenUsageContext.Current;
        scope?.RecordLlmCall(_o.Provider, deployment, promptTokens, completionTokens, cachedTokens, totalTokens, costUsd);

        if (_tokenStore is not null)
        {
            try
            {
                await _tokenStore.RecordAsync(new TokenUsageEvent
                {
                    OccurredAtUtc = DateTime.UtcNow,
                    UserName = scope?.UserName ?? "system",
                    CompanyCode = scope?.CompanyCode ?? "",
                    Feature = scope?.Feature ?? role.ToString(),
                    Outcome = "LiveLlm",
                    Provider = _o.Provider,
                    ModelRole = role.ToString(),
                    Deployment = deployment,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    CachedPromptTokens = cachedTokens,
                    TotalTokens = totalTokens,
                    SavedTokens = 0,
                    EstimatedCostUsd = costUsd,
                    SavedCostUsd = 0m,
                    DurationMs = (int)sw.ElapsedMilliseconds,
                    IsEstimated = isEstimated,
                    QuestionPreview = scope?.QuestionPreview ?? ""
                }, ct);
            }
            catch
            {
                // Telemetry errors must never disrupt live report generation
            }
        }

        return content;
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return Math.Max(0, i);
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, 0, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)Math.Clamp(Math.Round(d), 0, int.MaxValue);
        return 0;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
