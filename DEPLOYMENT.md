# Deployment & Multi-Environment Setup Guide (`ReportEngine.Web`)

This guide records every database script, role, permission, and configuration setting required to deploy **TERO AI Report Engine** into any environment (**Dev**, **QA**, **UAT**, or **Production**).

---

## 1. Architecture Summary
- **Console / RBAC Database** (e.g., `ThomsunGroupConsoleUAT1` or `ConsoleDB-QA`):
  - Holds `common."AspNetUsers"`, `"AspNetRoles"`, `"AspNetUserRoles"`, `"AspNetRoleClaims"`, `"AspNetUserClaims"`, `common.consoleusermenuaccess`, `common.usercompany`, `common.company`.
  - Holds Report Engine metadata & audit tables in `cat.*` and `audit.reportrun`.
- **Operational Company Databases** (e.g., `ChefDB-QA`, `ThomsunGeneralTradingMain_UAT`, `ThomsunFoodLLCUAT`, `ThomsunFoodServiceLLC_UAT`, `ThePrintHouse`):
  - Hold `rpt.*` reporting views over `sales`, `trading`, and `finance` schemas.
  - Queried strictly read-only via the `rb_reader` PostgreSQL role.
- **Zero-DB-Access AI**:
  - The AI model (`AzureOpenAI`, `OpenAI`, or local `Ollama`) receives **only** permitted `rpt.*` view/column metadata and outputs a JSON Query Spec (`DynamicReportRequest`).
  - Deterministic C# (`DynamicReportCompiler`) validates identifiers, applies RBAC & `company_code` scoping, checks `SqlGuard`, and executes the parameterized `SELECT`.

---

## 2. Database Setup (Idempotent)

### Step A: Update role passwords (Production / QA)
Before running in a new environment, change the default passwords in `sql/10_report_builder_support.pg.sql` (or run `ALTER ROLE rb_reader WITH PASSWORD '...'; ALTER ROLE rb_app WITH PASSWORD '...';` afterward).

### Step B: Run the deployment script
```bash
chmod +x sql/deploy.sh
PGHOST=192.168.100.53 PGPORT=5432 PGUSER=postgres PGPASSWORD='<password>' \
CONSOLE_DB=ThomsunGroupConsoleUAT1 \
COMPANY_DBS="ChefDB-QA ThomsunGeneralTradingMain_UAT ThomsunFoodLLCUAT ThomsunFoodServiceLLC_UAT ThePrintHouse" \
./sql/deploy.sh
```

What `sql/deploy.sh` applies:
1. **On Console DB**:
   - `sql/20_rpt_views.pg.sql` (Console master-data `rpt.*` views)
   - `sql/10_report_builder_support.pg.sql` (`rb_reader`, `rb_app`, `rb_designer` roles, `cat.*`, `audit.reportrun` with `SELECT, INSERT, UPDATE` grant for `FOR UPDATE` hash chaining + immutable trigger)
   - `sql/30_report_engine_extensions.pg.sql` (`cat.company_database_map`, `cat.erp_area`, `cat.module_view_map`, `GRANT USAGE ON SCHEMA common TO rb_app`)
2. **On Each Company DB**:
   - `sql/40_company_erp_views.pg.sql` (`sales_invoice_item_v`, `general_ledger_v`, `trial_balance_v`, `control_totals_v`, `manual_postings_v`, `ar_aging_v`, `ap_aging_v`)
   - `sql/50_company_erp_views_extended.pg.sql` (`stock_on_hand_v`, `purchase_invoice_item_v`, `sales_order_item_v`, `sales_return_item_v`, `bank_reconciliation_v`)

---

## 3. Application Configuration (`src/ReportEngine.Web/`)

### A. `tenants.json`
Drop your environment's `tenants.json` into `src/ReportEngine.Web/tenants.json`.
`CompanyDataSourceFactory` matches `Tenants[].Host` against `company_code` (e.g., `CAP`) or `common.company.host`, and `cat.company_database_map` in the Console DB can override any mapping.

### B. Switching Between Local (`Ollama`) and Cloud (`Azure`)
`ReportEngine.Web` supports a `Profile` setting (`Ollama` or `Azure`) via [appsettings.json](src/ReportEngine.Web/appsettings.json), `--Profile=Azure`, or `Profile=Azure` environment variable:
- **Local (`Profile=Ollama`)**: Uses [appsettings.Ollama.json](src/ReportEngine.Web/appsettings.Ollama.json) (`http://localhost:11434`, `gemma3:latest`).
- **Azure OpenAI (`Profile=Azure`)**: Uses [appsettings.Azure.json](src/ReportEngine.Web/appsettings.Azure.json) (`gpt-4o-mini` / `gpt-4.1-mini`). Set your key once via:
  ```bash
  dotnet user-secrets --project src/ReportEngine.Web set "Llm:ApiKey" "<your-azure-openai-key>"
  dotnet run --project src/ReportEngine.Web --Profile=Azure
  ```

### C. `appsettings.json` (or `dotnet user-secrets` / Environment Variables)
- `UatMode`: `true` enables the Quick UAT User Switcher; set to `false` in Production.
- `Database:ReaderConnectionString` & `Database:AppConnectionString`: point to your Console DB with `rb_reader` and `rb_app`.
- `Llm`:
  - **Azure OpenAI / OpenAI (Recommended for Production)**: Set `"Provider": "AzureOpenAI"`, `"Endpoint"`, `"ApiKey"`, and `"Deployments": { "fast": "gpt-4o-mini", "planner": "gpt-4.1-mini", "presenter": "gpt-4o-mini", "drafter": "gpt-4.1-mini" }`.
  - **Local Ollama**: Set `"Provider": "Ollama"`, `"Endpoint": "http://localhost:11434"`, and `"Deployments"` to your local model (e.g. `gemma3:latest` or `qwen2.5:14b-instruct`).

---

## 4. Adding New Reports / Views Later (Zero Code Changes)
1. Create a `CREATE OR REPLACE VIEW rpt.<new_view_v> AS ...` in the target company DB(s) and `GRANT SELECT ON rpt.<new_view_v> TO rb_reader;`.
2. Insert a row into `cat.erp_area` (area, display name, description, `date_column`, `default_fields`, `synonyms_hint`).
3. Insert RBAC mapping row(s) into `cat.module_view_map` (or use the **RBAC Administration** modal in the UI).
4. The new view and all its fields immediately appear in the UI and AI Query Planner.

---

## 5. Run & Verify
```bash
dotnet test ReportBuilder.sln
dotnet run --project src/ReportEngine.Web --urls http://localhost:5099
```
