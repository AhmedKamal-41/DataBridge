#!/usr/bin/env bash
# Runs the 30 AI reference questions in a throwaway Compose project with synthetic customers.
#   checks/run_ai_eval.sh mock
#   OPENAI_API_KEY=... [OPENAI_MODEL=...] checks/run_ai_eval.sh live
# Run it from the repo root. Your databridge stack and its data aren't touched.
set -euo pipefail

mode="${1:-}"
case "$mode" in
  mock) key="mock-key"; compose_files="compose.yaml:compose.mock.yaml" ;;
  live)
    key="${OPENAI_API_KEY:-}"
    if [ -z "$key" ]; then
      echo "Set OPENAI_API_KEY to run the live suite. It calls OpenAI 30 times, which costs money." >&2
      exit 2
    fi
    compose_files="compose.yaml"
    ;;
  *) echo "Usage: $0 mock|live" >&2; exit 2 ;;
esac

export COMPOSE_PROJECT_NAME="databridge-eval-$mode"
export COMPOSE_FILE="$compose_files"
export DATABRIDGE_SECRETS_DIR DATABRIDGE_PORT="${EVAL_PORT:-8089}"
DATABRIDGE_SECRETS_DIR="$(mktemp -d)"
export PYTHONDONTWRITEBYTECODE=1

cleanup() {
  docker compose down -v --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$DATABRIDGE_SECRETS_DIR"
}
trap cleanup EXIT

./scripts/setup-secrets.sh >/dev/null
printf '%s' 'eval-admin@example.test' > "$DATABRIDGE_SECRETS_DIR/app/Seed__Users__0__Email"
printf '%s' "Eval$(openssl rand -hex 16)Aa1!" > "$DATABRIDGE_SECRETS_DIR/app/Seed__Users__0__Password"
printf '%s' 'Admin' > "$DATABRIDGE_SECRETS_DIR/app/Seed__Users__0__Role"
printf '%s' "$key" > "$DATABRIDGE_SECRETS_DIR/app/OpenAI__ApiKey"
chmod 755 "$DATABRIDGE_SECRETS_DIR"
chmod 644 "$DATABRIDGE_SECRETS_DIR"/app/*

docker compose up -d --build --wait >/dev/null 2>&1
python3 checks/ai_eval.py "$mode"
