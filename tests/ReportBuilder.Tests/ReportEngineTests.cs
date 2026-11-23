using ReportBuilder.Core;
using ReportBuilder.Core.Data;
using ReportBuilder.Core.Llm;
using ReportBuilder.Core.Pipeline;
using ReportBuilder.Core.Security;
using ReportEngine.Web.Services;
using Xunit;

namespace ReportBuilder.Tests;

public class ReportEngineTests
{
    private static readonly ErpAreaViewMeta SalesView = new(
        "sales_invoice_item_v",
        "Sales & Inventory",
        "Sales Invoices & Items",
        "Sales lines",
        "invoice_date",
        ["company_code", "customer_name", "item_name", "quantity", "net_amount"],
        "credit sales, top selling inventory",
        [
            new("company_code", "text"),
            new("invoice_date", "date"),
            new("customer_name", "text"),
            new("payment_mode", "text"),
            new("item_code", "text"),
            new("item_name", "text"),
            new("quantity", "numeric"),
            new("net_amount", "numeric"),
            new("tax_registration_no", "text")
        ]);

    [Fact]
    public void Rbac_resolves_from_roles_claims_company_roles_and_menuid()
    {
        var rules = new List<RbacRule>
        {
            new() { Id = 1, MenuId = 2, ViewName = "sales_invoice_item_v", BlockedColumns = "tax_registration_no" },
            new() { Id = 2, RoleName = "Finance", ViewName = "trial_balance_v" },
            new() { Id = 3, ClaimType = "module", ClaimValue = "gl", ViewName = "general_ledger_v" }
        };

        var session = ErpSecurityProvider.ResolveFromData(
            "jdoe", "John Doe", isActive: true, viewAllFlags: false, userType: 1,
            roles: ["Sales"],
            roleClaims: [("module", "gl")],
            companyRoles: ["Finance"],
            companies: [new("TGTM", "Thomsun General Trading", "", true)],
            menuIds: [2],
            rules: rules,
            globalBlockedColumns: ["date_of_birth"]);

        Assert.True(session.AllowsCompany("TGTM"));
        Assert.False(session.AllowsCompany("CAP"));
        Assert.True(session.AllowsView("sales_invoice_item_v"));
        Assert.True(session.AllowsView("trial_balance_v"));
        Assert.True(session.AllowsView("general_ledger_v"));
        Assert.False(session.AllowsView("employee_v"));
        Assert.Contains("tax_registration_no", session.BlockedColumns);
        Assert.Contains("date_of_birth", session.BlockedColumns);
    }

    [Fact]
    public void Tenant_matcher_resolves_by_company_code_and_host()
    {
        var cfg = new TenantsConfig
        {
            Tenants =
            [
                new TenantEntryConfig { Host = "CAP", Name = "Capricorn", ReportConnection = "Host=192.168.100.53;Database=ChefDB-QA" },
                new TenantEntryConfig { Host = "phfinance.capricornbakery.com", Name = "TPH", ReportConnection = "Host=192.168.100.53;Database=ThePrintHouse" }
            ]
        };

        var cap = CompanyDataSourceFactory.MatchTenant(cfg, new("CAP", "Capricorn", "finance.capricornbakery.com", true));
        var tph = CompanyDataSourceFactory.MatchTenant(cfg, new("TPH", "Print House", "phfinance.capricornbakery.com", false));

        Assert.NotNull(cap);
        Assert.Contains("ChefDB-QA", cap!.ReportConnection);
        Assert.NotNull(tph);
        Assert.Contains("ThePrintHouse", tph!.ReportConnection);
    }

    [Fact]
    public void Dynamic_compiler_builds_parameterized_select_with_last_3_quarters_and_company_scope()
    {
        var session = new ErpUserSession
        {
            UserName = "alice",
            IsActive = true,
            AllowedCompanies = [new("TGTM", "TGTM", "", true)],
            AllowedViews = new HashSet<string>(["sales_invoice_item_v"], StringComparer.OrdinalIgnoreCase),
            BlockedColumns = new HashSet<string>(["tax_registration_no"], StringComparer.OrdinalIgnoreCase)
        };

        var req = new DynamicReportRequest
        {
            ViewName = "sales_invoice_item_v",
            CompanyCode = "TGTM",
            SelectedColumns = ["item_code", "item_name"],
            Aggregations = [new AggregationSpec { Column = "quantity", Function = "SUM", Alias = "total_qty" }],
            Filters = [new FilterSpec { Column = "payment_mode", Op = "=", Value = "Credit" }],
            RelativePeriod = "last_3_quarters",
            OrderBy = [new SortSpec { Column = "total_qty", Direction = "DESC" }],
            Limit = 25
        };

        var res = DynamicReportCompiler.Compile(req, session, SalesView, new DateTime(2026, 11, 20));
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.NotNull(res.Query);
        Assert.Contains("SELECT item_code, item_name, SUM(quantity) AS total_qty FROM rpt.sales_invoice_item_v", res.Query!.Sql);
        Assert.Contains("company_code = @p0", res.Query.Sql);
        Assert.Contains("payment_mode = @p1", res.Query.Sql);
        Assert.Contains("invoice_date >= @p2", res.Query.Sql);
        Assert.Equal("2026-02-20", res.Query.Parameters["p2"]);
        Assert.Contains("GROUP BY item_code, item_name ORDER BY total_qty DESC LIMIT 25", res.Query.Sql);
    }

