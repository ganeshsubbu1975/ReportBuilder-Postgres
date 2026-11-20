-- Extended Company ERP Reporting Views (Inventory Stock, Purchasing, Sales Orders, Sales Returns, Bank Reconciliation).
-- Apply to each company operational database (e.g., ChefDB-QA, ThomsunGeneralTradingMain_UAT, ThomsunFoodLLCUAT, ThomsunFoodServiceLLC_UAT, ThePrintHouse).

CREATE SCHEMA IF NOT EXISTS rpt;

-- 1. Inventory Stock On Hand (only created when trading.inventoryonhandstock exists in the company DB)
DO $$
BEGIN
    IF to_regclass('trading.inventoryonhandstock') IS NOT NULL THEN
        EXECUTE $v$
            CREATE OR REPLACE VIEW rpt.stock_on_hand_v AS
            SELECT
                'ALL'::text AS company_code,
                s.itemcode AS item_code,
                s.itemname AS item_name,
                s.warehousecode AS warehouse_code,
                s.warehousename AS warehouse_name,
                COALESCE(s.racknumber, '') AS rack_number,
                s.quantityuom AS uom,
                COALESCE(s.onhandquantity, 0)::numeric AS on_hand_qty
            FROM trading.inventoryonhandstock s
            WHERE NOT COALESCE(s.isarchived, false);
        $v$;
    END IF;
END $$;

-- 2. Purchase Invoices & Line Items
CREATE OR REPLACE VIEW rpt.purchase_invoice_item_v AS
SELECT
    'ALL'::text AS company_code,
    pi.invoiceno AS invoice_no,
    pi.invoicedate::date AS invoice_date,
    pi.transactiondate::date AS transaction_date,
    pi.businesspartnercode AS vendor_code,
    pi.businesspartnername AS vendor_name,
    pi.ponumber AS po_number,
    pi.grnnumber AS grn_number,
    pi.transactioncurrencycode AS currency_code,
    COALESCE(pi.approvestatusname, '') AS approve_status,
    pli.itemcode AS item_code,
    COALESCE(pli.description, '') AS item_description,
    COALESCE(pli.uomname, '') AS uom,
    COALESCE(pli.qty, 0) AS quantity,
    COALESCE(pli.rate, 0) AS unit_rate,
    COALESCE(pli.netamount, 0) AS net_amount,
    COALESCE(pli.taxamount, 0) AS tax_amount,
    COALESCE(pli.totalamount, 0) AS total_amount
FROM finance.purchaseinvoice pi
JOIN finance.purchaseinvoicelineitem pli ON pli.purchaseinvoiceid = pi.id AND NOT COALESCE(pli.isarchived, false)
WHERE NOT COALESCE(pi.isarchived, false);

-- 3. Sales Orders & Items
CREATE OR REPLACE VIEW rpt.sales_order_item_v AS
SELECT
    COALESCE(NULLIF(so.company, ''), 'ALL') AS company_code,
    so.branch AS branch_code,
    so.salesordercode AS sales_order_code,
    so.quotationcode AS quotation_code,
    so.date::date AS order_date,
    so.customercode AS customer_code,
    so.customername AS customer_name,
    so.salesrepname AS sales_rep,
    so.salesregionname AS sales_region,
    so.warehousecode AS warehouse_code,
    so.warehousename AS warehouse_name,
    so.currency AS currency_code,
    soi.itemcode AS item_code,
    soi.itemname AS item_name,
    soi.uom,
    COALESCE(soi.quantity, 0)::numeric AS quantity,
    COALESCE(soi.rate, 0)::numeric AS unit_rate,
    COALESCE(soi.netamount, 0)::numeric AS net_amount,
    COALESCE(soi.taxamount, 0)::numeric AS tax_amount,
    COALESCE(soi.grossamount, 0)::numeric AS gross_amount
FROM sales.salesorder so
JOIN sales.salesorderitem soi ON soi.salesorderid = so.id AND NOT COALESCE(soi.isarchived, false)
WHERE NOT COALESCE(so.isarchived, false);

-- 4. Sales Returns & Items
CREATE OR REPLACE VIEW rpt.sales_return_item_v AS
SELECT
    COALESCE(NULLIF(sr.company, ''), 'ALL') AS company_code,
    sr.branch AS branch_code,
    sr.salesreturncode AS sales_return_code,
    sr.salesinvoicecode AS sales_invoice_code,
    sr.date::date AS return_date,
    sr.customercode AS customer_code,
    sr.customername AS customer_name,
    COALESCE(sr.reasonremarks, sr.reasoncode, '') AS return_reason,
    sr.currency AS currency_code,
    sri.itemcode AS item_code,
    sri.itemname AS item_name,
    sri.uom,
    COALESCE(sri.returnquantity, 0)::numeric AS return_qty,
    COALESCE(sri.returnamount, 0)::numeric AS return_amount,
    COALESCE(sri.taxamount, 0)::numeric AS tax_amount,
    COALESCE(sri.totalreturnamount, 0)::numeric AS total_return_amount
