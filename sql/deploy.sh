#!/usr/bin/env bash
set -euo pipefail

# Repeatable PostgreSQL setup script for TERO AI Report Engine.
# Usage:
#   PGHOST=192.168.100.53 PGPORT=5432 PGUSER=postgres PGPASSWORD=... \
#   CONSOLE_DB=ThomsunGroupConsoleUAT1 \
#   COMPANY_DBS="ChefDB-QA ThomsunGeneralTradingMain_UAT ThomsunFoodLLCUAT ThomsunFoodServiceLLC_UAT ThePrintHouse" \
#   ./sql/deploy.sh

PGHOST="${PGHOST:-192.168.100.53}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-postgres}"
CONSOLE_DB="${CONSOLE_DB:-ThomsunGroupConsoleUAT1}"
COMPANY_DBS="${COMPANY_DBS:-ChefDB-QA ThomsunGeneralTradingMain_UAT ThomsunFoodLLCUAT ThomsunFoodServiceLLC_UAT ThePrintHouse}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== 1. Applying Console DB scripts to ${CONSOLE_DB} ==="
psql "host=${PGHOST} port=${PGPORT} dbname=${CONSOLE_DB} user=${PGUSER}" -v ON_ERROR_STOP=1 \
  -f "${SCRIPT_DIR}/20_rpt_views.pg.sql" \
  -f "${SCRIPT_DIR}/10_report_builder_support.pg.sql" \
  -f "${SCRIPT_DIR}/30_report_engine_extensions.pg.sql"

for db in ${COMPANY_DBS}; do
  echo "=== 2. Applying Company ERP views to ${db} ==="
  psql "host=${PGHOST} port=${PGPORT} dbname=${db} user=${PGUSER}" -v ON_ERROR_STOP=1 \
    -f "${SCRIPT_DIR}/40_company_erp_views.pg.sql" \
    -f "${SCRIPT_DIR}/50_company_erp_views_extended.pg.sql"
done

echo "=== Deployment complete ==="
