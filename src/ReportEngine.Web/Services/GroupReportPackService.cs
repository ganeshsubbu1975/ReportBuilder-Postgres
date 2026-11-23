using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportBuilder.Core;
using ReportBuilder.Core.Data;
using ReportBuilder.Core.Pipeline;
using ReportBuilder.Core.Security;

namespace ReportEngine.Web.Services;

public sealed record GroupPackOutput(byte[] PdfBytes, IReadOnlyList<GroupPackSection> Sections);

internal sealed record GroupReportSpec(
    string Category,
    string Title,
    string Prompt,
    string ViewName,
    string[] SelectedColumns,
    AggregationSpec[] Aggregations,
    FilterSpec[] Filters,
    string? RelativePeriod,
    SortSpec[] OrderBy,
    int Limit = 30);

/// <summary>
/// Compiles and executes curated cross-company and single-company executive report suites
/// through DynamicReportCompiler + CompanyDataSourceFactory and renders multi-page QuestPDF packs.
/// </summary>
public sealed class GroupReportPackService
{
    private readonly CompanyDataSourceFactory _dsf;
    private readonly PolicyOptions _policy;

    public GroupReportPackService(CompanyDataSourceFactory dsf, IOptions<PolicyOptions> policy)
    {
        _dsf = dsf;
        _policy = policy.Value;
    }

