# What ReportBuilder Is For

ReportBuilder lets people who don't know SQL get answers from the company's ERP data by asking in plain English. The usual alternatives are waiting on IT or BI to build a report, or exporting data to Excel and working it out by hand.

Example questions:

- "Show receivables aging by region for company 1000"
- "Net revenue trend"
- "Why doesn't the general ledger match the AR subledger?"

The user gets a table or chart plus a short written summary.

## Who uses it

| User | How they use it |
| --- | --- |
| Finance and accounting staff | Ask routine questions about receivables, revenue and balances |
| Managers and executives | Get numbers without raising a ticket to the reporting team |
| Auditors and controllers | Use reconciliation questions and the audit log |
| Catalog owners | Approve which reports exist and what they mean |
| IT and BI teams | Build fewer one-off reports |

## How it helps end users

1. **No SQL or report-tool training.** Type a question, get an answer.
2. **Fast answers.** Seconds instead of a request queue. The UI shows live progress.
3. **Numbers you can trust.** The AI never writes the SQL that runs. Every report comes from a catalog of approved definitions, so "revenue" means the same thing for everyone. A result that fails a ledger control check is labeled **Unverified** rather than shown as fact.
4. **It asks when unsure.** If a term could mean two reports, or no company is given, it asks which one you mean instead of guessing.
5. **Reconciliation help.** Ask why the general ledger and a subledger differ. The app compares the balances and explains the difference.
6. **You only see what you're allowed to see.** Your AD group decides which companies and views you can query, how many rows you get, and which columns are blocked. Queries run with your identity.
7. **Transparency.** The result shows how your wording was mapped to the report and the exact SQL that ran, so you can check it and spot a misreading.

## How it helps the organization

- **Safe by design.** SQL is checked twice. It is read-only, limited to the `rpt` views, and runs on a login that cannot change data.
- **Complete audit trail.** Every request is logged in a tamper-evident, hash-chained table. If the log cannot be written, no result is released.
- **Consistent definitions.** Only a catalog owner can certify a report, so teams stop producing conflicting numbers.
- **Less load on BI and IT.** Routine questions become self-service, which frees specialists for new work.
- **Extensible.** New metrics are added through the catalog generator or the NewMetric page, not new code.

## How it works, briefly

A model reads the question and picks an approved report from the catalog. Deterministic code then builds the SQL, checks it, runs it as the user, validates the result against ledger controls, and has a model write the summary. The summary model sees facts only, never the raw rows. See `README.md` for the nine pipeline steps and the code that implements each.

## Limits

- It answers only what is in the approved catalog. Forecasts, ad-hoc cross-module joins and data changes are refused as unsupported.
- Each report has one measure, simple filters and catalog-defined groupings.
- Term matching uses synonyms and word overlap, not embeddings, so unusual wording may need a clarification.

## Summary

ReportBuilder gives business users self-service access to ERP data while keeping the control, consistency and auditability that IT would otherwise provide by building each report by hand.
