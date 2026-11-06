#!/usr/bin/env bash
# Tests compose.production.yaml locally in a throwaway stack: restricted SQL logins, the
# migration identity, and the HTTPS proxy. Your databridge stack and its data aren't touched.
# Set PYTHON to an interpreter that has playwright for the browser check.
set -uo pipefail

python_bin="${PYTHON:-python3}"
export COMPOSE_PROJECT_NAME="databridge-production-check"
export COMPOSE_FILE="compose.yaml:compose.production.yaml"
export DATABRIDGE_PROXY_BIND=127.0.0.1 DATABRIDGE_HTTP_PORT="${PRODUCTION_HTTP_PORT:-8081}" DATABRIDGE_HTTPS_PORT="${PRODUCTION_HTTPS_PORT:-8443}"
export DATABRIDGE_BASE_URL="https://localhost:$DATABRIDGE_HTTPS_PORT" DATABRIDGE_INSECURE_TLS=1
export PYTHONDONTWRITEBYTECODE=1
DATABRIDGE_SECRETS_DIR="$(mktemp -d)"
export DATABRIDGE_SECRETS_DIR

cleanup() {
  docker compose down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$DATABRIDGE_SECRETS_DIR"
}
trap cleanup EXIT

./scripts/setup-secrets.sh >/dev/null
write_setting() { printf '%s' "$2" > "$DATABRIDGE_SECRETS_DIR/app/$1"; }
write_setting Seed__Users__0__Email 'production-admin@example.test'
write_setting Seed__Users__0__Password "Prod$(openssl rand -hex 16)Aa1!"
write_setting Seed__Users__0__Role Admin
write_setting Seed__Users__1__Email 'production-reviewer@example.test'
write_setting Seed__Users__1__Password "Rev$(openssl rand -hex 16)Aa1!"
write_setting Seed__Users__1__Role Reviewer
chmod 755 "$DATABRIDGE_SECRETS_DIR"
chmod 644 "$DATABRIDGE_SECRETS_DIR"/app/*

docker compose up -d --build --wait || { docker compose ps -a; docker compose logs --tail 60; exit 1; }

status=0
python3 checks/production_setup.py || status=1
docker compose restart app >/dev/null 2>&1
docker compose up -d --no-deps --wait app >/dev/null 2>&1
python3 checks/stale_approval.py || status=1
"$python_bin" checks/review_browser.py || status=1
exit $status
