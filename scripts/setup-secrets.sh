#!/usr/bin/env bash
# Creates the local secrets folder that compose.yaml reads. Git and Docker builds ignore it.
# Safe to run again: an existing SQL Server password is kept.
set -euo pipefail

secrets_dir="${DATABRIDGE_SECRETS_DIR:-./secrets}"
mkdir -p "$secrets_dir/app"
chmod 755 "$secrets_dir" "$secrets_dir/app"

if [ ! -f "$secrets_dir/sql_sa_password" ]; then
  printf '%s' "Db$(openssl rand -hex 20)Aa1!" > "$secrets_dir/sql_sa_password"
  echo "Created $secrets_dir/sql_sa_password"
else
  echo "Kept the existing $secrets_dir/sql_sa_password"
fi

# Only compose.production.yaml uses these two: one login runs migrations, the other runs the app.
for name in sql_migrator_password sql_app_password; do
  if [ ! -f "$secrets_dir/$name" ]; then
    printf '%s' "Db$(openssl rand -hex 20)Aa1!" > "$secrets_dir/$name"
    echo "Created $secrets_dir/$name"
  fi
done

# The SQL Server and app containers run as their own users, so they need read access.
chmod 644 "$secrets_dir/sql_sa_password" "$secrets_dir/sql_migrator_password" "$secrets_dir/sql_app_password"