    [Fact]
    public void Dynamic_compiler_supports_having_filters_on_aggregation_aliases()
    {
        var session = new ErpUserSession
        {
            UserName = "alice",
            IsActive = true,
            AllowedCompanies = [new("TGTM", "TGTM", "", true)],
            AllowedViews = new HashSet<string>(["sales_invoice_item_v"], StringComparer.OrdinalIgnoreCase)
        };
        var req = new DynamicReportRequest
        {
            ViewName = "sales_invoice_item_v",
            CompanyCode = "TGTM",
            SelectedColumns = ["customer_name"],
            Aggregations = [new AggregationSpec { Column = "net_amount", Function = "SUM", Alias = "total_sales" }],
            HavingFilters = [new HavingFilterSpec { Alias = "total_sales", Op = ">", Value = 50000m }]
        };
        var res = DynamicReportCompiler.Compile(req, session, SalesView);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.Contains("HAVING SUM(net_amount) > @p1", res.Query!.Sql);
        Assert.Equal(50000m, res.Query.Parameters["p1"]);
    }

    [Fact]
    public void Dynamic_compiler_includes_order_by_raw_columns_in_group_by()
    {
        var session = new ErpUserSession
        {
            UserName = "alice",
            IsActive = true,
            AllowedCompanies = [new("CAP", "CAP", "", true)],
            AllowedViews = new HashSet<string>(["sales_invoice_item_v"], StringComparer.OrdinalIgnoreCase)
        };
        var req = new DynamicReportRequest
        {
            ViewName = "sales_invoice_item_v",
            CompanyCode = "CAP",
            SelectedColumns = ["customer_name"],
            Aggregations = [new AggregationSpec { Column = "quantity", Function = "SUM", Alias = "total_qty" }],
            OrderBy = [new SortSpec { Column = "net_amount", Direction = "DESC" }]
        };
        var res = DynamicReportCompiler.Compile(req, session, SalesView);
        Assert.True(res.Ok, string.Join("; ", res.Errors));
        Assert.Contains("GROUP BY customer_name, net_amount", res.Query!.Sql);
    }

    [Fact]
    public void Dynamic_compiler_blocks_unauthorized_company_view_and_columns()
    {
        var session = new ErpUserSession
        {
            UserName = "bob",
            IsActive = true,
            AllowedCompanies = [new("CAP", "Capricorn", "", true)],
            AllowedViews = new HashSet<string>(["company_v"], StringComparer.OrdinalIgnoreCase),
            BlockedColumns = new HashSet<string>(["tax_registration_no"], StringComparer.OrdinalIgnoreCase)
        };

        var badView = DynamicReportCompiler.Compile(new DynamicReportRequest { ViewName = "sales_invoice_item_v", CompanyCode = "CAP" }, session, SalesView);
        Assert.False(badView.Ok);

        var sessionWithView = new ErpUserSession
        {
            UserName = "bob",
            IsActive = true,
            AllowedCompanies = [new("CAP", "Capricorn", "", true)],
            AllowedViews = new HashSet<string>(["sales_invoice_item_v"], StringComparer.OrdinalIgnoreCase),
            BlockedColumns = new HashSet<string>(["tax_registration_no"], StringComparer.OrdinalIgnoreCase)
        };

        var badComp = DynamicReportCompiler.Compile(new DynamicReportRequest { ViewName = "sales_invoice_item_v", CompanyCode = "TGTM" }, sessionWithView, SalesView);
        Assert.False(badComp.Ok);

        var badCol = DynamicReportCompiler.Compile(new DynamicReportRequest
        {
            ViewName = "sales_invoice_item_v",
            CompanyCode = "CAP",
            SelectedColumns = ["tax_registration_no"]
        }, sessionWithView, SalesView);
        Assert.False(badCol.Ok);
    }

    [Fact]
    public async Task Ai_assistant_outputs_reconcile_or_query_spec_without_db_access()
    {
        var fakeLlm = new StaticJsonLlm("""
            {
              "is_reconcile": true,
              "reconcile_areas": ["AR", "AP"],
              "view_name": "",
              "selected_columns": [],
              "aggregations": [],
              "filters": [],
              "relative_period": null,
              "order_by": [],
              "limit": 100,
              "chart_type": "auto",
              "explanation": "Routing to deterministic GL vs subledger reconciler.",
              "missing_data_note": null
            }
            """);
        var ai = new AiReportAssistant(fakeLlm);
        var res = await ai.ConfigureAsync("reasons for difference between ledger and trial balance", "TGTM", [SalesView]);
        Assert.True(res.IsReconcile);
        Assert.Equal(["AR", "AP"], res.ReconcileAreas);
    }

