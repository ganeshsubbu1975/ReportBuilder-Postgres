-- Operational Company ERP Reporting Views (sales, trading, finance).
-- Apply to each company operational database (e.g. ThomsunGeneralTradingMain_UAT, ThomsunFoodLLCUAT, ThePrintHouse, ChefDB-QA).
-- Views expose company_code so the deterministic SQL compiler can enforce company-level scoping.

CREATE SCHEMA IF NOT EXISTS rpt;

-- 1. Sales Invoices & Line Items (reads finance.salesinvoice + finance.salesinvoicelineitem where live ERP invoices are stored)
DROP VIEW IF EXISTS rpt.sales_invoice_item_v;
CREATE OR REPLACE VIEW rpt.sales_invoice_item_v AS
SELECT
    'ALL'::text AS company_code,
    si.documentnumber AS invoice_code,
    COALESCE(si.invoicedate, si.transactiondate)::date AS invoice_date,
    si.businesspartnercode AS customer_code,
    si.businesspartnername AS customer_name,
    COALESCE(si.employeename, '') AS sales_rep,
    COALESCE(si.salescategoryname, '') AS sales_category,
    si.transactioncurrencycode AS currency_code,
    COALESCE(si.transactiontype, '') AS payment_mode,
    COALESCE(sli.ledgeraccountcode, '') AS item_code,
    COALESCE(NULLIF(sli.description, ''), sli.ledgeraccountname, '') AS item_name,
    COALESCE(sli.uomname, '') AS uom,
    COALESCE(sli.qty, 0) AS quantity,
    COALESCE(sli.rate, 0) AS unit_rate,
    COALESCE(sli.amount, 0) AS line_amount,
    COALESCE(sli.netamount, 0) AS net_amount,
    COALESCE(sli.taxamount, 0) AS tax_amount,
    COALESCE(sli.totalamount, 0) AS gross_amount
FROM finance.salesinvoice si
JOIN finance.salesinvoicelineitem sli ON sli.salesinvoiceid = si.id AND NOT COALESCE(sli.isarchived, false)
WHERE NOT COALESCE(si.isarchived, false);

-- 2. General Ledger Postings
CREATE OR REPLACE VIEW rpt.general_ledger_v AS
SELECT
    'ALL'::text AS company_code,
    gl.documentnumber AS document_number,
    gl.documentdate::date AS document_date,
    gl.transactiondate::date AS transaction_date,
    gl.journalbookcode AS journal_book_code,
    gl.ledgeraccountcode AS ledger_account_code,
    COALESCE(NULLIF(gl.ledgeraccountname, ''), coa.description) AS ledger_account_name,
    gl.businesspartnercode AS partner_code,
    gl.transactioncurrencycode AS currency_code,
    COALESCE(gl.debitamountinbasecurrency, 0) AS debit_amount,
    COALESCE(gl.creditamountinbasecurrency, 0) AS credit_amount,
    COALESCE(gl.debitamountinbasecurrency, 0) - COALESCE(gl.creditamountinbasecurrency, 0) AS net_amount,
    COALESCE(gl.iscontrolaccount, false) AS is_control_account,
    gl.controlaccounttype AS control_account_type,
    gl.financialyearid AS financial_year_id,
    gl.periodid AS period_id,
    gl.narration
FROM finance.generalledger gl
LEFT JOIN finance.chartofaccount coa ON coa.code = gl.ledgeraccountcode
WHERE NOT COALESCE(gl.isarchived, false);