    private static readonly GroupReportSpec[] DefaultSpecs =
    [
        // Sales & Revenue
        new("Sales & Revenue",
            "Group Top Customers by Net Sales (Last 3 Quarters)",
            "Show total net and gross sales by customer for last 3 quarters ordered by net sales descending",
            "sales_invoice_item_v",
            ["customer_name"],
            [
                new() { Column = "net_amount", Function = "SUM", Alias = "total_net_sales" },
                new() { Column = "gross_amount", Function = "SUM", Alias = "total_gross_sales" }
            ],
            [], "last_3_quarters",
            [new() { Column = "total_net_sales", Direction = "DESC" }], 30),

        new("Sales & Revenue",
            "Group Credit Sales by Customer (Last 3 Quarters)",
            "Show total credit sales by customer for last 3 quarters ordered by credit net sales descending",
            "sales_invoice_item_v",
            ["customer_name"],
            [
                new() { Column = "net_amount", Function = "SUM", Alias = "credit_net_sales" },
                new() { Column = "invoice_code", Function = "COUNT", Alias = "invoice_lines" }
            ],
            [new() { Column = "payment_mode", Op = "ILIKE", Value = "%Credit%" }],
            "last_3_quarters",
            [new() { Column = "credit_net_sales", Direction = "DESC" }], 30),

        new("Sales & Revenue",
            "Group Top Selling Items by Quantity (Last 3 Quarters)",
            "Pull the list of top inventory items sold maximum by quantity in last 3 quarters",
            "sales_invoice_item_v",
            ["item_code", "item_name"],
            [
                new() { Column = "quantity", Function = "SUM", Alias = "total_qty" },
                new() { Column = "net_amount", Function = "SUM", Alias = "total_net_sales" }
            ],
            [], "last_3_quarters",
            [new() { Column = "total_qty", Direction = "DESC" }], 30),

        new("Sales & Revenue",
            "Group Sales Rep Leaderboard (YTD)",
            "Show total gross sales, net sales, and tax amount by sales rep for year to date",
            "sales_invoice_item_v",
            ["sales_rep"],
            [
                new() { Column = "net_amount", Function = "SUM", Alias = "total_net" },
                new() { Column = "tax_amount", Function = "SUM", Alias = "total_tax" },
                new() { Column = "gross_amount", Function = "SUM", Alias = "total_gross" }
            ],
            [], "ytd",
            [new() { Column = "total_gross", Direction = "DESC" }], 30),

        new("Sales & Revenue",
            "Group Sales Orders by Region & Rep (Last 6 Months)",
            "Show total sales orders booked by sales region and sales rep for the last 6 months",
            "sales_order_item_v",
            ["sales_region", "sales_rep"],
            [
                new() { Column = "quantity", Function = "SUM", Alias = "total_qty" },
                new() { Column = "net_amount", Function = "SUM", Alias = "total_order_value" }
            ],
            [], "last_6_months",
            [new() { Column = "total_order_value", Direction = "DESC" }], 30),

        new("Sales & Revenue",
            "Group Sales Returns & Reasons (Last Year)",
            "Show top returned items by return quantity and total return amount with return reason for last year",
            "sales_return_item_v",
            ["item_code", "item_name", "return_reason"],
            [
                new() { Column = "return_qty", Function = "SUM", Alias = "total_return_qty" },
                new() { Column = "total_return_amount", Function = "SUM", Alias = "total_return_value" }
            ],
            [], "last_year",
            [new() { Column = "total_return_value", Direction = "DESC" }], 30),

        // AR & Customer Risk
        new("AR & Customer Risk",
            "Group Customer 6M Sales vs. AR Aging Brackets",
            "Show top customers by last 6 months sales along with their 0-30, 31-60, 61-90, and >90 days AR aging balances",
            "customer_sales_ar_aging_v",
            ["customer_code", "customer_name", "sales_last_6m", "ar_0_30_days", "ar_31_60_days", "ar_61_90_days", "ar_over_90_days", "total_ar_balance"],
            [], [], null,
            [new() { Column = "sales_last_6m", Direction = "DESC" }], 30),

        new("AR & Customer Risk",
            "Group >90 Days Overdue AR Watchlist (> 10,000)",
            "Show customers with over 90 days AR balance greater than 10,000 ordered by ar_over_90_days descending",
            "customer_sales_ar_aging_v",
            ["customer_code", "customer_name", "sales_last_6m", "ar_0_30_days", "ar_31_60_days", "ar_61_90_days", "ar_over_90_days", "total_ar_balance"],
            [], [new() { Column = "ar_over_90_days", Op = ">", Value = "10000" }], null,
            [new() { Column = "ar_over_90_days", Direction = "DESC" }], 30),

        new("AR & Customer Risk",
            "Group AR Aging Bucket Distribution",
            "Show total accounts receivable balance grouped by aging bucket",
            "ar_aging_v",
            ["aging_bucket"],
            [
                new() { Column = "document_number", Function = "COUNT", Alias = "doc_count" },
                new() { Column = "balance_amount", Function = "SUM", Alias = "total_ar_balance" }
            ],
            [], null,
            [new() { Column = "total_ar_balance", Direction = "DESC" }], 30),

        // AP & Cash
        new("AP & Cash",
            "Group Top Vendor Payables Exposure",
            "Show top vendors by total accounts payable balance",
            "ap_aging_v",
            ["vendor_code", "vendor_name"],
            [new() { Column = "balance_amount", Function = "SUM", Alias = "total_ap_balance" }],
            [], null,
            [new() { Column = "total_ap_balance", Direction = "DESC" }], 30),

        new("AP & Cash",
            "Group Unreconciled Bank Statements & Uncleared Cash",
            "Show unreconciled bank statements with closing balance bank, closing balance books, and uncleared amounts",
            "bank_reconciliation_v",
            ["bank_name", "bank_account_number", "statement_date", "closing_balance_bank", "closing_balance_books", "total_uncleared_amount", "is_reconciled"],
            [], [new() { Column = "is_reconciled", Op = "=", Value = "false" }], null,
            [new() { Column = "total_uncleared_amount", Direction = "DESC" }], 30),

        // Purchasing
        new("Purchasing",
            "Group Consolidated Spend by Vendor (Last 6 Months)",
            "Show total purchase spend by vendor for the last 6 months ordered by total amount descending",
            "purchase_invoice_item_v",
            ["vendor_code", "vendor_name"],
            [new() { Column = "total_amount", Function = "SUM", Alias = "total_spend" }],
            [], "last_6_months",
            [new() { Column = "total_spend", Direction = "DESC" }], 30),

        new("Purchasing",
            "Group Top Purchased Items (YTD)",
            "Show top purchased items by quantity and total amount for year to date",
            "purchase_invoice_item_v",
            ["item_code", "item_description"],
            [
                new() { Column = "quantity", Function = "SUM", Alias = "total_qty" },
                new() { Column = "total_amount", Function = "SUM", Alias = "total_spend" }
            ],
            [], "ytd",
            [new() { Column = "total_spend", Direction = "DESC" }], 30),

        // Inventory
        new("Inventory",
            "Group Warehouse Stock On Hand",
            "Show total on-hand stock quantity grouped by warehouse",
            "stock_on_hand_v",
            ["warehouse_code", "warehouse_name"],
            [new() { Column = "on_hand_qty", Function = "SUM", Alias = "total_on_hand_qty" }],
            [], null,
            [new() { Column = "total_on_hand_qty", Direction = "DESC" }], 30),

        // Finance & Controls
        new("Finance & Controls",
            "Group GL vs. Subledger Control Variances",
            "Show control totals with gl balance, subledger balance, and difference by area",
            "control_totals_v",
            ["area", "gl_balance", "subledger_balance", "difference"],
            [], [], null,
            [new() { Column = "difference", Direction = "DESC" }], 30),

        new("Finance & Controls",
            "Group Manual & Unposted Control Journals",
            "List all manual journals and unposted GL batches affecting control accounts",
            "manual_postings_v",
            ["journal_no", "posting_date", "account_code", "net_amount", "description", "source_type"],
            [], [], null,
            [new() { Column = "posting_date", Direction = "DESC" }], 30),

        new("Finance & Controls",
            "Group Trial Balance Closing Net Movers",
            "Show trial balance accounts ordered by closing net balance descending",
            "trial_balance_v",
            ["account_code", "account_name", "opening_net", "period_debit", "period_credit", "closing_net"],
            [], [], null,
            [new() { Column = "closing_net", Direction = "DESC" }], 30),

        // Organization & Workforce
        new("Organization & Workforce",
            "Group Headcount by Company & Department",
            "Show employee headcount by company and department",
            "employee_v",
            ["company_code", "department_name"],
            [new() { Column = "employee_count", Function = "SUM", Alias = "headcount" }],
            [], null,
            [new() { Column = "headcount", Direction = "DESC" }], 40),

        new("Organization & Workforce",
            "Group Active Business Partners by Country & Type",
            "How many active business partners are there by country and partner type?",
            "business_partner_v",
            ["country", "partner_type"],
            [new() { Column = "partner_count", Function = "SUM", Alias = "total_partners" }],
            [new() { Column = "is_active", Op = "=", Value = "true" }], null,
            [new() { Column = "total_partners", Direction = "DESC" }], 40),

        new("Organization & Workforce",
            "Group Entities & Base Currencies",
            "List active companies in the group with currency and country",
            "company_v",
            ["company_code", "company_name", "currency_code", "country", "is_active"],
            [], [], null,
            [new() { Column = "company_code", Direction = "ASC" }], 30)
    ];

