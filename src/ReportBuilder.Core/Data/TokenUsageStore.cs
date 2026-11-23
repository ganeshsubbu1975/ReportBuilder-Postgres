using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ReportBuilder.Core.Data;

/// <summary>
/// Represents a single AI or zero-token reporting execution event recorded for the Token Usage &amp; Savings Dashboard.
/// </summary>
public sealed class TokenUsageEvent
{
    public long EventId { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string UserName { get; set; } = "";
    public string CompanyCode { get; set; } = "";
    public string Feature { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ModelRole { get; set; } = "";
    public string Deployment { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int CachedPromptTokens { get; set; }
    public int TotalTokens { get; set; }
    public int SavedTokens { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public decimal SavedCostUsd { get; set; }
    public int DurationMs { get; set; }
    public bool IsEstimated { get; set; }
    public string QuestionPreview { get; set; } = "";
}

public sealed record TokenModelRate(string Model, string DisplayName, decimal InputPer1M, decimal CachedInputPer1M, decimal OutputPer1M);

/// <summary>
/// Reference per-1M token USD pricing and baseline savings estimators for Azure OpenAI and local Ollama deployments.
/// </summary>
public static class TokenPricing
{
    public const int DefaultBaselinePromptTokens = 1400;
    public const int DefaultBaselineCompletionTokens = 200;
    public const int DefaultBaselinePlannerTokens = DefaultBaselinePromptTokens + DefaultBaselineCompletionTokens;

    private static readonly TokenModelRate[] Rates =
    [
        new("gpt-4o-mini", "GPT-4o mini", 0.15m, 0.075m, 0.60m),
        new("gpt-4o", "GPT-4o", 2.50m, 1.25m, 10.00m),
        new("gpt-4.1-nano", "GPT-4.1 nano", 0.10m, 0.025m, 0.40m),
        new("gpt-4.1-mini", "GPT-4.1 mini", 0.40m, 0.10m, 1.60m),
        new("gpt-4.1", "GPT-4.1", 2.00m, 0.50m, 8.00m),
        new("ollama-local", "Ollama Local ($0)", 0m, 0m, 0m)
    ];

    public static IReadOnlyList<TokenModelRate> GetModelRatesTable() => Rates;

    public static bool IsLocalProvider(string? provider) =>
        !string.IsNullOrWhiteSpace(provider) &&
        (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
         provider.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase));

    public static decimal EstimateCostUsd(
        string? provider,
        string? deployment,
        int promptTokens,
        int completionTokens,
        int cachedPromptTokens = 0)
    {
        if (IsLocalProvider(provider)) return 0m;
        var rate = MatchRate(deployment);
        var clampedCached = Math.Clamp(cachedPromptTokens, 0, Math.Max(0, promptTokens));
        var uncachedPrompt = Math.Max(0, promptTokens - clampedCached);
        var inputCost = (uncachedPrompt / 1_000_000m) * rate.InputPer1M;
        var cachedCost = (clampedCached / 1_000_000m) * rate.CachedInputPer1M;
        var outputCost = (Math.Max(0, completionTokens) / 1_000_000m) * rate.OutputPer1M;
        return decimal.Round(inputCost + cachedCost + outputCost, 6, MidpointRounding.AwayFromZero);
    }

    public static TokenModelRate MatchRate(string? deployment)
    {
        var d = (deployment ?? "").Trim().ToLowerInvariant();
        if (d.Contains("ollama") || d.Contains("gemma") || d.Contains("llama") || d.Contains("qwen"))
            return Rates[^1];
        if (d.Contains("gpt-4o-mini")) return Rates[0];
        if (d.Contains("gpt-4o")) return Rates[1];
        if (d.Contains("gpt-4.1-nano")) return Rates[2];
        if (d.Contains("gpt-4.1-mini")) return Rates[3];
        if (d.Contains("gpt-4.1")) return Rates[4];
        return Rates[1];
    }
}

/// <summary>
/// Per-request attribution state populated by HTTP endpoints and read by AzureOpenAiClient during LLM calls.
/// </summary>
public sealed class TokenCallScope
{
    public string UserName { get; }
    public string CompanyCode { get; }
    public string Feature { get; }
    public string QuestionPreview { get; }
    public bool LlmCalled { get; private set; }
    public int LastPromptTokens { get; private set; }
    public int LastCompletionTokens { get; private set; }
    public int LastCachedPromptTokens { get; private set; }
    public int LastTotalTokens { get; private set; }
    public decimal LastEstimatedCostUsd { get; private set; }
    public string LastDeployment { get; private set; } = "";
    public string LastProvider { get; private set; } = "";

    public TokenCallScope(string userName, string companyCode, string feature, string? questionPreview)
    {
        UserName = userName;
        CompanyCode = companyCode;
        Feature = feature;
        QuestionPreview = TruncatePreview(questionPreview, 220);
    }

    public void RecordLlmCall(
        string provider,
        string deployment,
        int promptTokens,
        int completionTokens,
        int cachedPromptTokens,
        int totalTokens,
        decimal estimatedCostUsd)
    {
        LlmCalled = true;
        LastProvider = provider;
        LastDeployment = deployment;
        LastPromptTokens = promptTokens;
        LastCompletionTokens = completionTokens;
        LastCachedPromptTokens = cachedPromptTokens;
        LastTotalTokens = totalTokens;
        LastEstimatedCostUsd = estimatedCostUsd;
    }

    public static string TruncatePreview(string? s, int maxLen = 220)
    {
        var clean = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= maxLen ? clean : clean[..maxLen];
    }
}

/// <summary>
/// AsyncLocal context that lets deep HTTP client calls tag token usage with the calling ERP user, company, and feature.
/// </summary>
public static class TokenUsageContext
{
    private static readonly AsyncLocal<TokenCallScope?> Holder = new();

    public static TokenCallScope? Current => Holder.Value;

    public static IDisposable Begin(string userName, string companyCode, string feature, string? questionPreview = null)
    {
        var prev = Holder.Value;
        Holder.Value = new TokenCallScope(userName, companyCode, feature, questionPreview);
        return new RestoreScope(prev);
    }

    private sealed class RestoreScope(TokenCallScope? prev) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Holder.Value = prev;
        }
    }
}

public sealed record TokenDashboardFilter(
    int Days,
    string? CompanyCode,
    string? FilterUserName,
    string RequestingUserName,
    bool IsAdmin,
    IReadOnlySet<string>? AllowedCompanies = null);

public sealed record TokenDailyPoint(
    string Date,
    long PromptTokens,
    long CompletionTokens,
    long CachedPromptTokens,
    long SavedTokens,
    decimal CostUsd,
    decimal SavedCostUsd,
    int LlmCalls,
    int ZeroTokenOps);

public sealed record TokenOutcomeBucket(
    string Outcome,
    string Label,
    int Count,
    long TokensSpent,
    long TokensSaved,
    decimal CostUsd,
    decimal SavedCostUsd);

public sealed record TokenFeatureBucket(
    string Feature,
    string Label,
    int Calls,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long SavedTokens,
    decimal CostUsd);

public sealed record TokenModelBucket(
    string Provider,
    string Deployment,
    int Calls,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    decimal CostUsd,
    int AvgLatencyMs);

public sealed record TokenEntityBucket(
    string Name,
    int LlmCalls,
    int ZeroTokenOps,
    long TotalTokens,
    long SavedTokens,
    decimal CostUsd);

public sealed record TokenDashboardSummary(
    long TotalTokensSpent,
    long PromptTokens,
    long CompletionTokens,
    long CachedPromptTokens,
    decimal EstimatedCostUsd,
    long TotalTokensSaved,
    decimal EstimatedSavingsUsd,
    int TotalOperations,
    int LiveLlmCalls,
    int ZeroTokenOps,
    double ZeroTokenRatePct,
    double CacheHitRatePct,
    double AvgTokensPerLlmCall,
    int AvgLatencyMs,
    long ProjectedMonthlyTokens,
    decimal ProjectedMonthlyCostUsd,
    int DaysWindow,
    bool IsAdminView,
    string ActiveProvider,
    IReadOnlyDictionary<string, string> ConfiguredDeployments,
    IReadOnlyList<TokenDailyPoint> DailySeries,
    IReadOnlyList<TokenOutcomeBucket> ByOutcome,
    IReadOnlyList<TokenFeatureBucket> ByFeature,
    IReadOnlyList<TokenModelBucket> ByModel,
    IReadOnlyList<TokenEntityBucket> ByUser,
    IReadOnlyList<TokenEntityBucket> ByCompany,
    IReadOnlyList<TokenUsageEvent> RecentEvents,
    IReadOnlyList<string> AvailableUsers,
    IReadOnlyList<string> AvailableCompanies,
    IReadOnlyList<TokenModelRate> ModelRates,
    Pipeline.AiPlanCacheStats? CacheStats);

public interface ITokenUsageStore
{
    Task RecordAsync(TokenUsageEvent entry, CancellationToken ct = default);
    Task<IReadOnlyList<TokenUsageEvent>> QueryAsync(TokenDashboardFilter filter, int maxRows = 5000, CancellationToken ct = default);
    Task<TokenDashboardSummary> GetSummaryAsync(
        TokenDashboardFilter filter,
        LlmOptions? llmOptions = null,
        Pipeline.AiPlanCacheStats? cacheStats = null,
        CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Persists AI token consumption and zero-token savings telemetry in PostgreSQL (audit.token_usage),
/// falling back seamlessly to thread-safe JSON storage (.local/token_usage.json) when offline or un-migrated.
/// </summary>
public sealed class SqlTokenUsageStore : ITokenUsageStore
{
    private const int MaxStoredEvents = 5000;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string? _appConnString;
    private readonly string _fallbackPath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private SqlTokenUsageStore(string? fallbackJsonPath)
    {
        _appConnString = null;
        _fallbackPath = fallbackJsonPath ?? Path.Combine(Directory.GetCurrentDirectory(), ".local", "token_usage.json");
    }

    public static SqlTokenUsageStore CreateFileBacked(string? fallbackJsonPath = null) => new(fallbackJsonPath);

    public SqlTokenUsageStore(IOptions<DatabaseOptions> dbOptions)
    {
        var cs = !string.IsNullOrWhiteSpace(dbOptions.Value.AppConnectionString)
            ? dbOptions.Value.AppConnectionString
            : dbOptions.Value.ReaderConnectionString;
        _appConnString = !string.IsNullOrWhiteSpace(cs) && !cs.Contains("Password=CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            ? cs
            : null;
        _fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), ".local", "token_usage.json");
    }

    public async Task RecordAsync(TokenUsageEvent entry, CancellationToken ct = default)
    {
        var clean = Sanitize(entry);

        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO audit.token_usage
                        (occurred_at_utc, user_name, company_code, feature, outcome, provider, model_role, deployment,
                         prompt_tokens, completion_tokens, cached_prompt_tokens, total_tokens, saved_tokens,
                         estimated_cost_usd, saved_cost_usd, duration_ms, is_estimated, question_preview)
                    VALUES
                        (@at, @u, @c, @f, @o, @p, @mr, @dep, @pt, @ct, @cpt, @tt, @st, @ec, @sc, @ms, @est, @q)
                    RETURNING event_id
                    """, conn);
                cmd.Parameters.AddWithValue("at", DateTime.SpecifyKind(clean.OccurredAtUtc, DateTimeKind.Unspecified));
                cmd.Parameters.AddWithValue("u", clean.UserName);
                cmd.Parameters.AddWithValue("c", clean.CompanyCode);
                cmd.Parameters.AddWithValue("f", clean.Feature);
                cmd.Parameters.AddWithValue("o", clean.Outcome);
                cmd.Parameters.AddWithValue("p", clean.Provider);
                cmd.Parameters.AddWithValue("mr", clean.ModelRole);
                cmd.Parameters.AddWithValue("dep", clean.Deployment);
                cmd.Parameters.AddWithValue("pt", clean.PromptTokens);
                cmd.Parameters.AddWithValue("ct", clean.CompletionTokens);
                cmd.Parameters.AddWithValue("cpt", clean.CachedPromptTokens);
                cmd.Parameters.AddWithValue("tt", clean.TotalTokens);
                cmd.Parameters.AddWithValue("st", clean.SavedTokens);
                cmd.Parameters.AddWithValue("ec", clean.EstimatedCostUsd);
                cmd.Parameters.AddWithValue("sc", clean.SavedCostUsd);
                cmd.Parameters.AddWithValue("ms", clean.DurationMs);
                cmd.Parameters.AddWithValue("est", clean.IsEstimated);
                cmd.Parameters.AddWithValue("q", clean.QuestionPreview);
                var id = await cmd.ExecuteScalarAsync(ct);
                if (id is long lid) entry.EventId = lid;
                return;
            }
            catch (NpgsqlException)
            {
                // Fall back cleanly to local JSON store if audit.token_usage is not deployed yet
            }
        }

        await _fileLock.WaitAsync(ct);
        try
        {
            var list = await ReadFallbackUnlockedAsync(ct);
            clean.EventId = list.Count == 0 ? 1 : list.Max(x => x.EventId) + 1;
            list.Add(clean);
            if (list.Count > MaxStoredEvents)
                list = list.OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.EventId).Take(MaxStoredEvents).Reverse().ToList();
            await WriteFallbackUnlockedAsync(list, ct);
            entry.EventId = clean.EventId;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<IReadOnlyList<TokenUsageEvent>> QueryAsync(TokenDashboardFilter filter, int maxRows = 5000, CancellationToken ct = default)
    {
        var allVisible = await LoadVisibleScopeAsync(filter, maxRows, ct);
        return ApplySelectionFilters(allVisible, filter)
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.EventId)
            .Take(Math.Clamp(maxRows, 1, MaxStoredEvents))
            .ToList();
    }

    public async Task<TokenDashboardSummary> GetSummaryAsync(
        TokenDashboardFilter filter,
        LlmOptions? llmOptions = null,
        Pipeline.AiPlanCacheStats? cacheStats = null,
        CancellationToken ct = default)
    {
        var scopeEvents = await LoadVisibleScopeAsync(filter, MaxStoredEvents, ct);
        var availableUsers = scopeEvents
            .Select(e => e.UserName)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var availableCompanies = scopeEvents
            .Select(e => e.CompanyCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var events = ApplySelectionFilters(scopeEvents, filter)
            .OrderByDescending(e => e.OccurredAtUtc)
            .ThenByDescending(e => e.EventId)
            .ToList();

        long totalTokensSpent = 0, promptTokens = 0, completionTokens = 0, cachedPromptTokens = 0, totalTokensSaved = 0;
        decimal estCostUsd = 0m, estSavingsUsd = 0m;
        var liveEvents = new List<TokenUsageEvent>();
        int zeroTokenOps = 0, cacheHits = 0, liveAiConfigureCalls = 0;

        foreach (var e in events)
        {
            totalTokensSpent += e.TotalTokens;
            promptTokens += e.PromptTokens;
            completionTokens += e.CompletionTokens;
            cachedPromptTokens += e.CachedPromptTokens;
            totalTokensSaved += e.SavedTokens;
            estCostUsd += e.EstimatedCostUsd;
            estSavingsUsd += e.SavedCostUsd;

            if (e.Outcome.Equals("LiveLlm", StringComparison.Ordinal))
            {
                liveEvents.Add(e);
                if (e.Feature.Equals("AiConfigure", StringComparison.OrdinalIgnoreCase))
                    liveAiConfigureCalls++;
            }
            else
            {
                zeroTokenOps++;
                if (e.Outcome.Equals("CacheHitMemory", StringComparison.Ordinal) ||
                    e.Outcome.Equals("CacheHitDatabase", StringComparison.Ordinal))
                {
                    cacheHits++;
                }
            }
        }

        var totalOps = events.Count;
        var liveCount = liveEvents.Count;
        var zeroPct = totalOps > 0 ? Math.Round(zeroTokenOps * 100.0 / totalOps, 1) : 0.0;
        var cacheDenom = cacheHits + liveAiConfigureCalls;
        var cacheHitPct = cacheDenom > 0 ? Math.Round(cacheHits * 100.0 / cacheDenom, 1) : 0.0;
        var avgTokensPerLlm = liveCount > 0 ? Math.Round((double)totalTokensSpent / liveCount, 1) : 0.0;
        var avgLatencyMs = liveCount > 0 ? (int)Math.Round(liveEvents.Average(e => e.DurationMs)) : 0;

        var dailySeries = BuildDailySeries(events, filter.Days);
        var (projTokens, projCost) = ComputeMonthlyProjection(events, totalTokensSpent, estCostUsd, filter.Days);

        var byOutcome = events
            .GroupBy(e => e.Outcome, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TokenOutcomeBucket(
                g.Key,
                FormatOutcomeLabel(g.Key),
                g.Count(),
                g.Sum(x => (long)x.TotalTokens),
                g.Sum(x => (long)x.SavedTokens),
                decimal.Round(g.Sum(x => x.EstimatedCostUsd), 6),
                decimal.Round(g.Sum(x => x.SavedCostUsd), 6)))
            .OrderByDescending(x => x.Count)
            .ToList();

        var byFeature = events
            .GroupBy(e => string.IsNullOrWhiteSpace(e.Feature) ? "Other" : e.Feature, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TokenFeatureBucket(
                g.Key,
                FormatFeatureLabel(g.Key),
                g.Count(),
                g.Sum(x => (long)x.PromptTokens),
                g.Sum(x => (long)x.CompletionTokens),
                g.Sum(x => (long)x.TotalTokens),
                g.Sum(x => (long)x.SavedTokens),
                decimal.Round(g.Sum(x => x.EstimatedCostUsd), 6)))
            .OrderByDescending(x => x.TotalTokens)
            .ThenByDescending(x => x.Calls)
            .ToList();

        var byModel = liveEvents
            .GroupBy(e => (Provider: string.IsNullOrWhiteSpace(e.Provider) ? "Unknown" : e.Provider,
                           Deployment: string.IsNullOrWhiteSpace(e.Deployment) ? "default" : e.Deployment))
            .Select(g => new TokenModelBucket(
                g.Key.Provider,
                g.Key.Deployment,
                g.Count(),
                g.Sum(x => (long)x.PromptTokens),
                g.Sum(x => (long)x.CompletionTokens),
                g.Sum(x => (long)x.TotalTokens),
                decimal.Round(g.Sum(x => x.EstimatedCostUsd), 6),
                (int)Math.Round(g.Average(x => x.DurationMs))))
            .OrderByDescending(x => x.TotalTokens)
            .ToList();

        var byUser = events
            .GroupBy(e => string.IsNullOrWhiteSpace(e.UserName) ? "(unknown)" : e.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TokenEntityBucket(
                g.Key,
                g.Count(x => x.Outcome == "LiveLlm"),
                g.Count(x => x.Outcome != "LiveLlm"),
                g.Sum(x => (long)x.TotalTokens),
                g.Sum(x => (long)x.SavedTokens),
                decimal.Round(g.Sum(x => x.EstimatedCostUsd), 6)))
            .OrderByDescending(x => x.TotalTokens + x.SavedTokens)
            .Take(15)
            .ToList();

        var byCompany = events
            .GroupBy(e => string.IsNullOrWhiteSpace(e.CompanyCode) ? "UNSCOPED" : e.CompanyCode.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new TokenEntityBucket(
                g.Key,
                g.Count(x => x.Outcome == "LiveLlm"),
                g.Count(x => x.Outcome != "LiveLlm"),
                g.Sum(x => (long)x.TotalTokens),
                g.Sum(x => (long)x.SavedTokens),
                decimal.Round(g.Sum(x => x.EstimatedCostUsd), 6)))
            .OrderByDescending(x => x.TotalTokens + x.SavedTokens)
            .Take(15)
            .ToList();

        return new TokenDashboardSummary(
            totalTokensSpent,
            promptTokens,
            completionTokens,
            cachedPromptTokens,
            decimal.Round(estCostUsd, 6),
            totalTokensSaved,
            decimal.Round(estSavingsUsd, 6),
            totalOps,
            liveCount,
            zeroTokenOps,
            zeroPct,
            cacheHitPct,
            avgTokensPerLlm,
            avgLatencyMs,
            projTokens,
            projCost,
            filter.Days,
            filter.IsAdmin,
            llmOptions?.Provider ?? "AzureOpenAI",
            llmOptions?.Deployments is { Count: > 0 } deps
                ? new Dictionary<string, string>(deps, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(),
            dailySeries,
            byOutcome,
            byFeature,
            byModel,
            byUser,
            byCompany,
            events.Take(30).ToList(),
            availableUsers,
            availableCompanies,
            TokenPricing.GetModelRatesTable(),
            cacheStats);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("DELETE FROM audit.token_usage", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (NpgsqlException)
            {
                // Ignore if un-migrated
            }
        }

        await _fileLock.WaitAsync(ct);
        try
        {
            if (File.Exists(_fallbackPath))
                File.Delete(_fallbackPath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<List<TokenUsageEvent>> LoadVisibleScopeAsync(TokenDashboardFilter filter, int maxRows, CancellationToken ct)
    {
        var cutoff = filter.Days > 0 ? DateTime.UtcNow.AddDays(-filter.Days) : DateTime.MinValue;
        List<TokenUsageEvent> raw;

        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                var rows = new List<TokenUsageEvent>();
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                var sql = """
                    SELECT event_id, occurred_at_utc, user_name, company_code, feature, outcome, provider, model_role, deployment,
                           prompt_tokens, completion_tokens, cached_prompt_tokens, total_tokens, saved_tokens,
                           estimated_cost_usd, saved_cost_usd, duration_ms, is_estimated, question_preview
                    FROM audit.token_usage
                    WHERE (@allDays = true OR occurred_at_utc >= @cutoff)
                      AND (@isAdmin = true OR LOWER(user_name) = LOWER(@reqUser))
                    ORDER BY occurred_at_utc DESC, event_id DESC
                    LIMIT @lim
                    """;
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("allDays", filter.Days <= 0);
                cmd.Parameters.AddWithValue("cutoff", DateTime.SpecifyKind(cutoff == DateTime.MinValue ? DateTime.UtcNow.AddYears(-10) : cutoff, DateTimeKind.Unspecified));
                cmd.Parameters.AddWithValue("isAdmin", filter.IsAdmin);
                cmd.Parameters.AddWithValue("reqUser", filter.RequestingUserName);
                cmd.Parameters.AddWithValue("lim", Math.Clamp(maxRows, 1, MaxStoredEvents));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    rows.Add(ReadRow(r));
                return ApplyRbacVisibility(rows, filter);
            }
            catch (NpgsqlException)
            {
                // Fall back to local JSON store
            }
        }

        raw = await ReadFallbackAsync(ct);
        var windowed = raw.Where(e =>
            (filter.Days <= 0 || e.OccurredAtUtc >= cutoff) &&
            (filter.IsAdmin || e.UserName.Equals(filter.RequestingUserName, StringComparison.OrdinalIgnoreCase)));
        return ApplyRbacVisibility(windowed, filter);
    }

    private static List<TokenUsageEvent> ApplyRbacVisibility(IEnumerable<TokenUsageEvent> events, TokenDashboardFilter filter)
    {
        if (filter.IsAdmin || filter.AllowedCompanies is null || filter.AllowedCompanies.Count == 0)
            return events.ToList();

        return events.Where(e =>
            string.IsNullOrWhiteSpace(e.CompanyCode) ||
            e.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase) ||
            filter.AllowedCompanies.Contains(e.CompanyCode)).ToList();
    }

    private static IEnumerable<TokenUsageEvent> ApplySelectionFilters(IEnumerable<TokenUsageEvent> events, TokenDashboardFilter filter)
    {
        var q = events;
        if (!string.IsNullOrWhiteSpace(filter.CompanyCode) && !filter.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            q = q.Where(e => e.CompanyCode.Equals(filter.CompanyCode.Trim(), StringComparison.OrdinalIgnoreCase));

        if (filter.IsAdmin && !string.IsNullOrWhiteSpace(filter.FilterUserName) && !filter.FilterUserName.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            q = q.Where(e => e.UserName.Equals(filter.FilterUserName.Trim(), StringComparison.OrdinalIgnoreCase));

        return q;
    }

    private static List<TokenDailyPoint> BuildDailySeries(List<TokenUsageEvent> events, int daysWindow)
    {
        if (events.Count == 0) return [];

        var today = DateTime.UtcNow.Date;
        var minEventDay = events.Min(e => e.OccurredAtUtc.Date);
        var startDay = daysWindow > 0
            ? (daysWindow == 1 ? minEventDay : (minEventDay > today.AddDays(-(daysWindow - 1)) ? today.AddDays(-(daysWindow - 1)) : minEventDay))
            : minEventDay;
        if ((today - startDay).TotalDays > 120)
            startDay = today.AddDays(-120);

        var byDay = events
            .GroupBy(e => e.OccurredAtUtc.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        var series = new List<TokenDailyPoint>();
        for (var d = startDay; d <= today; d = d.AddDays(1))
        {
            if (byDay.TryGetValue(d, out var dayEvents))
            {
                series.Add(new TokenDailyPoint(
                    d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    dayEvents.Sum(x => (long)x.PromptTokens),
                    dayEvents.Sum(x => (long)x.CompletionTokens),
                    dayEvents.Sum(x => (long)x.CachedPromptTokens),
                    dayEvents.Sum(x => (long)x.SavedTokens),
                    decimal.Round(dayEvents.Sum(x => x.EstimatedCostUsd), 6),
                    decimal.Round(dayEvents.Sum(x => x.SavedCostUsd), 6),
                    dayEvents.Count(x => x.Outcome == "LiveLlm"),
                    dayEvents.Count(x => x.Outcome != "LiveLlm")));
            }
            else
            {
                series.Add(new TokenDailyPoint(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 0, 0, 0, 0, 0m, 0m, 0, 0));
            }
        }
        return series;
    }

    private static (long ProjectedTokens, decimal ProjectedCostUsd) ComputeMonthlyProjection(
        List<TokenUsageEvent> events,
        long totalTokensSpent,
        decimal estCostUsd,
        int daysWindow)
    {
        if (events.Count == 0 || totalTokensSpent == 0) return (0L, 0m);
        var today = DateTime.UtcNow.Date;
        var minDay = events.Min(e => e.OccurredAtUtc.Date);
        var elapsed = Math.Max(1.0, (today - minDay).TotalDays + 1.0);
        if (daysWindow > 0 && elapsed > daysWindow) elapsed = daysWindow;
        var scale = 30.0 / elapsed;
        return ((long)Math.Round(totalTokensSpent * scale), decimal.Round(estCostUsd * (decimal)scale, 4));
    }

    private static string FormatOutcomeLabel(string outcome) => outcome switch
    {
        "LiveLlm" => "Live AI Call",
        "CacheHitMemory" => "Memory Cache Hit (0 tok)",
        "CacheHitDatabase" => "DB Cache Hit (0 tok)",
        "SavedReportReplay" => "Saved Replay (0 tok)",
        "GuardrailBlocked" => "Pre-LLM Guardrail (0 tok)",
        "DirectRun" => "Direct Form SQL (0 tok)",
        _ => outcome
    };

    private static string FormatFeatureLabel(string feature) => feature switch
    {
        "AiConfigure" => "AI Query Planner",
        "ReconcileSummary" => "Reconciliation Summary",
        "ViewDraft" => "AI View Draft",
        "SavedReplay" => "Saved Report Replay",
        "DirectRun" => "Direct Report Builder",
        _ => feature
    };

    private static TokenUsageEvent Sanitize(TokenUsageEvent src) => new()
    {
        OccurredAtUtc = src.OccurredAtUtc == default ? DateTime.UtcNow : src.OccurredAtUtc.ToUniversalTime(),
        UserName = TokenCallScope.TruncatePreview(src.UserName, 120),
        CompanyCode = TokenCallScope.TruncatePreview(src.CompanyCode, 30),
        Feature = TokenCallScope.TruncatePreview(src.Feature, 40),
        Outcome = TokenCallScope.TruncatePreview(src.Outcome, 30),
        Provider = TokenCallScope.TruncatePreview(src.Provider, 40),
        ModelRole = TokenCallScope.TruncatePreview(src.ModelRole, 30),
        Deployment = TokenCallScope.TruncatePreview(src.Deployment, 80),
        PromptTokens = Math.Max(0, src.PromptTokens),
        CompletionTokens = Math.Max(0, src.CompletionTokens),
        CachedPromptTokens = Math.Max(0, src.CachedPromptTokens),
        TotalTokens = Math.Max(0, src.TotalTokens),
        SavedTokens = Math.Max(0, src.SavedTokens),
        EstimatedCostUsd = Math.Max(0m, src.EstimatedCostUsd),
        SavedCostUsd = Math.Max(0m, src.SavedCostUsd),
        DurationMs = Math.Max(0, src.DurationMs),
        IsEstimated = src.IsEstimated,
        QuestionPreview = TokenCallScope.TruncatePreview(src.QuestionPreview, 220)
    };

    private static TokenUsageEvent ReadRow(NpgsqlDataReader r) => new()
    {
        EventId = r.GetInt64(0),
        OccurredAtUtc = DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc),
        UserName = r.GetString(2),
        CompanyCode = r.GetString(3),
        Feature = r.GetString(4),
        Outcome = r.GetString(5),
        Provider = r.GetString(6),
        ModelRole = r.GetString(7),
        Deployment = r.GetString(8),
        PromptTokens = r.GetInt32(9),
        CompletionTokens = r.GetInt32(10),
        CachedPromptTokens = r.GetInt32(11),
        TotalTokens = r.GetInt32(12),
        SavedTokens = r.GetInt32(13),
        EstimatedCostUsd = r.GetDecimal(14),
        SavedCostUsd = r.GetDecimal(15),
        DurationMs = r.GetInt32(16),
        IsEstimated = r.GetBoolean(17),
        QuestionPreview = r.GetString(18)
    };

    private async Task<List<TokenUsageEvent>> ReadFallbackAsync(CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try { return await ReadFallbackUnlockedAsync(ct); }
        finally { _fileLock.Release(); }
    }

    private async Task<List<TokenUsageEvent>> ReadFallbackUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_fallbackPath)) return [];
        var json = await File.ReadAllTextAsync(_fallbackPath, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<TokenUsageEvent>>(json, JsonOpts) ?? [];
    }

    private async Task WriteFallbackUnlockedAsync(List<TokenUsageEvent> list, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_fallbackPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(list, JsonOpts);
        await File.WriteAllTextAsync(_fallbackPath, json, ct);
    }
}
