# AI Report Builder for ThomsunGroupConsoleUAT1 (C# / .NET, PostgreSQL)

Port of the ERP_Full Report Builder to PostgreSQL. See `MAPPING.md` for how the tables map and what is not available.

## Setup
1. As a Postgres owner, run `sql/20_rpt_views.pg.sql` then `sql/10_report_builder_support.pg.sql` (change the role passwords first). Not yet applied: the views were only validated in a rolled-back transaction.
2. Set secrets (the committed connection strings use `Password=CHANGE_ME`):
   ```
   cd src/ReportBuilder.Web
   dotnet user-secrets set "Database:ReaderConnectionString" "Host=192.168.100.53;Port=5432;Database=ThomsunGroupConsoleUAT1;Username=rb_reader;Password=..."
   dotnet user-secrets set "Database:AppConnectionString" "...Username=rb_app;..."
   dotnet user-secrets set "Database:DesignerConnectionString" "...Username=rb_designer;..."
   dotnet user-secrets set "Llm:ApiKey" "<key>"
   ```
3. Generate and approve the catalog: `dotnet run --project src/ReportBuilder.Cli -- generate --questions questions.json --out out --load`, then open `/catalog`.
4. Tests: `dotnet test` (112 pass; they use fakes, not the database).

Status: builds and unit tests pass. Not yet run against the live database through the app (no role setup, no LLM key).