    private static readonly GroupReportSpec[] SingleCompanyPreFinanceSpecs =
    [
        // Sales & Revenue (1-8)
        new("Sales & Revenue",
            "Top 25 Customers by Net & Gross Sales",
            "Show top 25 customers by net and gross sales for last 3 quarters",
            "sales_invoice_item_v",
            ["customer_name"],
            [
                new() { Column = "net_amount", Function = "SUM", Alias = "total_net_sales" },
                new() { Column = "gross_amount", Function = "SUM", Alias = "total_gross_sales" }
            ],
            [], "last_3_quarters",
            [new() { Column = "total_net_sales", Direction = "DESC" }], 25),

        new("Sales & Revenue",
            "Credit Sales by Customer",
            "Show total credit sales by customer for last 3 quarters",
            "sales_invoice_item_v",
            ["customer_name"],
            [
                new() { Column = "net_amount", Function = "SUM", Alias = "credit_net_sales" },
                new() { Column = "invoice_code", Function = "COUNT", Alias = "invoice_lines" }
            ],
            [new() { Column = "payment_mode", Op = "ILIKE", Value = "%Credit%" }],
            "last_3_quarters",
            [new() { Column = "credit_net_sales", Direction = "DESC" }], 25),

        new("Sales & Revenue",
            "Top 25 Selling Items by Quantity & Revenue",
            "Pull top 25 inventory items sold maximum by quantity and revenue in last 3 quarters",
            "sales_invoice_item_v",
            ["item_code", "item_name"],
            [
                new() { Column = "quantity", Function = "SUM", Alias = "total_qty" },
                new() { Column = "net_amount", Function = "SUM", Alias = "total_net_sales" }
            ],
            [], "last_3_quarters",
            [new() { Column = "total_qty", Direction = "DESC" }], 25),

        new("Sales & Revenue",
            "Sales Rep Performance Leaderboard (YTD)",
            "Show total net sales, tax, and gross sales by sales rep for year to date",
            "sales_invoice_item_v",
            ["sales_rep"],
            [
                new() { Column = "net_amount", Function = "SUM", Alias = "total_net" },
                new() { Column = "tax_amount", Function = "SUM", Alias = "total_tax" },
                new() { Column = "gross_amount", Function = "SUM", Alias = "total_gross" }
            ],
            [], "ytd",
            [new() { Column = "total_gross", Direction = "DESC" }], 25),

        new("Sales & Revenue",
            "Sales Breakdown by Category & Payment Mode",
            "Show total sales broken down by sales category and payment mode",
            "sales_invoice_item_v",
            ["sales_category", "payment_mode"],
            [
                new() { Column = "invoice_code", Function = "COUNT", Alias = "line_count" },
                new() { Column = "net_amount", Function = "SUM", Alias = "total_net" },
                new() { Column = "gross_amount", Function = "SUM", Alias = "total_gross" }
            ],
            [], null,
            [new() { Column = "total_gross", Direction = "DESC" }], 25),

        new("Sales & Revenue",
            "Latest 30 Sales Invoice Lines (Detail)",
            "Show latest 30 sales invoice lines with customer, item, quantity, and gross amount",
            "sales_invoice_item_v",
            ["invoice_code", "invoice_date", "customer_name", "payment_mode", "item_name", "quantity", "unit_rate", "net_amount", "gross_amount"],
            [], [], null,
            [new() { Column = "invoice_date", Direction = "DESC" }], 30),

        new("Sales & Revenue",
            "Sales Orders by Region & Sales Rep",
            "Show total sales orders by sales region and sales rep for the last 6 months",
            "sales_order_item_v",
            ["sales_region", "sales_rep"],
            [
                new() { Column = "quantity", Function = "SUM", Alias = "total_qty" },
                new() { Column = "net_amount", Function = "SUM", Alias = "total_order_value" }
            ],
            [], "last_6_months",
            [new() { Column = "total_order_value", Direction = "DESC" }], 25),

        new("Sales & Revenue",
            "Sales Returns by Item & Reason",
            "Show returned items with return quantity, total return amount, and return reason for last year",
            "sales_return_item_v",
            ["item_code", "item_name", "return_reason"],
            [
                new() { Column = "return_qty", Function = "SUM", Alias = "total_return_qty" },
                new() { Column = "total_return_amount", Function = "SUM", Alias = "total_return_value" }
            ],
            [], "last_year",
            [new() { Column = "total_return_value", Direction = "DESC" }], 25),

        // AR & Customer Risk (9-12)
        new("AR & Customer Risk",
            "Top Customers: 6M Sales vs. AR Aging Brackets",
            "Show top customers by last 6 months sales with 0-30, 31-60, 61-90, and >90 days AR brackets",
            "customer_sales_ar_aging_v",
            ["customer_code", "customer_name", "sales_last_6m", "ar_0_30_days", "ar_31_60_days", "ar_61_90_days", "ar_over_90_days", "total_ar_balance"],
            [], [], null,
            [new() { Column = "sales_last_6m", Direction = "DESC" }], 25),

        new("AR & Customer Risk",
            "High-Risk >90 Days Overdue AR Watchlist (> 10,000)",
            "Show customers with over 90 days AR balance greater than 10,000",
            "customer_sales_ar_aging_v",
            ["customer_code", "customer_name", "sales_last_6m", "ar_0_30_days", "ar_31_60_days", "ar_61_90_days", "ar_over_90_days", "total_ar_balance"],
            [], [new() { Column = "ar_over_90_days", Op = ">", Value = "10000" }], null,
            [new() { Column = "ar_over_90_days", Direction = "DESC" }], 25),

        new("AR & Customer Risk",
            "AR Aging Summary by Bucket",
            "Show total accounts receivable balance grouped by aging bucket",
            "ar_aging_v",
            ["aging_bucket"],
            [
                new() { Column = "document_number", Function = "COUNT", Alias = "doc_count" },
                new() { Column = "debit_amount", Function = "SUM", Alias = "total_debit" },
                new() { Column = "balance_amount", Function = "SUM", Alias = "total_balance" }
            ],
            [], null,
            [new() { Column = "total_balance", Direction = "DESC" }], 25),

        new("AR & Customer Risk",
            "Top 25 Customers by Open AR Balance",
            "List top 25 customers with highest open accounts receivable balance as of today",
            "ar_aging_v",
            ["customer_code", "customer_name"],
            [
                new() { Column = "document_number", Function = "COUNT", Alias = "open_docs" },
                new() { Column = "balance_amount", Function = "SUM", Alias = "total_ar_balance" }
            ],
            [], null,
            [new() { Column = "total_ar_balance", Direction = "DESC" }], 25),

        // AP & Cash (13-15)
        new("AP & Cash",
            "Top 25 Vendors by Accounts Payable Balance",
            "Show top 25 vendors by total accounts payable balance",
            "ap_aging_v",
            ["vendor_code", "vendor_name"],
            [
                new() { Column = "document_number", Function = "COUNT", Alias = "doc_count" },
                new() { Column = "balance_amount", Function = "SUM", Alias = "total_ap_balance" }
            ],
            [], null,
            [new() { Column = "total_ap_balance", Direction = "DESC" }], 25),

        new("AP & Cash",
            "Bank Reconciliation Summary & Uncleared Cash",
            "Show bank statement closing balances vs books and uncleared amounts ordered by statement date",
            "bank_reconciliation_v",
            ["bank_name", "bank_account_number", "statement_date", "closing_balance_bank", "closing_balance_books", "total_uncleared_amount", "is_reconciled"],
            [], [], null,
            [new() { Column = "statement_date", Direction = "DESC" }], 25),

        new("AP & Cash",
            "Unreconciled Bank Statements Watchlist",
            "Show unreconciled bank statements with uncleared amounts",
            "bank_reconciliation_v",
            ["bank_name", "bank_account_number", "statement_date", "closing_balance_bank", "closing_balance_books", "total_uncleared_amount", "is_reconciled"],
            [], [new() { Column = "is_reconciled", Op = "=", Value = "false" }], null,
            [new() { Column = "total_uncleared_amount", Direction = "DESC" }], 25),

        // Purchasing & Inventory (16-19)
        new("Purchasing & Inventory",
            "Top 25 Vendors by Purchase Spend (Last 6 Months)",
            "Show top 25 vendors by purchase spend for the last 6 months",
            "purchase_invoice_item_v",
            ["vendor_code", "vendor_name"],
            [new() { Column = "total_amount", Function = "SUM", Alias = "total_spend" }],
            [], "last_6_months",
            [new() { Column = "total_spend", Direction = "DESC" }], 25),

        new("Purchasing & Inventory",
            "Top 25 Purchased Items by Quantity & Spend (YTD)",
            "Show top 25 purchased items by quantity and total amount for year to date",
            "purchase_invoice_item_v",
            ["item_code", "item_description"],
            [
                new() { Column = "quantity", Function = "SUM", Alias = "total_qty" },
                new() { Column = "total_amount", Function = "SUM", Alias = "total_spend" }
            ],
            [], "ytd",
            [new() { Column = "total_spend", Direction = "DESC" }], 25),

        new("Purchasing & Inventory",
            "Latest 30 Purchase Invoice Lines (with PO & GRN)",
            "Show latest 30 purchase invoice lines including PO number and GRN number",
            "purchase_invoice_item_v",
            ["invoice_no", "invoice_date", "vendor_name", "po_number", "grn_number", "item_code", "item_description", "quantity", "total_amount"],
            [], [], null,
            [new() { Column = "invoice_date", Direction = "DESC" }], 30),

        new("Purchasing & Inventory",
            "Warehouse Stock On Hand Summary",
            "Show total on-hand stock quantity grouped by warehouse",
            "stock_on_hand_v",
            ["warehouse_code", "warehouse_name"],
            [new() { Column = "on_hand_qty", Function = "SUM", Alias = "total_on_hand_qty" }],
            [], null,
            [new() { Column = "total_on_hand_qty", Direction = "DESC" }], 25)
    ];