FROM sales.salesreturn sr
JOIN sales.salesreturnitem sri ON sri.salesreturnid = sr.id AND NOT COALESCE(sri.isarchived, false)
WHERE NOT COALESCE(sr.isarchived, false);

-- 5. Bank Reconciliation Summary
CREATE OR REPLACE VIEW rpt.bank_reconciliation_v AS
SELECT
    COALESCE(NULLIF(br.companycode, ''), 'ALL') AS company_code,
    br.branchcode AS branch_code,
    br.statementnumber AS statement_number,
    br.bankname AS bank_name,
    br.bankaccountname AS bank_account_name,
    br.bankaccountnumber AS bank_account_number,
    br.currencycode AS currency_code,
    br.currentstatementdate::date AS statement_date,
    COALESCE(br.bankaccountbalance, 0) AS bank_account_balance,
    COALESCE(br.currentclosingbalancebank, 0) AS closing_balance_bank,
    COALESCE(br.currentclosingbalancebooks, 0) AS closing_balance_books,
    COALESCE(br.totalunclearedamount, 0) AS total_uncleared_amount,
    COALESCE(br.totalexchangevariation, 0) AS total_exchange_variation,
    COALESCE(br.isreconciled, false) AS is_reconciled
FROM finance.bankreconciliation br
WHERE NOT COALESCE(br.isarchived, false);

-- 6. Customer 6-Month Sales + AR Aging Brackets (0-30, 31-60, 61-90, >90 days)
CREATE OR REPLACE VIEW rpt.customer_sales_ar_aging_v AS
WITH sales_6m AS (
    SELECT
        si.businesspartnercode AS customer_code,
        MAX(si.businesspartnername) AS customer_name,
        SUM(COALESCE(si.totalamountinbasecurrency, si.totalamount, 0)) AS sales_last_6m
    FROM finance.salesinvoice si
    WHERE NOT COALESCE(si.isarchived, false)
      AND COALESCE(si.invoicedate, si.transactiondate)::date >= (CURRENT_DATE - INTERVAL '6 months')::date
    GROUP BY si.businesspartnercode
),
ar_buckets AS (
    SELECT
        ar.businesspartnercode AS customer_code,
        MAX(ar.businesspartnername) AS customer_name,
        SUM(CASE WHEN (CURRENT_DATE - COALESCE(ar.documentdate, ar.transactiondate)::date) BETWEEN 0 AND 30
                 THEN COALESCE(ar.balanceamountinbasecurrency, 0) ELSE 0 END) AS ar_0_30_days,
        SUM(CASE WHEN (CURRENT_DATE - COALESCE(ar.documentdate, ar.transactiondate)::date) BETWEEN 31 AND 60
                 THEN COALESCE(ar.balanceamountinbasecurrency, 0) ELSE 0 END) AS ar_31_60_days,
        SUM(CASE WHEN (CURRENT_DATE - COALESCE(ar.documentdate, ar.transactiondate)::date) BETWEEN 61 AND 90
                 THEN COALESCE(ar.balanceamountinbasecurrency, 0) ELSE 0 END) AS ar_61_90_days,
        SUM(CASE WHEN (CURRENT_DATE - COALESCE(ar.documentdate, ar.transactiondate)::date) > 90
                 THEN COALESCE(ar.balanceamountinbasecurrency, 0) ELSE 0 END) AS ar_over_90_days,
        SUM(COALESCE(ar.balanceamountinbasecurrency, 0)) AS total_ar_balance
    FROM finance.accountsreceivable ar
    WHERE NOT COALESCE(ar.isarchived, false)
    GROUP BY ar.businesspartnercode
)
SELECT
    'ALL'::text AS company_code,
    COALESCE(s.customer_code, a.customer_code) AS customer_code,
    COALESCE(s.customer_name, a.customer_name) AS customer_name,
    COALESCE(s.sales_last_6m, 0) AS sales_last_6m,
    COALESCE(a.ar_0_30_days, 0) AS ar_0_30_days,
    COALESCE(a.ar_31_60_days, 0) AS ar_31_60_days,
    COALESCE(a.ar_61_90_days, 0) AS ar_61_90_days,
    COALESCE(a.ar_over_90_days, 0) AS ar_over_90_days,
    COALESCE(a.total_ar_balance, 0) AS total_ar_balance
FROM sales_6m s
FULL OUTER JOIN ar_buckets a ON a.customer_code = s.customer_code;

GRANT USAGE ON SCHEMA rpt TO rb_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA rpt TO rb_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA rpt GRANT SELECT ON TABLES TO rb_reader;
