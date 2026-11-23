# TERO ERP — AI Report Engine (`ReportEngine.Web` & `ReportBuilder.Core`)

**TERO AI Report Engine** is a Zero-DB-Access, RBAC-governed, deterministic AI reporting platform for **PostgreSQL** (`ThomsunGroupConsoleUAT1` + operational company databases). Users ask reporting questions in plain English, and AI models plan structured JSON configurations from permitted `rpt.*` metadata only — deterministic C# compiles, validates, audits, and executes every SQL query without ever exposing database rows to the model.

See [ARCHITECTURE.md](ARCHITECTURE.md) for system diagrams and detailed component design, [DEPLOYMENT.md](DEPLOYMENT.md) for environment/database setup, [MAPPING.md](MAPPING.md) for Console DB table mappings, and [`documents/`](documents/) for the offline Query Catalog PDFs.

---

## Key Capabilities

- **Zero-DB-Access AI Query Planner (`AiReportAssistant`)**:
  - Sees **only** permitted `rpt.*` view and column metadata — never connects to PostgreSQL, never writes raw SQL, and never sees database rows.
- **Multi-Layered Anti-Hallucination & Scope Guardrails (`ScopeGuardrail`, `GuardrailOutputVerifier`, `SqlGuard`)**:
  - **Pre-LLM Input Guardrail** blocks prompt injections, write/DDL attempts, forecasts/predictions, out-of-scope trivia/coding prompts, and restricted PII/blocked columns before any tokens are spent.
  - **Closed-World Strict JSON Schemas (`strict: true`)** with `Temperature: 0`.
  - **Post-LLM Spec & Summary Verifiers** reject hallucinated views, non-existent columns, invalid `SUM`/`AVG` on non-numeric fields, ungrounded filter values, and fabricated summary numbers.
- **Two-Tier RBAC-Safe AI Plan Caching (`AiPlanCache`)**:
  - **L1 (In-Memory, 24h TTL, 1,000-entry cap)** + **L2 (PostgreSQL `cat.ai_plan_cache`)**.
  - Keyed by SHA-256 of normalized prompt + company scope + max rows + **user's exact permitted views & blocked-columns hash** (`schema_hash`), ensuring strict RBAC isolation and automatic invalidation whenever views or permissions change.
  - Repeated natural-language prompts replay in milliseconds with **0 AI tokens**. Includes a **Bypass cache** toggle and Admin cache stats/clear controls.
- **Saved Reports / Favorites (`SqlSavedReportStore` / `cat.saved_report`)**:
  - Users can save any AI-configured or manually tuned report as a personal or shared favorite and re-run it anytime with **1 click and 0 AI tokens**.
  - Saved specs are re-compiled dynamically through `DynamicReportCompiler` on every run so relative date windows (`last_3_quarters`, `ytd`, etc.) stay fresh and live RBAC permissions are always re-checked.
- **Multi-Company Fan-Out (`Company = ALL`) & Executive PDF Packs**:
  - Queries execute in parallel across permitted company databases (`CAP`, `TGTM`, `TF`, `TFS`, `TPH`, `TGC`) with automatic `company_code` row tagging and graceful skipping when a view does not exist on a given DB.
  - Supports **Excel (`.xlsx`)**, **PDF**, **CSV**, **JSON**, and 1-click **PDF Report Pack** (single-company or cross-company).
- **Tamper-Evident Audit Logging & FinOps Token Usage Dashboard (`audit.reportrun`, `audit.token_usage`)**:
  - Insert-only query audit trail (`audit.reportrun`) with SHA-256 hash chaining (`prev_hash` -> `row_hash`).
  - Graphical **Token Usage & Savings Dashboard** (`Token Analytics` / left sidebar) backed by `audit.token_usage` ([70_token_usage.pg.sql](sql/70_token_usage.pg.sql)) and `GET /api/analytics/tokens`, visualizing live Azure OpenAI / Ollama token spend, estimated USD cost, 0-token savings (Cache Hits, Saved Replays, Pre-LLM Guardrails, Direct SQL), 30-day projections, and interactive Model What-If simulations with CSV export.

---

## Token Usage Matrix