    private static readonly GroupReportSpec[] SingleCompanyFinanceSpecs =
    [
        new("Finance & Controls",
            "Trial Balance — Top 30 Accounts by Closing Net Balance",
            "Show trial balance accounts with opening, debit, credit, and closing net balance",
            "trial_balance_v",
            ["account_code", "account_name", "opening_net", "period_debit", "period_credit", "closing_net"],
            [], [], null,
            [new() { Column = "closing_net", Direction = "DESC" }], 30),

        new("Finance & Controls",
            "General Ledger — Top 25 Accounts by Movement (Last 30 Days)",
            "Show total debit, credit, and net postings by ledger account for the last 30 days",
            "general_ledger_v",
            ["ledger_account_code", "ledger_account_name"],
            [
                new() { Column = "debit_amount", Function = "SUM", Alias = "total_debit" },
                new() { Column = "credit_amount", Function = "SUM", Alias = "total_credit" },
                new() { Column = "net_amount", Function = "SUM", Alias = "total_net" }
            ],
            [], "last_30_days",
            [new() { Column = "total_debit", Direction = "DESC" }], 25),

        new("Finance & Controls",
            "Manual & Unposted Control Journals (Latest 30)",
            "List latest 30 manual journals and unposted GL batches affecting control accounts",
            "manual_postings_v",
            ["journal_no", "posting_date", "account_code", "account_name", "net_amount", "description", "source_type"],
            [], [], null,
            [new() { Column = "posting_date", Direction = "DESC" }], 30)
    ];

