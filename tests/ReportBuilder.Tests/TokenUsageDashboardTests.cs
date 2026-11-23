using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReportBuilder.Core;
using ReportBuilder.Core.Data;
using ReportBuilder.Core.Llm;
using ReportBuilder.Core.Pipeline;
using Xunit;

namespace ReportBuilder.Tests;

public class TokenUsageDashboardTests
{
    private static readonly ErpAreaViewMeta SalesView = new(
        "sales_invoice_item_v",
        "Sales & Inventory",
        "Sales Invoices & Items",
        "Sales invoice lines",
        "invoice_date",
        ["company_code", "customer_name", "net_amount"],
        "credit sales",
        [
            new("company_code", "text"),
            new("invoice_date", "date"),
            new("customer_name", "text"),
            new("payment_mode", "text"),
            new("net_amount", "numeric")
        ]);

    [Fact]
    public void TokenPricing_computes_azure_cached_and_ollama_rates_accurately()
    {
        // gpt-4o: 2.50 / 1M uncached input, 1.25 / 1M cached input, 10.00 / 1M output
        var cost4o = TokenPricing.EstimateCostUsd("AzureOpenAI", "gpt-4o", promptTokens: 1200, completionTokens: 200, cachedPromptTokens: 200);
        // (1000 * 2.50 + 200 * 1.25 + 200 * 10.00) / 1_000_000 = 0.00475
        Assert.Equal(0.004750m, cost4o);

        // gpt-4o-mini must match mini rate (0.15 / 1M), NOT gpt-4o (2.50 / 1M)
        var costMini = TokenPricing.EstimateCostUsd("AzureOpenAI", "gpt-4o-mini", promptTokens: 1000, completionTokens: 100, cachedPromptTokens: 0);
        Assert.Equal(0.000210m, costMini);

        // Ollama / OpenAICompatible always $0
        Assert.Equal(0m, TokenPricing.EstimateCostUsd("Ollama", "gemma3:latest", 5000, 1000, 0));
    }

    [Fact]
    public async Task SqlTokenUsageStore_computes_kpis_daily_series_breakdowns_and_rbac_filters()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"token_usage_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = SqlTokenUsageStore.CreateFileBacked(tempPath);
            var now = DateTime.UtcNow;

            await store.RecordAsync(new TokenUsageEvent
            {
                OccurredAtUtc = now.AddDays(-1),
                UserName = "alice",
                CompanyCode = "CAP",
                Feature = "AiConfigure",
                Outcome = "LiveLlm",
                Provider = "AzureOpenAI",
                ModelRole = "Planner",
                Deployment = "gpt-4o",
                PromptTokens = 1400,
                CompletionTokens = 200,
                TotalTokens = 1600,
                EstimatedCostUsd = 0.0055m,
                DurationMs = 900,
                QuestionPreview = "Show CAP sales"
            });

            await store.RecordAsync(new TokenUsageEvent
            {
                OccurredAtUtc = now,
                UserName = "alice",
                CompanyCode = "CAP",
                Feature = "AiConfigure",
                Outcome = "CacheHitMemory",
                Provider = "AzureOpenAI",
                ModelRole = "Planner",
                Deployment = "gpt-4o",
                TotalTokens = 0,
                SavedTokens = 1600,
                SavedCostUsd = 0.0055m,
                DurationMs = 4,
                IsEstimated = true,
                QuestionPreview = "Show CAP sales"
            });

            await store.RecordAsync(new TokenUsageEvent
            {
                OccurredAtUtc = now,
                UserName = "alice",
                CompanyCode = "CAP",
                Feature = "SavedReplay",
                Outcome = "SavedReportReplay",
                Provider = "AzureOpenAI",
                TotalTokens = 0,
                SavedTokens = 1600,
                SavedCostUsd = 0.0055m,
                DurationMs = 42,
                IsEstimated = true,
                QuestionPreview = "Monthly CAP Sales"
            });

            await store.RecordAsync(new TokenUsageEvent
            {
                OccurredAtUtc = now,
                UserName = "bob",
                CompanyCode = "TGTM",
                Feature = "AiConfigure",
                Outcome = "GuardrailBlocked",
                Provider = "AzureOpenAI",
                TotalTokens = 0,
                SavedTokens = 1600,
                SavedCostUsd = 0.0055m,
                DurationMs = 2,
                IsEstimated = true,
                QuestionPreview = "Tell me a joke"
            });

