-- Report Builder support objects for PostgreSQL. Run as a superuser/owner. Change the passwords first.
CREATE SCHEMA IF NOT EXISTS cat; CREATE SCHEMA IF NOT EXISTS audit; CREATE SCHEMA IF NOT EXISTS rpt;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='rb_reader')   THEN CREATE ROLE rb_reader   LOGIN PASSWORD 'ChangeMe_Reader_2026'; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='rb_app')      THEN CREATE ROLE rb_app      LOGIN PASSWORD 'ChangeMe_App_2026'; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='rb_designer') THEN CREATE ROLE rb_designer LOGIN PASSWORD 'ChangeMe_Designer_2026'; END IF;
END $$;

CREATE TABLE IF NOT EXISTS cat.metric (
  metric_id int PRIMARY KEY, name varchar(100) NOT NULL, description varchar(400), view_name varchar(80) NOT NULL,
  measure_expr varchar(200) NOT NULL, sql_template text NOT NULL, control_areas varchar(200),
  is_certified boolean NOT NULL DEFAULT false,
  review_status varchar(20) NOT NULL DEFAULT 'UNVERIFIED' CHECK (review_status IN ('UNVERIFIED','CERTIFIED','REJECTED')),
  source_question varchar(400), reviewed_by varchar(100), reviewed_at timestamp(3), review_note varchar(400),
  chart_type varchar(10) NOT NULL DEFAULT 'auto');
CREATE TABLE IF NOT EXISTS cat.dimension (metric_id int NOT NULL REFERENCES cat.metric(metric_id), column_name varchar(80) NOT NULL, PRIMARY KEY (metric_id, column_name));
CREATE TABLE IF NOT EXISTS cat.synonym (term varchar(100) NOT NULL, metric_id int NOT NULL REFERENCES cat.metric(metric_id), PRIMARY KEY (term, metric_id));
CREATE TABLE IF NOT EXISTS cat.viewdraft (
  draft_id serial PRIMARY KEY, name varchar(80) NOT NULL, description varchar(400) NOT NULL DEFAULT '', definition_json text NOT NULL, sql_text text NOT NULL,
  status varchar(20) NOT NULL DEFAULT 'PENDING', created_by varchar(200) NOT NULL, created_at timestamp NOT NULL DEFAULT (now() at time zone 'utc'),
  reviewed_by varchar(200), reviewed_at timestamp, review_note varchar(400));

CREATE TABLE IF NOT EXISTS audit.reportrun (
  run_id bigserial PRIMARY KEY, run_at timestamp(3) NOT NULL, user_name varchar(200) NOT NULL, question text NOT NULL, status varchar(30) NOT NULL,
  metric_id int, sql_text text, row_count int NOT NULL, findings text NOT NULL, duration_ms int NOT NULL, prev_hash varchar(64), row_hash varchar(64) NOT NULL);
CREATE OR REPLACE FUNCTION audit.reportrun_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'audit.reportrun is insert-only.'; END $$;
DROP TRIGGER IF EXISTS reportrun_immutable ON audit.reportrun;
CREATE TRIGGER reportrun_immutable BEFORE UPDATE OR DELETE ON audit.reportrun FOR EACH ROW EXECUTE FUNCTION audit.reportrun_immutable();

-- permissions (views run with the owner's rights, so rb_reader needs no access to common.*)
GRANT USAGE ON SCHEMA rpt TO rb_reader;   GRANT SELECT ON ALL TABLES IN SCHEMA rpt TO rb_reader;
ALTER DEFAULT PRIVILEGES IN SCHEMA rpt GRANT SELECT ON TABLES TO rb_reader;
GRANT USAGE ON SCHEMA cat, audit TO rb_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA cat TO rb_app;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA cat, audit TO rb_app;
GRANT SELECT, INSERT, UPDATE ON audit.reportrun TO rb_app;
GRANT USAGE ON SCHEMA common, rpt TO rb_designer; GRANT SELECT ON ALL TABLES IN SCHEMA common TO rb_designer; GRANT CREATE ON SCHEMA rpt TO rb_designer;