    public async Task<GroupPackOutput> BuildPdfAsync(ErpUserSession session, CancellationToken ct = default)
    {
        var allViews = await _dsf.GetPermittedViewsAcrossCompaniesAsync(session, ct);
        var viewMap = allViews.ToDictionary(v => v.ViewName, StringComparer.OrdinalIgnoreCase);
        var sections = await ExecuteSpecsAsync(session, DefaultSpecs, viewMap, "ALL", sortMerged: true, ct);

        var companies = session.AllowedCompanies.Select(c => c.Code).ToArray();
        var userLabel = string.IsNullOrWhiteSpace(session.DisplayName) ? session.UserName : $"{session.DisplayName} ({session.UserName})";
        var pdfBytes = ReportExporters.ToGroupPackPdf(sections, userLabel, companies);
        return new GroupPackOutput(pdfBytes, sections);
    }

    public async Task<GroupPackOutput> BuildSingleCompanyPdfAsync(
        ErpUserSession session,
        CompanyAccessInfo company,
        CancellationToken ct = default)
    {
        var views = await _dsf.GetPermittedViewsAsync(session, company, ct);
        var viewMap = views.ToDictionary(v => v.ViewName, StringComparer.OrdinalIgnoreCase);

        var preFinance = await ExecuteSpecsAsync(
            session, SingleCompanyPreFinanceSpecs, viewMap, company.Code, sortMerged: false, ct);
        var reconSection = await BuildReconciliationSectionAsync(session, company, ct);
        var financeTail = await ExecuteSpecsAsync(
            session, SingleCompanyFinanceSpecs, viewMap, company.Code, sortMerged: false, ct);

        var sections = new List<GroupPackSection>(preFinance.Count + 1 + financeTail.Count);
        sections.AddRange(preFinance);
        sections.Add(reconSection);
        sections.AddRange(financeTail);

        var userLabel = string.IsNullOrWhiteSpace(session.DisplayName) ? session.UserName : $"{session.DisplayName} ({session.UserName})";
        var pdfBytes = ReportExporters.ToGroupPackPdf(
            sections,
            userLabel,
            [company.Code],
            packTitle: $"TERO ERP — Single-Company Report Pack ({company.Code} — {company.Name})",
            scopeHeader: $"Company: {company.Code} — {company.Name}");

        return new GroupPackOutput(pdfBytes, sections);
    }

