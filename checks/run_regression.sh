#!/usr/bin/env bash
# Runs the regression checks in a throwaway Compose stack with synthetic data and the fake
# OpenAI server. It never makes a paid call. Your databridge stack and its data aren't touched.
#   checks/run_regression.sh                     every check
#   REGRESSION_CHECKS="guest_access ai_cap" checks/run_regression.sh
# Set PYTHON to an interpreter that has playwright and pillow for the browser checks.
set -uo pipefail

python_bin="${PYTHON:-python3}"
all_checks="guest_access stale_approval review_workflow health_outage ai_cap file_retention import_10k recovery ai_eval_mock review_browser theme_browser"
checks="${REGRESSION_CHECKS:-$all_checks}"

export COMPOSE_PROJECT_NAME="databridge-regression"
export COMPOSE_FILE="compose.yaml:compose.mock.yaml"
export DATABRIDGE_PORT="${REGRESSION_PORT:-8090}"
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
write_setting Seed__Users__0__Email 'regression-admin@example.test'
write_setting Seed__Users__0__Password "Reg$(openssl rand -hex 16)Aa1!"
write_setting Seed__Users__0__Role Admin
write_setting Seed__Users__1__Email 'regression-reviewer@example.test'
write_setting Seed__Users__1__Password "Rev$(openssl rand -hex 16)Aa1!"
write_setting Seed__Users__1__Role Reviewer
write_setting OpenAI__ApiKey mock-key
chmod 755 "$DATABRIDGE_SECRETS_DIR"
chmod 644 "$DATABRIDGE_SECRETS_DIR"/app/*

docker compose up -d --build --wait || { docker compose logs --tail 60; exit 1; }

failed=()
passed=()
for name in $checks; do
  echo
  echo "=== $name"
  # A fresh app resets the in-memory limit on guest entries, which the checks share.
  docker compose restart app >/dev/null 2>&1
  docker compose up -d --no-deps --wait app >/dev/null 2>&1
  if [ "$name" = "ai_eval_mock" ]; then
    # Starts its own stack, because it needs an empty Customers table.
    (unset COMPOSE_FILE COMPOSE_PROJECT_NAME DATABRIDGE_SECRETS_DIR DATABRIDGE_PORT; EVAL_PORT="${EVAL_PORT:-8091}" checks/run_ai_eval.sh mock)
  elif [ "$name" = "review_browser" ] || [ "$name" = "theme_browser" ]; then
    "$python_bin" "checks/$name.py"
  else
    python3 "checks/$name.py"
  fi
  if [ $? -eq 0 ]; then passed+=("$name"); else failed+=("$name"); fi
done

echo
echo "=== Regression summary"
echo "Passed: ${passed[*]:-none}"
echo "Failed: ${failed[*]:-none}"
[ ${#failed[@]} -eq 0 ]
