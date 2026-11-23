using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReportBuilder.Core.Catalog;
using ReportBuilder.Core.Data;
using ReportBuilder.Core.Generator;
using ReportBuilder.Core.Llm;
using ReportBuilder.Core.Pipeline;
using ReportBuilder.Core.Security;
using ReportBuilder.Core.Views;

namespace ReportBuilder.Core;

public static class ServiceRegistration
{
    public static IServiceCollection AddReportBuilder(this IServiceCollection s, IConfiguration c)
    {
        s.Configure<LlmOptions>(c.GetSection("Llm"));
        s.Configure<DatabaseOptions>(c.GetSection("Database"));
        s.Configure<PolicyOptions>(c.GetSection("Policy"));
        s.Configure<DesignerOptions>(c.GetSection("Designer"));
        s.Configure<TenantsConfig>(c);

        s.AddHttpClient<ILlmClient, AzureOpenAiClient>(h => h.Timeout = TimeSpan.FromSeconds(120));

        s.AddSingleton<ErpSecurityProvider>();
        s.AddSingleton<CompanyDataSourceFactory>();
        s.AddSingleton<AiPlanCache>();
        s.AddSingleton<ISavedReportStore, SqlSavedReportStore>();
        s.AddSingleton<AiReportAssistant>();

        s.AddSingleton<SqlQueryExecutor>();
        s.AddSingleton<IQueryExecutor>(p => p.GetRequiredService<SqlQueryExecutor>());
        s.AddSingleton<IReconciliationSource>(p => p.GetRequiredService<SqlQueryExecutor>());
        s.AddSingleton<IDraftPreview>(p => p.GetRequiredService<SqlQueryExecutor>());
        s.AddSingleton<ICatalogStore, SqlCatalogStore>();
        s.AddSingleton<IAuditStore, SqlAuditStore>();
        s.AddSingleton<IViewStore, SqlViewStore>();

        s.AddSingleton<IntentClassifier>();
        s.AddSingleton<CatalogMapper>();
        s.AddSingleton<ReportPlanner>();
        s.AddSingleton<QueryCompiler>();
        s.AddSingleton<PolicyGate>();
        s.AddSingleton<Optimizer>();
        s.AddSingleton<ResultValidator>();
        s.AddSingleton<Reconciler>();
        s.AddSingleton<Presenter>();
        s.AddSingleton<ReportPipeline>();

        s.AddSingleton<ICatalogDrafter, LlmCatalogDrafter>();
        s.AddSingleton<DraftValidator>();
        s.AddSingleton<CatalogGenerator>();
        return s;
    }
}