    public static (byte[] PdfBytes, IReadOnlyList<QueryCatalogEntry> Entries) BuildQueryCatalogPdf(bool singleCompany)
    {
        var entries = new List<QueryCatalogEntry>();

        if (!singleCompany)
        {
            foreach (var spec in DefaultSpecs)
            {
                var scope = spec.ViewName is "employee_v" or "business_partner_v" or "company_v"
                    ? "Console DB (TGC) — All Entities"
                    : "ALL Permitted Companies";
                entries.Add(new QueryCatalogEntry(
                    spec.Category,
                    spec.Title,
                    spec.Prompt,
                    spec.ViewName,
                    scope,
                    RecommendVisual(spec),
                    BuildSqlTemplate(spec, singleCompany: false)));
            }

            var pdf = ReportExporters.ToQueryCatalogPdf(
                entries,
                "TERO ERP — Cross-Company Group Report Queries",
                "Governed Read-Only Query Catalog Across Group Companies (CAP, TGTM, TF, TFS, TPH & TGC)");
            return (pdf, entries);
        }

        foreach (var spec in SingleCompanyPreFinanceSpecs)
        {
            entries.Add(new QueryCatalogEntry(
                spec.Category,
                spec.Title,
                spec.Prompt,
                spec.ViewName,
                "Single Company (<company_code>)",
                RecommendVisual(spec),
                BuildSqlTemplate(spec, singleCompany: true)));
        }

        entries.Add(new QueryCatalogEntry(
            "Finance & Controls",
            "Deterministic GL vs. Trial Balance Reconciliation",
            "Give me reasons for difference between ledger and trial balance during reconciliation",
            "control_totals_v",
            "Single Company (<company_code>)",
            "Reconciliation Table + Summary",
            "-- 1. Control Totals vs Subledgers\n" +
            "SELECT area, gl_balance, subledger_balance, difference\n" +
            "FROM rpt.control_totals_v\n" +
            "WHERE (company_code = @company OR company_code = 'ALL');\n\n" +
            "-- 2. Trial Balance Net Check\n" +
            "SELECT COALESCE(SUM(closing_net), 0) AS trial_balance_net\n" +
            "FROM rpt.trial_balance_v\n" +
            "WHERE (company_code = @company OR company_code = 'ALL');\n\n" +
            "-- 3. Manual & Unposted Control Journals (Latest 25)\n" +
            "SELECT journal_no, posting_date, account_code, net_amount, description\n" +
            "FROM rpt.manual_postings_v\n" +
            "WHERE (company_code = @company OR company_code = 'ALL')\n" +
            "ORDER BY posting_date DESC\n" +
            "LIMIT 25;"));

        foreach (var spec in SingleCompanyFinanceSpecs)
        {
            entries.Add(new QueryCatalogEntry(
                spec.Category,
                spec.Title,
                spec.Prompt,
                spec.ViewName,
                "Single Company (<company_code>)",
                RecommendVisual(spec),
                BuildSqlTemplate(spec, singleCompany: true)));
        }

        var singlePdf = ReportExporters.ToQueryCatalogPdf(
            entries,
            "TERO ERP — Single-Company Report Queries",
            "Governed Read-Only Query Catalog Per Operational Company (CAP, TGTM, TF, TFS, TPH)");
        return (singlePdf, entries);
    }

    private static string RecommendVisual(GroupReportSpec spec)
    {
        if (spec.ViewName == "customer_sales_ar_aging_v" && spec.Filters.Length == 0)
            return "Bar Chart (Compare All Numeric Columns)";
        if (spec.ViewName == "ar_aging_v" && spec.SelectedColumns.Contains("aging_bucket"))
            return "Pie / Bar Chart + Table";
        if (spec.Aggregations.Length > 0 && spec.Title.Contains("Stock", StringComparison.OrdinalIgnoreCase))
            return "Bar Chart + Table";
        if (spec.Aggregations.Length > 0)
            return "Bar Chart (Log Scale if skewed) + Table";
        return "Interactive Table + Excel/PDF Export";
    }

    private static string BuildSqlTemplate(GroupReportSpec spec, bool singleCompany)
    {
        var aggCols = spec.Aggregations
            .Select(a => a.Column)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nonAggCols = spec.SelectedColumns
            .Where(c => spec.Aggregations.Length == 0 || !aggCols.Contains(c))
            .ToList();

        var selectExprs = new List<string>(nonAggCols);
        foreach (var a in spec.Aggregations)
            selectExprs.Add($"{a.Function}({(string.IsNullOrWhiteSpace(a.Column) ? "*" : a.Column)}) AS {a.Alias}");

        var whereClauses = new List<string>();
        if (singleCompany)
            whereClauses.Add("(company_code = @company OR company_code = 'ALL')");

        foreach (var f in spec.Filters)
            whereClauses.Add($"{f.Column} {f.Op} '{f.Value}'");

        if (!string.IsNullOrWhiteSpace(spec.RelativePeriod))
        {
            var dateCol = DateColumnFor(spec.ViewName);
            whereClauses.Add($"{dateCol} >= @period_start  /* {spec.RelativePeriod} */");
        }

        var lines = new List<string>
        {
            $"SELECT {string.Join(", ", selectExprs)}",
            $"FROM rpt.{spec.ViewName}"
        };
        if (whereClauses.Count > 0)
            lines.Add($"WHERE {string.Join("\n  AND ", whereClauses)}");
        if (spec.Aggregations.Length > 0 && nonAggCols.Count > 0)
            lines.Add($"GROUP BY {string.Join(", ", nonAggCols)}");
        if (spec.OrderBy.Length > 0)
            lines.Add($"ORDER BY {string.Join(", ", spec.OrderBy.Select(o => $"{o.Column} {o.Direction}"))}");
        lines.Add($"LIMIT {spec.Limit};");

        return string.Join("\n", lines);
    }