            // Admin view sees all 4 events across Alice + Bob
            var adminSummary = await store.GetSummaryAsync(
                new TokenDashboardFilter(30, null, null, "admin", IsAdmin: true));
            Assert.Equal(4, adminSummary.TotalOperations);
            Assert.Equal(1, adminSummary.LiveLlmCalls);
            Assert.Equal(3, adminSummary.ZeroTokenOps);
            Assert.Equal(75.0, adminSummary.ZeroTokenRatePct);
            Assert.Equal(50.0, adminSummary.CacheHitRatePct); // 1 cache hit / (1 cache hit + 1 live AiConfigure)
            Assert.Equal(1600, adminSummary.TotalTokensSpent);
            Assert.Equal(4800, adminSummary.TotalTokensSaved);
            Assert.True(adminSummary.DailySeries.Count >= 2);
            Assert.Equal(["alice", "bob"], adminSummary.AvailableUsers.ToArray());
            Assert.Equal(["CAP", "TGTM"], adminSummary.AvailableCompanies.ToArray());
            Assert.True(adminSummary.ByOutcome.Count >= 4);
            Assert.True(adminSummary.ByUser.Count == 2);

            // Non-admin Alice is strictly isolated to Alice's CAP events even if requesting Bob
            var aliceSummary = await store.GetSummaryAsync(
                new TokenDashboardFilter(30, null, "bob", "alice", IsAdmin: false,
                    new HashSet<string>(["CAP"], StringComparer.OrdinalIgnoreCase)));
            Assert.Equal(3, aliceSummary.TotalOperations);
            Assert.Equal(1600, aliceSummary.TotalTokensSpent);
            Assert.All(aliceSummary.RecentEvents, e => Assert.Equal("alice", e.UserName));
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task AzureOpenAiClient_captures_usage_and_AiPlanCache_records_zero_token_savings()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"token_usage_client_{Guid.NewGuid():N}.json");
        try
        {
            var store = SqlTokenUsageStore.CreateFileBacked(tempPath);
            var payload = """
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"is_out_of_scope\":false,\"refusal_reason\":null,\"is_reconcile\":false,\"reconcile_areas\":[],\"view_name\":\"sales_invoice_item_v\",\"selected_columns\":[\"customer_name\"],\"aggregations\":[{\"column\":\"net_amount\",\"function\":\"SUM\",\"alias\":\"total_sales\"}],\"filters\":[],\"having_filters\":[],\"relative_period\":\"last_3_quarters\",\"order_by\":[{\"column\":\"total_sales\",\"direction\":\"DESC\"}],\"limit\":25,\"chart_type\":\"bar\",\"explanation\":\"Configured Sales Invoices & Items for total_sales.\",\"missing_data_note\":null}"
                      }
                    }
                  ],
                  "usage": {
                    "prompt_tokens": 1250,
                    "completion_tokens": 150,
                    "total_tokens": 1400,
                    "prompt_tokens_details": { "cached_tokens": 250 }
                  }
                }
                """;

            using var http = new HttpClient(new StubHandler(payload));
            var llmOpts = Options.Create(new LlmOptions
            {
                Provider = "AzureOpenAI",
                Endpoint = "https://example.openai.azure.com",
                ApiKey = "test-key",
                Deployments = new Dictionary<string, string> { ["planner"] = "gpt-4o" }
            });

            var client = new AzureOpenAiClient(http, llmOpts, store);
            var cache = new AiPlanCache();
            var assistant = new AiReportAssistant(client, cache);

            AiConfigureResult first;
            using (TokenUsageContext.Begin("alice", "CAP", "AiConfigure", "Show sales by customer"))
            {
                first = await assistant.ConfigureAsync("Show sales by customer", "CAP", [SalesView]);
            }

            Assert.False(first.FromCache);
            Assert.Equal(1250, first.PromptTokens);
            Assert.Equal(150, first.CompletionTokens);
            Assert.Equal(250, first.CachedPromptTokens);
            Assert.Equal(1400, first.TotalTokens);
            Assert.Equal("gpt-4o", first.ModelDeployment);
            Assert.True(first.EstimatedCostUsd > 0m);

            var second = await assistant.ConfigureAsync("show sales by customer?", "CAP", [SalesView]);
            Assert.True(second.FromCache);
            Assert.Equal(0, second.TotalTokens);
            Assert.Equal(0m, second.EstimatedCostUsd);
            Assert.Equal(1400, second.SavedTokens);
            Assert.Equal(first.EstimatedCostUsd, second.SavedCostUsd);

            var events = await store.QueryAsync(new TokenDashboardFilter(30, null, null, "admin", IsAdmin: true));
            var recorded = Assert.Single(events);
            Assert.Equal("alice", recorded.UserName);
            Assert.Equal("CAP", recorded.CompanyCode);
            Assert.Equal("LiveLlm", recorded.Outcome);
            Assert.Equal(1400, recorded.TotalTokens);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void Di_container_resolves_token_usage_store_and_llm_client_unambiguously()
    {
        var services = new ServiceCollection();
        services.AddReportBuilder(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SqlTokenUsageStore>(provider.GetRequiredService<ITokenUsageStore>());
        Assert.IsType<AzureOpenAiClient>(provider.GetRequiredService<ILlmClient>());
        Assert.NotNull(provider.GetRequiredService<AiPlanCache>());
        Assert.NotNull(provider.GetRequiredService<ISavedReportStore>());
        Assert.NotNull(provider.GetRequiredService<AiReportAssistant>());
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
