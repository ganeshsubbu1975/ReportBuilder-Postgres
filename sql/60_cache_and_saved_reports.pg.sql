-- Token Optimization Extensions: Two-Tier RBAC-Safe AI Plan Cache & Saved Reports (0-Token Replay)
-- Run on the Console database (ThomsunGroupConsoleUAT1) after 30_report_engine_extensions.pg.sql.

CREATE TABLE IF NOT EXISTS cat.ai_plan_cache (
    cache_key varchar(64) PRIMARY KEY,
    normalized_question text NOT NULL,
    company_code varchar(30) NOT NULL,
    schema_hash varchar(64) NOT NULL,
    result_json text NOT NULL,
    hit_count int NOT NULL DEFAULT 0,
    created_at_utc timestamp NOT NULL DEFAULT (now() at time zone 'utc'),
    last_hit_at_utc timestamp NOT NULL DEFAULT (now() at time zone 'utc')
);

CREATE TABLE IF NOT EXISTS cat.saved_report (
    report_id serial PRIMARY KEY,
    name varchar(160) NOT NULL,
    description varchar(400) NOT NULL DEFAULT '',
    owner_user varchar(120) NOT NULL,
    is_shared boolean NOT NULL DEFAULT false,
    company_code varchar(30) NOT NULL,
    view_name varchar(80) NOT NULL,
    prompt_text text NOT NULL DEFAULT '',
    request_json text NOT NULL,
    run_count int NOT NULL DEFAULT 0,
    created_at_utc timestamp NOT NULL DEFAULT (now() at time zone 'utc'),
    last_run_at_utc timestamp
);

GRANT SELECT, INSERT, UPDATE, DELETE ON cat.ai_plan_cache, cat.saved_report TO rb_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA cat TO rb_app;