    [Fact]
    public void Exporters_produce_csv_json_excel_and_pdf()
    {
        var qr = new QueryResult
        {
            Columns = ["item_code", "item_name", "total_qty"],
            Rows = [["ITM-01", "Widget A", 125m], ["ITM-02", "Widget B", 80m]]
        };

        var csv = ReportExporters.ToCsv(qr);
        var json = ReportExporters.ToJson(qr, "Top Inventory", "TGTM");
        var xlsx = ReportExporters.ToExcel(qr, "Top Inventory", "TGTM");
        var pdf = ReportExporters.ToPdf(qr, "Top Inventory", "TGTM");

        Assert.Contains("Widget A", System.Text.Encoding.UTF8.GetString(csv));
        Assert.Contains("Top Inventory", System.Text.Encoding.UTF8.GetString(json));
        Assert.True(xlsx.Length > 1000);
        Assert.Equal((byte)'%', pdf[0]); // %PDF header
        Assert.Equal((byte)'P', pdf[1]);
    }

    [Fact]
    public void Tag_company_rows_replaces_all_and_prepends_when_missing()
    {
        var withCol = new QueryResult
        {
            Columns = ["company_code", "customer_name", "total_sales"],
            Rows = [["ALL", "Acme", 100m], ["TGTM", "Beta", 200m]]
        };
        var tagged1 = CompanyDataSourceFactory.TagCompanyRows(withCol, "CAP");
        Assert.Equal(["company_code", "customer_name", "total_sales"], tagged1.Columns);
        Assert.Equal("CAP", tagged1.Rows[0][0]);
        Assert.Equal("TGTM", tagged1.Rows[1][0]);

        var withoutCol = new QueryResult
        {
            Columns = ["customer_name", "total_sales"],
            Rows = [["Acme", 100m]]
        };
        var tagged2 = CompanyDataSourceFactory.TagCompanyRows(withoutCol, "TPH");
        Assert.Equal(["company_code", "customer_name", "total_sales"], tagged2.Columns);
        Assert.Equal("TPH", tagged2.Rows[0][0]);
        Assert.Equal("Acme", tagged2.Rows[0][1]);
        Assert.Equal(100m, tagged2.Rows[0][2]);
    }

    [Fact]
    public void Group_pack_pdf_exporter_handles_populated_and_empty_sections()
    {
        var sections = new List<GroupPackSection>
        {
            new("Sales & Revenue", "Group Top Customers", "Show top customers", "sales_invoice_item_v", "ALL",
                new QueryResult { Columns = ["company_code", "customer_name", "total_net"], Rows = [["CAP", "Acme", 12500.50m]] }),
            new("Inventory", "Warehouse Stock", "Show stock", "stock_on_hand_v", "ALL",
                new QueryResult(), "No records found.")
        };

        var pdf = ReportExporters.ToGroupPackPdf(sections, "Admin (admin)", ["CAP", "TPH"]);
        Assert.True(pdf.Length > 2000);
        Assert.Equal((byte)'%', pdf[0]);
        Assert.Equal((byte)'P', pdf[1]);
        Assert.Equal((byte)'D', pdf[2]);
        Assert.Equal((byte)'F', pdf[3]);

        var singlePdf = ReportExporters.ToGroupPackPdf(
            sections, "Admin (admin)", ["CAP"],
            packTitle: "TERO ERP — Single-Company Report Pack (CAP — Capricorn)",
            scopeHeader: "Company: CAP — Capricorn");
        Assert.True(singlePdf.Length > 2000);
        Assert.Equal((byte)'%', singlePdf[0]);
        Assert.Equal((byte)'P', singlePdf[1]);
    }

    [Fact]
    public void Query_catalog_pdfs_render_without_db_and_have_expected_entry_counts()
    {
        var (groupPdf, groupEntries) = GroupReportPackService.BuildQueryCatalogPdf(singleCompany: false);
        Assert.Equal(20, groupEntries.Count);
        Assert.True(groupPdf.Length > 2000);
        Assert.Equal((byte)'%', groupPdf[0]);
        Assert.Equal((byte)'P', groupPdf[1]);

        var (singlePdf, singleEntries) = GroupReportPackService.BuildQueryCatalogPdf(singleCompany: true);
        Assert.Equal(23, singleEntries.Count);
        Assert.True(singlePdf.Length > 2000);
        Assert.Equal((byte)'%', singlePdf[0]);
        Assert.Equal((byte)'P', singlePdf[1]);
    }

    private sealed class StaticJsonLlm(string json) : ILlmClient
    {
        public Task<string> CompleteTextAsync(ModelRole role, string system, string user, CancellationToken ct = default) =>
            Task.FromResult(json);

        public Task<T> CompleteJsonAsync<T>(ModelRole role, string system, string user, string schemaName, string jsonSchema, CancellationToken ct = default) =>
            Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(json)!);
    }
}