| Action / Feature | Calls LLM? | AI Tokens Used | Notes |
| --- | --- | --- | --- |
| Guardrail-blocked prompt (out-of-scope / injection / write / PII) | No | **0** | Stopped deterministically by `ScopeGuardrail.InspectInput` before any LLM call |
| Repeated natural-language AI query (Cache Hit — L1 Memory or L2 DB) | No | **0** | Served by `AiPlanCache` with RBAC + schema hash verification |
| Saved Report / Favorite 1-click replay (`Run` / `Load`) | No | **0** | Compiled & executed deterministically with live RBAC & fresh relative dates |
| Manual form **Generate Report**, Table search, Grouping, Charts | No | **0** | Pure C# compilation (`DynamicReportCompiler`) + PostgreSQL + Chart.js |
| Exports (Excel, PDF, CSV, JSON, **PDF Report Pack**) | No | **0** | Generated directly from compiled query results via ClosedXML & QuestPDF |
| First-time natural-language **Auto-Configure** / **Ask & Run** (Cache Miss) | Yes (1 call) | Planner (`gpt-4o` / Ollama) | Sends permitted `rpt.*` metadata + question only; result is cached for reuse |
| GL vs. Trial Balance **Reconciliation** (`POST /api/reconcile`) | Yes (1 call, summary only) | Presenter (`gpt-4.1` / Ollama) | Deterministic `Reconciler` runs SQL; LLM summarizes verified control facts only |
| Admin **Suggest View Draft** (`POST /api/views/suggest-draft`) | Yes (1 call) | Drafter (`gpt-4.1` / Ollama) | Suggests structured `ViewRequest` JSON over non-sensitive `information_schema` |

---

## Quick Start

1. **Apply Database Objects** (idempotent across Console DB + company DBs — see [DEPLOYMENT.md](DEPLOYMENT.md)):
   ```bash
   PGHOST=192.168.100.53 PGPORT=5432 PGUSER=postgres PGPASSWORD='<password>' ./sql/deploy.sh
   ```
2. **Configure Local Secrets** (`src/ReportEngine.Web`, committed `appsettings*.json` use `Password=CHANGE_ME` and empty `ApiKey`):
   ```bash
   dotnet user-secrets --project src/ReportEngine.Web set "Database:ReaderConnectionString" "Host=192.168.100.53;Port=5432;Database=ThomsunGroupConsoleUAT1;Username=rb_reader;Password=..."
   dotnet user-secrets --project src/ReportEngine.Web set "Database:AppConnectionString" "Host=192.168.100.53;Port=5432;Database=ThomsunGroupConsoleUAT1;Username=rb_app;Password=..."
   dotnet user-secrets --project src/ReportEngine.Web set "Database:DesignerConnectionString" "Host=192.168.100.53;Port=5432;Database=ThomsunGroupConsoleUAT1;Username=rb_designer;Password=..."
   dotnet user-secrets --project src/ReportEngine.Web set "Llm:ApiKey" "<your-azure-openai-key>"
   ```
3. **Run Tests**:
   ```bash
   dotnet test tests/ReportBuilder.Tests/ReportBuilder.Tests.csproj
   ```
4. **Run the Web App** (`Profile: "Azure"` is default in [appsettings.json](src/ReportEngine.Web/appsettings.json); pass `--Profile=Ollama` for local models):
   ```bash
   dotnet run --project src/ReportEngine.Web --urls http://localhost:5099
   ```

---

## CLI Utilities (`ReportEngine.Web`)

- Export offline **Query Catalog PDFs** (0 live rows) to `documents/`:
  ```bash
  dotnet run --project src/ReportEngine.Web -- --export-query-catalog-pdfs "documents"
  ```
- Export live **Cross-Company Group PDF Pack**:
  ```bash
  dotnet run --project src/ReportEngine.Web -- --export-group-pdf "TERO_CrossCompany_Group_Reports.pdf"
  ```
- Export live **Single-Company PDF Pack**:
  ```bash
  dotnet run --project src/ReportEngine.Web -- --export-company-pdf "TERO_SingleCompany_CAP.pdf" --company CAP
  ```
