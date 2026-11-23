-- AI Token Usage, Cost & Savings Telemetry for FinOps Dashboard
-- Run on the Console database (ThomsunGroupConsoleUAT1) after 60_cache_and_saved_reports.pg.sql.

CREATE SCHEMA IF NOT EXISTS audit;

CREATE TABLE IF NOT EXISTS audit.token_usage (
    event_id bigserial PRIMARY KEY,
    occurred_at_utc timestamp NOT NULL DEFAULT (now() at time zone 'utc'),
    user_name varchar(120) NOT NULL,
    company_code varchar(30) NOT NULL DEFAULT '',
    feature varchar(40) NOT NULL,
    outcome varchar(30) NOT NULL,
    provider varchar(40) NOT NULL DEFAULT '',
    model_role varchar(30) NOT NULL DEFAULT '',
    deployment varchar(80) NOT NULL DEFAULT '',
    prompt_tokens int NOT NULL DEFAULT 0,
    completion_tokens int NOT NULL DEFAULT 0,
    cached_prompt_tokens int NOT NULL DEFAULT 0,
    total_tokens int NOT NULL DEFAULT 0,
    saved_tokens int NOT NULL DEFAULT 0,
    estimated_cost_usd numeric(12,6) NOT NULL DEFAULT 0,
    saved_cost_usd numeric(12,6) NOT NULL DEFAULT 0,
    duration_ms int NOT NULL DEFAULT 0,
    is_estimated boolean NOT NULL DEFAULT false,
    question_preview varchar(220) NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS ix_token_usage_occurred_at ON audit.token_usage (occurred_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_token_usage_user_occurred ON audit.token_usage (user_name, occurred_at_utc DESC);

GRANT USAGE ON SCHEMA audit TO rb_app;
GRANT SELECT, INSERT, DELETE ON audit.token_usage TO rb_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA audit TO rb_app;
