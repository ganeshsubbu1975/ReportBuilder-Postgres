-- ThomsunGroupConsoleUAT1 (PostgreSQL): reporting views over the common.* master data.
-- Every view exposes company_code because the Report Builder compiler filters on it.
-- Tables without a company key use 'ALL' (shared master data) so the policy gate can still scope them.
CREATE SCHEMA IF NOT EXISTS rpt;

CREATE OR REPLACE VIEW rpt.company_v AS
SELECT c.code AS company_code, c.name AS company_name, c.groupcode AS group_code, c.currencycode AS currency_code,
       c.countryname AS country, c.statename AS state, c.cityname AS city, c.taxregistrationnumber AS tax_registration_no,
       c.basecompanycode AS base_company_code, (NOT COALESCE(c.isarchived, false)) AS is_active, 1 AS company_count
FROM common.company c;

CREATE OR REPLACE VIEW rpt.business_partner_v AS
SELECT COALESCE(NULLIF(b.sourcecompany, ''), 'ALL') AS company_code, b.code AS partner_code, b.name AS partner_name,
       b.bptype AS partner_type, b.currencycode AS currency_code, b.countryname AS country, b.statename AS state, b.cityname AS city,
       b.taxregistrationnumber AS tax_registration_no, b.taxname AS tax_name,
       COALESCE(b.isactive, false) AS is_active, COALESCE(b.isarchived, false) AS is_archived,
       b.createddate::date AS created_date, 1 AS partner_count
FROM common.businesspartner b;

CREATE OR REPLACE VIEW rpt.employee_v AS
SELECT COALESCE(c.code, 'ALL') AS company_code, e.employeecode AS employee_code, e.displayname AS employee_name,
       e.department AS department_name, e.designation AS designation_name, e.gender,
       e.dateofjoining::date AS joining_date, COALESCE(e.isactive, false) AS is_active, 1 AS employee_count
FROM common.employee e
LEFT JOIN common.company c ON c.id = e.companyid;

CREATE OR REPLACE VIEW rpt.department_v AS
SELECT 'ALL'::text AS company_code, d.code AS department_code, d.name AS department_name, 1 AS department_count
FROM common.department d WHERE NOT COALESCE(d.isarchived, false);

CREATE OR REPLACE VIEW rpt.designation_v AS
SELECT 'ALL'::text AS company_code, d.code AS designation_code, d.name AS designation_name, 1 AS designation_count
FROM common.designation d WHERE NOT COALESCE(d.isarchived, false);

CREATE OR REPLACE VIEW rpt.currency_rate_v AS
SELECT 'ALL'::text AS company_code, r.basecurrencycode AS base_currency, r.transactioncurrencycode AS transaction_currency,
       r.exchangedate AS exchange_date, r.exchangerate AS exchange_rate
FROM common.currencyexchangerate r WHERE NOT COALESCE(r.isarchived, false);

CREATE OR REPLACE VIEW rpt.fiscal_period_v AS
SELECT 'ALL'::text AS company_code, y.code AS fiscal_year_code, y.name AS fiscal_year_name, p.period,
       p.fromdate::date AS from_date, p.todate::date AS to_date, p.status AS period_status, 1 AS period_count
FROM common.financialyearperiod p JOIN common.financialyear y ON y.id = p.financialyearid;

CREATE OR REPLACE VIEW rpt.tax_rate_v AS
SELECT 'ALL'::text AS company_code, t.taxcode AS tax_code, t.taxname AS tax_name, t.taxtype AS tax_type, t.taxpercent AS tax_percent,
       t.taxjurisdictioncode AS jurisdiction_code
FROM common.tax t WHERE NOT COALESCE(t.isarchived, false);

CREATE OR REPLACE VIEW rpt.bank_v AS
SELECT 'ALL'::text AS company_code, b.code AS bank_code, b.name AS bank_name, 1 AS bank_count
FROM common.bank b WHERE NOT COALESCE(b.isarchived, false);

CREATE OR REPLACE VIEW rpt.item_master_v AS
SELECT 'ALL'::text AS company_code, i.itemcode AS item_code, i.itemname AS item_name, cm.itemcommodityname AS commodity,
       cl.itemclassname AS item_class, f.itemfamilyname AS item_family, s.itemsegmentname AS item_segment, 1 AS item_count
FROM common.item i
LEFT JOIN common.itemcommodity cm ON cm.id = i.itemcommodityid
LEFT JOIN common.itemclass cl ON cl.id = cm.itemclassid
LEFT JOIN common.itemfamily f ON f.id = cl.itemfamilyid
LEFT JOIN common.itemsegment s ON s.id = f.itemsegmentid
WHERE i.iscurrentrevision AND NOT COALESCE(i.isarchived, false);
