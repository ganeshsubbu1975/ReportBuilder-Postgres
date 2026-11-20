# ERP_Full (SQL Server) to ThomsunGroupConsoleUAT1 (PostgreSQL) mapping

Introspected read-only from `192.168.100.53:5432/ThomsunGroupConsoleUAT1` (schemas `common` 82 tables, `public` 5 tables).

**Key finding:** the Postgres database holds master data only. It has no sales, purchasing, inventory, AR/AP or GL tables,
so the transactional ERP_Full modules (`sal`, `pur`, `inv`, `ar.Invoice`, `ap.Invoice`, `fin.Journal*`, `mfg`, `hr.Payroll*`) have no counterpart.

| ERP_Full (SQL Server) | PostgreSQL source | rpt view (new) | Rows | Notes |
| --- | --- | --- | --- | --- |
| org.Company | common.company | rpt.company_v | 19 | company_code = `code` (TGC, CAP, TPH, ...) |
| ar.Customer + ap.Vendor | common.businesspartner | rpt.business_partner_v | 14,672 | One table; `bptype` codes B, EBP, S, OBP, C, s, ABP, HBP. No company key: `sourcecompany` is used, else `ALL` |
| hr.Employee | common.employee (+ company) | rpt.employee_v | 398 | company via `companyid`. Salary not present |
| hr.Department | common.department | rpt.department_v | 16 | shared, company_code `ALL` |
| (none) designation | common.designation | rpt.designation_v | 67 | |
| org.Currency / org.ExchangeRate | common.currency, currencyexchangerate | rpt.currency_rate_v | 27 | |
| org.FiscalPeriod | common.financialyear + financialyearperiod | rpt.fiscal_period_v | 120 | |
| (tax setup) | common.tax | rpt.tax_rate_v | 2 | |
| (bank master) | common.bank | rpt.bank_v | 36 | |
| inv.Material | common.item + itemcommodity/class/family/segment | rpt.item_master_v | 0 | table is empty in UAT |
| org.Region, Plant, Warehouse, CostCenter, UnitOfMeasure | none | n/a | | not in this database |
| fin.*, sal.*, pur.*, inv.Stock*, ar.Invoice/Receipt, ap.Invoice/Payment, mfg.*, hr.Payroll* | none | n/a | | not in this database |

## Other conversions
- `Microsoft.Data.SqlClient` replaced by `Npgsql`; `TOP`/`OFFSET FETCH` became `LIMIT`; `SESSION_CONTEXT` became `set_config('rb.user', ...)`.
- `cat.*` and `audit.reportrun` recreated in Postgres (insert-only trigger kept). Logins became roles `rb_reader`, `rb_app`, `rb_designer`.
- Designer edits use `CREATE OR REPLACE VIEW` (Postgres has no `ALTER VIEW ... AS`).
- `SqlGuard` forbids `pg_*`, `copy`, `dblink`, `set_config` and the schemas `common`, `public`, `pg_catalog`.
- `Reconciler` still expects `rpt.control_totals_v`, `manual_postings_v`, `trial_balance_v`. They do not exist here, so reconcile questions are unsupported until GL data exists.
- `questions.json` rewritten for the master-data views (13 answerable, 2 ambiguous, 4 unsupported, 3 unsafe).