-- 3. Trial Balance (Opening + Posted GL movements by account)
CREATE OR REPLACE VIEW rpt.trial_balance_v AS
WITH opening AS (
    SELECT
        COALESCE(NULLIF(ob.companycode, ''), 'ALL') AS company_code,
        ob.chartofaccountcode AS account_code,
        MAX(ob.chartofaccountname) AS account_name,
        SUM(COALESCE(ob.debitamount, 0) - COALESCE(ob.creditamount, 0)) AS opening_net
    FROM finance.glopeningbalance ob
    WHERE NOT COALESCE(ob.isarchived, false)
    GROUP BY COALESCE(NULLIF(ob.companycode, ''), 'ALL'), ob.chartofaccountcode
),
gl AS (
    SELECT
        'ALL'::text AS company_code,
        gl.ledgeraccountcode AS account_code,
        MAX(gl.ledgeraccountname) AS account_name,
        SUM(COALESCE(gl.debitamountinbasecurrency, 0)) AS period_debit,
        SUM(COALESCE(gl.creditamountinbasecurrency, 0)) AS period_credit,
        SUM(COALESCE(gl.debitamountinbasecurrency, 0) - COALESCE(gl.creditamountinbasecurrency, 0)) AS period_net
    FROM finance.generalledger gl
    WHERE NOT COALESCE(gl.isarchived, false)
    GROUP BY gl.ledgeraccountcode
)
SELECT
    COALESCE(o.company_code, g.company_code, 'ALL') AS company_code,
    COALESCE(o.account_code, g.account_code) AS account_code,
    COALESCE(g.account_name, o.account_name, coa.description) AS account_name,
    COALESCE(o.opening_net, 0) AS opening_net,
    COALESCE(g.period_debit, 0) AS period_debit,
    COALESCE(g.period_credit, 0) AS period_credit,
    COALESCE(o.opening_net, 0) + COALESCE(g.period_net, 0) AS closing_net
FROM opening o
FULL OUTER JOIN gl g ON g.account_code = o.account_code
LEFT JOIN finance.chartofaccount coa ON coa.code = COALESCE(o.account_code, g.account_code);

-- 4. Control Totals (GL control accounts vs. AR/AP subledgers for Reconciler)
CREATE OR REPLACE VIEW rpt.control_totals_v AS
WITH gl_ctrl AS (
    SELECT
        CASE controlaccounttype
            WHEN 1 THEN 'AR'
            WHEN 2 THEN 'AP'
            ELSE 'OTHER_' || COALESCE(controlaccounttype::text, '0')
        END AS area,
        SUM(COALESCE(debitamountinbasecurrency, 0) - COALESCE(creditamountinbasecurrency, 0)) AS gl_balance
    FROM finance.generalledger
    WHERE NOT COALESCE(isarchived, false) AND COALESCE(iscontrolaccount, false) = true
    GROUP BY controlaccounttype
),
ar_sub AS (
    SELECT 'AR'::text AS area, SUM(COALESCE(debitvalueinbasecurrency, 0) - COALESCE(creditvalueinbasecurrency, 0)) AS subledger_balance
    FROM finance.accountsreceivable WHERE NOT COALESCE(isarchived, false)
),
ap_sub AS (
    SELECT 'AP'::text AS area, SUM(COALESCE(debitvalueinbasecurrency, 0) - COALESCE(creditvalueinbasecurrency, 0)) AS subledger_balance
    FROM finance.accountspayable WHERE NOT COALESCE(isarchived, false)
),
subs AS (
    SELECT * FROM ar_sub UNION ALL SELECT * FROM ap_sub
)
SELECT
    'ALL'::text AS company_code,
    COALESCE(s.area, g.area) AS area,
    COALESCE(g.gl_balance, 0) AS gl_balance,
    COALESCE(s.subledger_balance, 0) AS subledger_balance,
    COALESCE(g.gl_balance, 0) - COALESCE(s.subledger_balance, 0) AS difference
FROM subs s
FULL OUTER JOIN gl_ctrl g ON g.area = s.area;

-- 5. Manual & Unposted Postings (explains Ledger vs Trial Balance / Subledger variances)
CREATE OR REPLACE VIEW rpt.manual_postings_v AS
SELECT
    'ALL'::text AS company_code,
    mje.documentnumber AS journal_no,
    mje.transactiondate::date AS posting_date,
    mjed.ledgeraccountcode AS account_code,
    mjed.ledgeraccountname AS account_name,
    COALESCE(mjed.debitamountinbasecurrency, 0) - COALESCE(mjed.creditamountinbasecurrency, 0) AS net_amount,
    COALESCE(mjed.narration, mje.narration, '') AS description,
    CASE WHEN COALESCE(mje.isposted, false) THEN 'MANUAL_POSTED' ELSE 'MANUAL_UNPOSTED' END AS source_type