    private static string DateColumnFor(string viewName) => viewName.ToLowerInvariant() switch
    {
        "sales_invoice_item_v" or "purchase_invoice_item_v" => "invoice_date",
        "sales_order_item_v" => "order_date",
        "sales_return_item_v" => "return_date",
        "general_ledger_v" => "transaction_date",
        "bank_reconciliation_v" => "statement_date",
        "manual_postings_v" => "posting_date",
        _ => "transaction_date"
    };

    private async Task<GroupPackSection> BuildReconciliationSectionAsync(
        ErpUserSession session,
        CompanyAccessInfo company,
        CancellationToken ct)
    {
        const string Title = "Deterministic GL vs. Trial Balance Reconciliation";
        const string Prompt = "Give me reasons for difference between ledger and trial balance during reconciliation";
        if (!session.AllowsView("control_totals_v") || !session.AllowsView("manual_postings_v") || !session.AllowsView("trial_balance_v"))
        {
            return new GroupPackSection(
                "Finance & Controls", Title, Prompt, "control_totals_v", company.Code, new QueryResult(),
                "RBAC permission required for control_totals_v, manual_postings_v, and trial_balance_v.");
        }

        try
        {
            var cs = await _dsf.ResolveConnectionStringAsync(company, ct);
            if (string.IsNullOrWhiteSpace(cs))
            {
                return new GroupPackSection(
                    "Finance & Controls", Title, Prompt, "control_totals_v", company.Code, new QueryResult(),
                    $"No database connection configured for {company.Code}.");
            }

            var ds = _dsf.GetOrCreateDataSource(cs);
            var reconciler = new Reconciler(new CompanyReconciliationSource(ds));
            var (tbNet, areas) = await reconciler.ExplainAsync(company.Code, [], ct);
            var result = new QueryResult
            {
                Columns = ["area", "gl_balance", "subledger_balance", "difference", "explanation"],
                Rows = areas.Select(a => new object?[] { a.Area, a.GlBalance, a.SubledgerBalance, a.Difference, a.Explanation }).ToList()
            };
            return new GroupPackSection(
                "Finance & Controls", Title, Prompt, "control_totals_v", company.Code, result,
                $"Trial Balance Net: {tbNet.ToString("N2", CultureInfo.InvariantCulture)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GroupPackSection(
                "Finance & Controls", Title, Prompt, "control_totals_v", company.Code, new QueryResult(),
                $"Reconciliation notice: {ex.Message}");
        }
    }

    private async Task<List<GroupPackSection>> ExecuteSpecsAsync(
        ErpUserSession session,
        IReadOnlyList<GroupReportSpec> specs,
        IReadOnlyDictionary<string, ErpAreaViewMeta> viewMap,
        string targetCompany,
        bool sortMerged,
        CancellationToken ct)
    {
        var sections = new List<GroupPackSection>(specs.Count);
        var defaultScope = targetCompany.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? "ALL Permitted"
            : targetCompany;

        foreach (var spec in specs)
        {
            if (!viewMap.TryGetValue(spec.ViewName, out var viewMeta))
            {
                sections.Add(new GroupPackSection(
                    spec.Category,
                    spec.Title,
                    spec.Prompt,
                    spec.ViewName,
                    defaultScope,
                    new QueryResult(),
                    "View not available or not permitted by current RBAC rules."));
                continue;
            }

            try
            {
                var req = BuildRequest(spec, viewMeta, targetCompany);
                var maxRows = _policy.DefaultMaxRows > 0 ? _policy.DefaultMaxRows : 5000;
                var compiled = DynamicReportCompiler.Compile(req, session, viewMeta, DateTime.UtcNow, maxRows);
                if (!compiled.Ok || compiled.Query is null)
                {
                    sections.Add(new GroupPackSection(
                        spec.Category,
                        spec.Title,
                        spec.Prompt,
                        spec.ViewName,
                        defaultScope,
                        new QueryResult(),
                        "Compile rejected: " + string.Join("; ", compiled.Errors)));
                    continue;
                }

                var result = await _dsf.ExecuteCompiledAsync(session, targetCompany, compiled.Query, viewMeta.Columns, ct);
                var scopeBase = targetCompany.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                    ? "ALL Permitted Companies"
                    : targetCompany;
                var scopeLabel = string.IsNullOrWhiteSpace(spec.RelativePeriod)
                    ? scopeBase
                    : $"{scopeBase} — {spec.RelativePeriod}";
                string? note = null;

                // Transparent UAT date fallback if recent window returned zero rows
                if (result.Rows.Count == 0 && !string.IsNullOrWhiteSpace(spec.RelativePeriod))
                {
                    var allTimeReq = BuildRequest(spec with { RelativePeriod = "all" }, viewMeta, targetCompany);
                    var allTimeCompiled = DynamicReportCompiler.Compile(allTimeReq, session, viewMeta, DateTime.UtcNow, maxRows);
                    if (allTimeCompiled.Ok && allTimeCompiled.Query is not null)
                    {
                        var allTimeResult = await _dsf.ExecuteCompiledAsync(session, targetCompany, allTimeCompiled.Query, viewMeta.Columns, ct);
                        if (allTimeResult.Rows.Count > 0)
                        {
                            result = allTimeResult;
                            scopeLabel = $"{scopeBase} — All Time (0 rows in {spec.RelativePeriod})";
                            note = $"0 rows fell within '{spec.RelativePeriod}'; showing all-time UAT records instead.";
                        }
                    }
                }

                if (sortMerged)
                    result = SortMergedRowsIfNeeded(result, spec);
                sections.Add(new GroupPackSection(spec.Category, spec.Title, spec.Prompt, spec.ViewName, scopeLabel, result, note));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sections.Add(new GroupPackSection(
                    spec.Category,
                    spec.Title,
                    spec.Prompt,
                    spec.ViewName,
                    defaultScope,
                    new QueryResult(),
                    $"Execution notice: {ex.Message}"));
            }
        }

        return sections;
    }

