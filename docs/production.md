# Production setup

`compose.production.yaml` is an overlay on `compose.yaml` for a public deployment. The local workflow is unchanged: `docker compose up` without the overlay works as before.

| | `compose.yaml` | With `compose.production.yaml` |
|---|---|---|
| App's SQL login | `sa` | `databridge_app`: reads and writes rows only |
| Migrations' SQL login | `sa` | `databridge_migrator`: `db_owner` of this database only |
| `sa` password in the app container | Yes | No |
| Hangfire schema installed by | `migrate` and, if missing, the app | `migrate` only |
| Public entry point | App on `127.0.0.1:8088`, plain HTTP | Caddy on ports 80 and 443, HTTPS |
| App's own port | Published to localhost | Not published |

## Start it

```bash
./scripts/setup-secrets.sh    # Creates the SQL passwords, and keeps any that already exist
printf '%s' 'you@example.com' > secrets/app/Seed__Users__0__Email
printf '%s' 'Admin' > secrets/app/Seed__Users__0__Role
read -rsp 'Admin password: ' p; printf '%s' "$p" > secrets/app/Seed__Users__0__Password; unset p; echo

export DATABRIDGE_DOMAIN=databridge.example.com
docker compose -f compose.yaml -f compose.production.yaml up -d --build --wait
```

The startup order is:

1. `sqlserver` starts and passes its health check.
2. `db-init` connects as `sa`, creates the database and both logins, then exits. It's safe to run again, and it resets the logins' passwords to the current secret files.
3. `migrate` connects as `databridge_migrator`, applies the EF Core migrations, installs the Hangfire schema, then exits.
4. `app` connects as `databridge_app`, with `Hangfire__PrepareSchema` set to `false`.
5. `proxy` starts once the app reports ready.

To add these logins to an existing stack, use the same command. `db-init` leaves existing data alone.

## SQL identities

| Login | Permissions | Used by |
|---|---|---|
| `sa` | Server administrator | `sqlserver` health check and `db-init` |
| `databridge_migrator` | `db_owner` of `DataBridge`. It can't create databases or manage the server | `migrate` |
| `databridge_app` | `db_datareader` and `db_datawriter`. It can't change the schema, users, or roles | `app` |

The logins are created by `scripts/sql/create-production-logins.sql`.

EF Core's reads and writes, Hangfire's queue and housekeeping, and the batch lock (`sp_getapplock`) all work with the runtime login's permissions. `checks/production_setup.py` verifies this.

## HTTPS

Caddy is the only public entry point, configured in `deploy/Caddyfile`.

- **A real domain:** set `DATABRIDGE_DOMAIN` and point its DNS at the server. Caddy gets and renews a certificate automatically, and needs ports 80 and 443 open. It redirects HTTP to HTTPS.
- **Locally:** the default domain is `localhost`, which uses Caddy's own test certificate. Browsers warn about it unless you trust Caddy's local CA.
- **Ports and interface:** `DATABRIDGE_HTTP_PORT`, `DATABRIDGE_HTTPS_PORT`, and `DATABRIDGE_PROXY_BIND` change them. They default to `80`, `443`, and `0.0.0.0`.

The app sends an HSTS header on HTTPS responses, with ASP.NET Core's 30-day default. It leaves this out for `localhost`. Once HTTPS is working on your domain, you may want a longer value.

## Trusted proxy

Behind a proxy, the app sees the proxy's address, not the client's. It needs `X-Forwarded-For` for the per-address limit on guest entry, and `X-Forwarded-Proto` to mark sign-in cookies `Secure`.

A client could fake those headers, so the app only believes them from addresses listed in `ForwardedHeaders__KnownProxies__N`:

- The overlay gives Caddy the fixed address `172.30.57.10`, on a fixed subnet, and lists it as the one trusted proxy.
- Caddy replaces any `X-Forwarded-*` headers that a client sends.
- If nothing is listed, as in `compose.yaml`, the app ignores these headers.

**Using a different proxy or load balancer:**

1. Set `ForwardedHeaders__KnownProxies__0` to the address the app sees it connect from. Each entry must be a single IP address.
2. Make sure the proxy overwrites client-supplied `X-Forwarded-For` and `X-Forwarded-Proto`, rather than appending to them.
3. Keep the app's port unreachable except from the proxy. Anyone else who can reach it would appear as the proxy's network with no forwarded address.
4. Allow WebSocket upgrades, as Blazor Interactive Server uses them.

Keep `Api__BaseUrl` as `http://localhost:8080`. Blazor pages call the app's own API on this address inside the container, not through the proxy.

## Health checks

| Endpoint | Meaning | Use it for |
|---|---|---|
| `GET /api/health/live` | The app process can answer. It doesn't touch SQL Server | Restarting a stuck process |
| `GET /api/health/ready` | SQL Server answered within about 3 seconds. Otherwise `503` | Routing traffic, and Compose's health check |

An app that fails readiness during a database outage should be left running. It recovers without a restart when SQL Server returns.

## Optional settings

**Raw file cleanup.** Uploaded files are kept by default. To remove the files of finished member imports after a number of days:

```bash
export IMPORT_RAW_FILE_RETENTION_ENABLED=true
export IMPORT_RAW_FILE_RETENTION_DAYS=90
```

It runs hourly and at startup. It only removes files from committed member batches with no active job, and records `RawFileDeleted` in the batch's audit history. Files for batches still in progress are kept because jobs replay from them. Guest files are removed with their workspace.

**AI call cap.** `OPENAI_MAX_CALLS_PER_DAY` limits paid AI calls per UTC day across all workspaces, and defaults to 1000. It's counted in the database, so it holds across concurrent requests and restarts. `0` turns AI calls off. Requests over the cap get `429`, and manual filters still work. Each guest's limit of 5 searches still applies as well.

## Before going public

The checks below test this setup locally, using Caddy's local certificate. These haven't been tested:

- Issuing a real certificate for a public domain.
- Running behind a proxy other than Caddy.

This overlay also doesn't provide database backups, off-server log storage, or an IP limit on sign-in, so plan for those separately. Sign-in has account lockout but no per-address limit.

## Test it locally

```bash
PYTHON=<python with playwright> checks/run_production_check.sh
```

This starts a throwaway stack with its own secrets on `https://localhost:8443`, runs `checks/production_setup.py`, the two-account stale approval check, and the review browser check through the proxy, then removes the stack.
