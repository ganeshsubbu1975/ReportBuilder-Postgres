-- Report Engine extensions for multi-company database routing, ERP Area metadata, and DB-driven RBAC.
-- Run on the Console database (ThomsunGroupConsoleUAT1) after 10_report_builder_support.pg.sql.

CREATE TABLE IF NOT EXISTS cat.company_database_map (
    company_code varchar(30) PRIMARY KEY,
    database_name varchar(120) NOT NULL,
    host varchar(200),
    port int,
    is_active boolean NOT NULL DEFAULT true
);

CREATE TABLE IF NOT EXISTS cat.erp_area (
    view_name varchar(80) PRIMARY KEY,
    area_name varchar(80) NOT NULL,
    display_name varchar(120) NOT NULL,
    description varchar(400) NOT NULL DEFAULT '',
    date_column varchar(80),
    default_fields text NOT NULL DEFAULT '',
    synonyms_hint text NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS cat.module_view_map (
    id serial PRIMARY KEY,
    role_name varchar(120),
    claim_type varchar(120),
    claim_value varchar(200),
    menuid int,
    area_name varchar(80),
    view_name varchar(80) NOT NULL,
    allow_all_for_view_all_flags boolean NOT NULL DEFAULT true,
    blocked_columns text NOT NULL DEFAULT ''
);

-- Seed default company -> operational database mappings (supplements/overrides Tenants JSON)
INSERT INTO cat.company_database_map (company_code, database_name, is_active) VALUES
    ('TGC',  'ThomsunGroupConsoleUAT1', true),
    ('CAP',  'ChefDB-QA', true),
    ('TPH',  'ThePrintHouse', true),
    ('TF',   'ThomsunFoodLLCUAT', true),
    ('TFS',  'ThomsunFoodServiceLLC_UAT', true),
    ('TGT',  'ThomsunGeneralTradingMain_UAT', true),
    ('TGTM', 'ThomsunGeneralTradingMain_UAT', true),
    ('TIC',  'TICVansales', true)
ON CONFLICT (company_code) DO NOTHING;

-- Seed ERP Area metadata for both Console master-data views and Company transactional views
INSERT INTO cat.erp_area (view_name, area_name, display_name, description, date_column, default_fields, synonyms_hint) VALUES
    ('company_v',             'Organization',      'Companies',                'Company master records, currencies, and locations', NULL, 'company_code,company_name,currency_code,country,is_active', 'company, entity, subsidiary'),
    ('business_partner_v',    'Business Partners', 'Business Partners',        'Customers, vendors, and business partner master data', 'created_date', 'company_code,partner_code,partner_name,partner_type,country,is_active', 'customer, vendor, supplier, bp'),
    ('employee_v',            'HR & Employees',    'Employees',                'Employee directory, department, designation, and joining dates', 'joining_date', 'company_code,employee_code,employee_name,department_name,designation_name,is_active', 'staff, headcount, hr'),
    ('department_v',          'HR & Employees',    'Departments',              'Organization departments', NULL, 'department_code,department_name', 'dept, division'),
    ('designation_v',         'HR & Employees',    'Designations',             'Job titles and designations', NULL, 'designation_code,designation_name', 'role, job title'),
    ('currency_rate_v',       'Finance & Tax',     'Currency Rates',           'Exchange rates by base and transaction currency', 'exchange_date', 'base_currency,transaction_currency,exchange_date,exchange_rate', 'fx, forex, exchange rate'),
    ('fiscal_period_v',       'Finance & Tax',     'Fiscal Periods',           'Financial years, periods, and open/closed status', 'from_date', 'fiscal_year_code,period,from_date,to_date,period_status', 'year, period, accounting period'),
    ('tax_rate_v',            'Finance & Tax',     'Tax Rates',                'Tax codes, types, percentages, and jurisdictions', NULL, 'tax_code,tax_name,tax_type,tax_percent,jurisdiction_code', 'vat, gst, tax'),
    ('bank_v',                'Finance & Tax',     'Banks',                    'Bank master records', NULL, 'bank_code,bank_name', 'bank'),
    ('item_master_v',         'Inventory & Items', 'Item Master',              'Items with commodity, class, family, and segment hierarchy', NULL, 'item_code,item_name,commodity,item_class,item_family,item_segment', 'sku, product, material'),
    ('sales_invoice_item_v',  'Sales & Inventory', 'Sales Invoices & Items',   'Sales invoice lines with customer, item, quantity, net/gross amount, and credit terms', 'invoice_date', 'company_code,invoice_code,invoice_date,customer_name,item_code,item_name,quantity,net_amount,payment_mode', 'credit sales, top selling inventory, revenue, sold items, customer sales'),
    ('general_ledger_v',      'Finance & GL',      'General Ledger',           'Posted GL journal lines with account code, debit/credit base amounts, and partner', 'transaction_date', 'company_code,document_number,transaction_date,ledger_account_code,ledger_account_name,debit_amount,credit_amount', 'gl, ledger, journal postings'),
    ('trial_balance_v',       'Finance & GL',      'Trial Balance',            'Opening and period debit/credit balances by ledger account', NULL, 'company_code,account_code,account_name,opening_net,period_debit,period_credit,closing_net', 'tb, trial balance'),
    ('control_totals_v',      'Finance & GL',      'Control Totals',           'GL control account balances compared against AR and AP subledgers', NULL, 'company_code,area,gl_balance,subledger_balance,difference', 'reconciliation, variance, gl vs subledger'),
    ('manual_postings_v',     'Finance & GL',      'Manual & Unposted Journals','Manual journals posted to control accounts and unposted GL entries', 'posting_date', 'company_code,journal_no,posting_date,account_code,net_amount,description,source_type', 'manual journal, unposted, reconciliation reason'),
    ('ar_aging_v',               'Finance & AR',      'Accounts Receivable',      'Customer receivables, invoice balances, and transaction dates', 'transaction_date', 'company_code,customer_code,customer_name,document_number,transaction_date,debit_amount,credit_amount,balance_amount', 'ar, receivables, customer balance'),
    ('ap_aging_v',               'Finance & AP',      'Accounts Payable',         'Vendor payables, invoice balances, and transaction dates', 'transaction_date', 'company_code,vendor_code,vendor_name,document_number,transaction_date,debit_amount,credit_amount,balance_amount', 'ap, payables, supplier balance'),
    ('stock_on_hand_v',          'Sales & Inventory', 'Stock On Hand',            'Current inventory on-hand quantities by item, warehouse, and rack', NULL, 'item_code,item_name,warehouse_code,warehouse_name,uom,on_hand_qty', 'stock, inventory on hand, warehouse stock, balance qty'),
    ('purchase_invoice_item_v',  'Purchasing',        'Purchase Invoices',        'Vendor purchase invoice lines with PO, GRN, quantity, and total amount', 'invoice_date', 'invoice_no,invoice_date,vendor_name,po_number,grn_number,item_code,item_description,quantity,total_amount', 'purchases, vendor invoices, procurement, grn'),
    ('sales_order_item_v',       'Sales & Inventory', 'Sales Orders',             'Sales order lines by customer, sales rep, item, quantity, and net amount', 'order_date', 'company_code,sales_order_code,order_date,customer_name,sales_rep,item_code,item_name,quantity,net_amount', 'orders, bookings, sales order'),
    ('sales_return_item_v',      'Sales & Inventory', 'Sales Returns',            'Sales return lines with reason, returned quantity, and credit amount', 'return_date', 'company_code,sales_return_code,return_date,customer_name,return_reason,item_code,item_name,return_qty,total_return_amount', 'returns, credit return, damaged goods'),
    ('bank_reconciliation_v',    'Finance & GL',      'Bank Reconciliation',      'Bank statement closing balances vs books, uncleared amounts, and status', 'statement_date', 'company_code,statement_number,bank_name,bank_account_number,statement_date,closing_balance_bank,closing_balance_books,total_uncleared_amount,is_reconciled', 'brs, bank rec, uncleared, bank statement'),
    ('customer_sales_ar_aging_v','Finance & AR',      'Customer Sales & AR Aging','Top customers by last 6 months sales with AR aging brackets (0-30, 31-60, 61-90, >90 days)', NULL, 'customer_code,customer_name,sales_last_6m,ar_0_30_days,ar_31_60_days,ar_61_90_days,ar_over_90_days,total_ar_balance', 'top customers, 30 60 90 days bracket, ar aging with sales, customer receivables aging')
ON CONFLICT (view_name) DO NOTHING;

-- Seed RBAC mappings: menuid 1-4 and role names
INSERT INTO cat.module_view_map (role_name, menuid, area_name, view_name, allow_all_for_view_all_flags, blocked_columns)
SELECT v.role_name, v.menuid, v.area_name, v.view_name, true, v.blocked_columns
FROM (VALUES
    (NULL, 1, 'Organization',      'company_v',            'tax_registration_no'),
    (NULL, 1, 'HR & Employees',    'employee_v',           'date_of_birth'),
    (NULL, 1, 'HR & Employees',    'department_v',         ''),
    (NULL, 1, 'HR & Employees',    'designation_v',        ''),
    (NULL, 2, 'Business Partners', 'business_partner_v',   'tax_registration_no'),
    (NULL, 2, 'Inventory & Items', 'item_master_v',        ''),
    (NULL, 2, 'Sales & Inventory', 'sales_invoice_item_v', ''),
    (NULL, 3, 'Finance & Tax',     'currency_rate_v',      ''),
    (NULL, 3, 'Finance & Tax',     'fiscal_period_v',      ''),
    (NULL, 3, 'Finance & Tax',     'tax_rate_v',           ''),
    (NULL, 3, 'Finance & Tax',     'bank_v',               ''),
    (NULL, 3, 'Finance & GL',      'general_ledger_v',     ''),
    (NULL, 3, 'Finance & GL',      'trial_balance_v',      ''),
    (NULL, 3, 'Finance & GL',      'control_totals_v',     ''),
    (NULL, 3, 'Finance & GL',      'manual_postings_v',    ''),
    (NULL, 3, 'Finance & AR',      'ar_aging_v',           ''),
    (NULL, 3, 'Finance & AP',      'ap_aging_v',           ''),
    (NULL, 4, '*',                 '*',                    ''),
    ('Admin',       NULL, '*', '*', ''),
    ('ReportAdmin', NULL, '*', '*', ''),
    ('Finance',     NULL, 'Finance & GL',      'general_ledger_v',     ''),
    ('Finance',     NULL, 'Finance & GL',      'trial_balance_v',      ''),
    ('Finance',     NULL, 'Finance & GL',      'control_totals_v',     ''),
    ('Finance',     NULL, 'Finance & GL',      'manual_postings_v',    ''),
    ('Finance',     NULL, 'Finance & AR',      'ar_aging_v',           ''),
    ('Finance',     NULL, 'Finance & AP',      'ap_aging_v',           ''),
    ('Sales',       NULL, 'Sales & Inventory', 'sales_invoice_item_v', ''),
    ('Inventory',   NULL, 'Sales & Inventory', 'sales_invoice_item_v', ''),
    ('Inventory',   NULL, 'Inventory & Items', 'item_master_v',        ''),
    ('HR',          NULL, 'HR & Employees',    'employee_v',           ''),
    ('HR',          NULL, 'HR & Employees',    'department_v',         ''),
    ('HR',          NULL, 'HR & Employees',    'designation_v',        '')
) AS v(role_name, menuid, area_name, view_name, blocked_columns)
WHERE NOT EXISTS (SELECT 1 FROM cat.module_view_map);

GRANT SELECT, INSERT, UPDATE, DELETE ON cat.company_database_map, cat.erp_area, cat.module_view_map TO rb_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA cat TO rb_app;
GRANT USAGE ON SCHEMA common TO rb_app;
GRANT SELECT ON common."AspNetUsers", common."AspNetRoles", common."AspNetUserRoles",
                common."AspNetRoleClaims", common."AspNetUserClaims",
                common.consoleusermenuaccess, common.usercompany, common.company TO rb_app;

-- Idempotently ensure extended views are mapped in cat.module_view_map
INSERT INTO cat.module_view_map (role_name, menuid, area_name, view_name, allow_all_for_view_all_flags, blocked_columns)
SELECT v.role_name, v.menuid, v.area_name, v.view_name, true, ''
FROM (VALUES
    (NULL, 2, 'Sales & Inventory', 'stock_on_hand_v'),
    (NULL, 2, 'Sales & Inventory', 'sales_order_item_v'),
    (NULL, 2, 'Sales & Inventory', 'sales_return_item_v'),
    (NULL, 3, 'Purchasing',        'purchase_invoice_item_v'),
    (NULL, 3, 'Finance & GL',      'bank_reconciliation_v'),
    (NULL, 3, 'Finance & AR',      'customer_sales_ar_aging_v'),
    ('Inventory', NULL, 'Sales & Inventory', 'stock_on_hand_v'),
    ('Sales',     NULL, 'Sales & Inventory', 'sales_order_item_v'),
    ('Sales',     NULL, 'Sales & Inventory', 'sales_return_item_v'),
    ('Purchase',  NULL, 'Purchasing',        'purchase_invoice_item_v'),
    ('Finance',   NULL, 'Finance & GL',      'bank_reconciliation_v'),
    ('Finance',   NULL, 'Finance & AR',      'customer_sales_ar_aging_v')
) AS v(role_name, menuid, area_name, view_name)
WHERE NOT EXISTS (
    SELECT 1 FROM cat.module_view_map m
    WHERE m.view_name = v.view_name
      AND m.role_name IS NOT DISTINCT FROM v.role_name
      AND m.menuid IS NOT DISTINCT FROM v.menuid
);