    private static DynamicReportRequest BuildRequest(GroupReportSpec spec, ErpAreaViewMeta viewMeta, string companyCode)
    {
        var available = viewMeta.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new DynamicReportRequest
        {
            ViewName = spec.ViewName,
            CompanyCode = companyCode,
            SelectedColumns = spec.SelectedColumns.Where(available.Contains).ToList(),
            Aggregations = spec.Aggregations
                .Where(a => string.IsNullOrWhiteSpace(a.Column) || available.Contains(a.Column))
                .Select(a => new AggregationSpec { Column = a.Column, Function = a.Function, Alias = a.Alias })
                .ToList(),
            Filters = spec.Filters
                .Where(f => available.Contains(f.Column))
                .Select(f => new FilterSpec { Column = f.Column, Op = f.Op, Value = f.Value })
                .ToList(),
            RelativePeriod = spec.RelativePeriod,
            OrderBy = spec.OrderBy
                .Select(o => new SortSpec { Column = o.Column, Direction = o.Direction })
                .ToList(),
            Limit = spec.Limit
        };
    }

    private static QueryResult SortMergedRowsIfNeeded(QueryResult r, GroupReportSpec spec)
    {
        if (r.Rows.Count <= 1 || spec.OrderBy.Length == 0) return r;
        var primary = spec.OrderBy[0];
        var colIdx = Array.FindIndex(r.Columns, c => c.Equals(primary.Column, StringComparison.OrdinalIgnoreCase));
        if (colIdx < 0) return r;

        var desc = primary.Direction.Equals("DESC", StringComparison.OrdinalIgnoreCase);
        var sorted = desc
            ? r.Rows.OrderByDescending(row => ToSortableDecimal(row, colIdx)).ThenBy(row => row[0]?.ToString()).ToList()
            : r.Rows.OrderBy(row => ToSortableDecimal(row, colIdx)).ThenBy(row => row[0]?.ToString()).ToList();

        return new QueryResult
        {
            Columns = r.Columns,
            Rows = sorted,
            Truncated = r.Truncated
        };
    }

    private static decimal ToSortableDecimal(object?[] row, int idx)
    {
        if (idx >= row.Length || row[idx] is null) return decimal.MinValue;
        try { return Convert.ToDecimal(row[idx], CultureInfo.InvariantCulture); }
        catch { return 0m; }
    }
}

internal sealed class CompanyReconciliationSource(NpgsqlDataSource ds) : IReconciliationSource
{
    public async Task<IReadOnlyList<ControlTotal>> GetControlTotalsAsync(string company, CancellationToken ct)
    {
        var list = new List<ControlTotal>();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT area, gl_balance, subledger_balance, difference FROM rpt.control_totals_v WHERE company_code = @c OR company_code = 'ALL'", conn);
        cmd.Parameters.AddWithValue("c", company);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ControlTotal(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3)));
        return list;
    }

    public async Task<decimal> GetTrialBalanceNetAsync(string company, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT COALESCE(SUM(closing_net), 0) FROM rpt.trial_balance_v WHERE company_code = @c OR company_code = 'ALL'", conn);
        cmd.Parameters.AddWithValue("c", company);
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<ManualPosting>> GetManualPostingsAsync(string company, IReadOnlyCollection<string> accounts, CancellationToken ct)
    {
        var list = new List<ManualPosting>();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT journal_no, posting_date, account_code, net_amount, description FROM rpt.manual_postings_v WHERE (company_code = @c OR company_code = 'ALL') ORDER BY posting_date DESC LIMIT 25", conn);
        cmd.Parameters.AddWithValue("c", company);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ManualPosting(r.GetString(0), r.GetDateTime(1), r.GetString(2), r.GetDecimal(3), r.GetString(4)));
        return list;
    }
}