FROM finance.manualjournalentry mje
JOIN finance.manualjournalentrydetail mjed ON mjed.manualjournalentryid = mje.id
WHERE NOT COALESCE(mje.isarchived, false)
  AND NOT COALESCE(mjed.isarchived, false)
  AND COALESCE(mjed.iscontrolaccount, false) = true
UNION ALL
SELECT
    'ALL'::text AS company_code,
    gbp.documentnumber AS journal_no,
    gbp.transactiondate::date AS posting_date,
    gbp.ledgeraccountcode AS account_code,
    gbp.ledgeraccountname AS account_name,
    COALESCE(gbp.debitamountinbasecurrency, 0) - COALESCE(gbp.creditamountinbasecurrency, 0) AS net_amount,
    COALESCE(gbp.narration, gbp.documenttypename, 'Pending GL posting') AS description,
    'UNPOSTED_GL_BATCH'::text AS source_type
FROM finance.generalledgerbeforeposting gbp
WHERE NOT COALESCE(gbp.isarchived, false);

-- 6. AR & AP Aging Views
DROP VIEW IF EXISTS rpt.ar_aging_v;
CREATE OR REPLACE VIEW rpt.ar_aging_v AS
SELECT
    'ALL'::text AS company_code,
    ar.businesspartnercode AS customer_code,
    ar.businesspartnername AS customer_name,
    ar.documentnumber AS document_number,
    ar.invoiceno AS invoice_no,
    ar.documentdate::date AS document_date,
    ar.transactiondate::date AS transaction_date,
    CASE
        WHEN (CURRENT_DATE - COALESCE(ar.documentdate, ar.transactiondate)::date) <= 30 THEN '0-30 Days'
        WHEN (CURRENT_DATE - COALESCE(ar.documentdate, ar.transactiondate)::date) <= 60 THEN '31-60 Days'
        WHEN (CURRENT_DATE - COALESCE(ar.documentdate, ar.transactiondate)::date) <= 90 THEN '61-90 Days'
        ELSE '>90 Days'
    END AS aging_bucket,
    ar.transactioncurrencycode AS currency_code,
    COALESCE(ar.debitvalueinbasecurrency, 0) AS debit_amount,
    COALESCE(ar.creditvalueinbasecurrency, 0) AS credit_amount,
    COALESCE(ar.balanceamountinbasecurrency, 0) AS balance_amount
FROM finance.accountsreceivable ar
WHERE NOT COALESCE(ar.isarchived, false);

CREATE OR REPLACE VIEW rpt.ap_aging_v AS
SELECT
    'ALL'::text AS company_code,
    ap.businesspartnercode AS vendor_code,
    ap.businesspartnername AS vendor_name,
    ap.documentnumber AS document_number,
    ap.invoiceno AS invoice_no,
    ap.documentdate::date AS document_date,
    ap.transactiondate::date AS transaction_date,
    ap.transactioncurrencycode AS currency_code,
    COALESCE(ap.debitvalueinbasecurrency, 0) AS debit_amount,
    COALESCE(ap.creditvalueinbasecurrency, 0) AS credit_amount,
    COALESCE(ap.balanceamountinbasecurrency, 0) AS balance_amount
FROM finance.accountspayable ap
WHERE NOT COALESCE(ap.isarchived, false);

GRANT USAGE ON SCHEMA rpt TO rb_reader;
GRANT SELECT ON ALL TABLES IN SCHEMA rpt TO rb_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA rpt GRANT SELECT ON TABLES TO rb_reader;

-- Grant rb_designer permissions to create approved views in rpt schema
DO $$
DECLARE s text;
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rb_designer') THEN
        GRANT USAGE, CREATE ON SCHEMA rpt TO rb_designer;
        FOREACH s IN ARRAY ARRAY['finance','sales','trading','common','hrms'] LOOP
            IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = s) THEN
                EXECUTE format('GRANT USAGE ON SCHEMA %I TO rb_designer', s);
                EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO rb_designer', s);
            END IF;
        END LOOP;
    END IF;
END $$;
