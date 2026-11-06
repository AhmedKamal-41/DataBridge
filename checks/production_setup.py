"""The production overlay: restricted logins, Hangfire and EF Core with them, and the HTTPS proxy.

Run it through checks/run_production_check.sh, which starts an isolated stack."""
import time
import urllib.error
import urllib.request

from support import (BASE_URL, Client, TLS_CONTEXT, approval, audit_entries, compose, new_guest, ready_batch, sql, sql_value,
                     wait_until_healthy)


def as_login(login, password_file, query):
    """Runs a query as a restricted login, from the one-shot container that has the passwords."""
    script = (f'SQLCMDPASSWORD="$(cat /run/secrets/{password_file})" /opt/mssql-tools18/bin/sqlcmd -C -S sqlserver '
              f'-U {login} -d DataBridge -b -W -h -1 -Q "$1"')
    return compose("run", "--rm", "--no-deps", "-T", "db-init", "bash", "-c", script, "_", "SET NOCOUNT ON; " + query, check=False)


def check_logins():
    logins = {row[0]: row[1] for row in sql(
        "SELECT name, IS_SRVROLEMEMBER('sysadmin', name) FROM sys.server_principals WHERE name IN ('databridge_app','databridge_migrator')")}
    assert logins == {"databridge_app": "0", "databridge_migrator": "0"}, logins
    roles = sorted(row[0] for row in sql("""SELECT r.name FROM sys.database_role_members m
        JOIN sys.database_principals r ON r.principal_id=m.role_principal_id
        JOIN sys.database_principals u ON u.principal_id=m.member_principal_id WHERE u.name='databridge_app'"""))
    assert roles == ["db_datareader", "db_datawriter"], roles
    print("PASS neither login is sysadmin, and the runtime login only has db_datareader and db_datawriter")

    for attempt in ("CREATE TABLE dbo.NotAllowed(Id int)", "DROP TABLE dbo.Customers", "ALTER TABLE dbo.Customers ADD NotAllowed int",
                    "CREATE USER NotAllowed WITHOUT LOGIN", "ALTER ROLE db_owner ADD MEMBER databridge_app", "DROP TABLE HangFire.Job"):
        result = as_login("databridge_app", "sql_app_password", attempt)
        assert result.returncode != 0, f"the runtime login could run: {attempt}"
    assert as_login("databridge_app", "sql_app_password", "SELECT COUNT(*) FROM dbo.Customers").returncode == 0
    assert as_login("databridge_app", "sql_app_password", "SELECT COUNT(*) FROM HangFire.Job").returncode == 0
    print("PASS the runtime login can read data but can't change the schema, users, or roles")

    result = as_login("databridge_migrator", "sql_migrator_password", "CREATE TABLE dbo.MigratorCheck(Id int); DROP TABLE dbo.MigratorCheck")
    assert result.returncode == 0, result.stdout + result.stderr
    result = as_login("databridge_migrator", "sql_migrator_password", "CREATE DATABASE NotAllowed")
    assert result.returncode != 0, "the migration login shouldn't manage the server"
    print("PASS the migration login can change this database's schema, but not create databases")


def check_app_identity():
    sessions = {row[0] for row in sql("""SELECT DISTINCT s.login_name FROM sys.dm_exec_sessions s
        WHERE s.is_user_process=1 AND s.database_id=DB_ID('DataBridge') AND s.session_id<>@@SPID""")}
    assert sessions == {"databridge_app"}, sessions
    assert compose("exec", "-T", "app", "test", "-e", "/run/secrets/sql_sa_password", check=False).returncode != 0
    assert compose("exec", "-T", "app", "test", "-e", "/run/secrets/sql_app_password", check=False).returncode == 0
    migrated = compose("logs", "migrate").stdout
    assert int(sql_value("SELECT COUNT(*) FROM __EFMigrationsHistory")) >= 6, migrated[-300:]
    assert int(sql_value("SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='HangFire'")) > 0
    published = compose("ps", "app", "--format", "{{.Publishers}}").stdout
    assert ":8080->" not in published.replace("0.0.0.0:0->", ""), published
    print("PASS the app connects as databridge_app, has no sa secret, isn't published, and the migrator installed EF Core and Hangfire")


def check_proxy():
    with urllib.request.urlopen(BASE_URL + "/", timeout=15, context=TLS_CONTEXT) as response:
        assert response.status == 200 and response.url.startswith("https://"), response.url

    guest = new_guest()
    identity = [cookie for cookie in guest.jar if cookie.name == ".AspNetCore.Identity.Application"]
    assert identity and identity[0].secure, "over the proxy the sign-in cookie should be Secure, which needs the forwarded scheme"
    print("PASS HTTPS works through the proxy, and the sign-in cookie is Secure because X-Forwarded-Proto is trusted")

    # Caddy replaces a client's X-Forwarded-For, so a client can't dodge the per-address limit.
    statuses = []
    faker = Client()
    faker.refresh_token()
    for number in range(20):
        request = urllib.request.Request(BASE_URL + "/account/guest-token", method="POST", headers={
            "X-CSRF-TOKEN": faker.token, "X-Forwarded-For": f"203.0.113.{number}"})
        try:
            with faker.opener.open(request, timeout=15) as response:
                statuses.append(response.status)
        except urllib.error.HTTPError as error:
            statuses.append(error.code)
    assert 429 in statuses, statuses
    print(f"PASS faked X-Forwarded-For headers don't get around the guest limit ({statuses.count(200)} allowed, then 429)")

    # A request that reaches the app directly isn't from the trusted proxy, so its headers are ignored.
    script = ("exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET /api/account/antiforgery HTTP/1.0\\r\\nX-Forwarded-Proto: https\\r\\n\\r\\n' >&3; "
              "grep -ci '^set-cookie:.*secure' <&3 || true")
    direct = compose("exec", "-T", "app", "bash", "-c", script).stdout.strip()
    assert direct == "0", direct
    print("PASS the app ignores X-Forwarded-Proto from anything but the trusted proxy")


def check_workflow_with_restricted_login():
    # Restarted first, as the previous step used up this address's guest entries.
    compose("restart", "app")
    wait_until_healthy()
    guest = new_guest()
    sample = open("wwwroot/sample-customers.csv", "rb").read()
    batch_id = ready_batch(guest, sample, "sample-customers.csv", {
        "nameColumn": 0, "companyColumn": 1, "emailColumn": 2, "phoneColumn": 3, "locationColumn": 4, "statusColumn": 5, "notesColumn": 6})
    for row in guest.rows(batch_id):
        request = approval(row) if row["matchResult"] in ("New", "Conflict") else {"decision": "Rejected", "reason": "Not needed"}
        status, reply = guest.put(f"/api/imports/{batch_id}/rows/{row['id']}/decision", request)
        assert status == 200, (status, reply)
    status, summary = guest.post(f"/api/imports/{batch_id}/commit")
    assert status == 200 and summary["customersUpdated"] == 1, (status, summary)
    assert "BatchCommitted" in [entry["action"] for entry in audit_entries(guest, batch_id)]
    print("PASS upload, Hangfire parse and validate jobs, the batch lock, review, and commit all work with the restricted login")

    # Hangfire's own housekeeping: recurring jobs registered and running with no schema rights.
    recurring = {row[0] for row in sql("SELECT Value FROM HangFire.[Set] WHERE [Key]='recurring-jobs'")}
    assert {"recover-import-jobs", "delete-expired-guest-workspaces", "delete-expired-import-files"} <= recurring, recurring
    deadline = time.monotonic() + 90
    while True:
        succeeded = int(sql_value("""SELECT COUNT(*) FROM HangFire.Job WHERE StateName='Succeeded'
            AND InvocationData LIKE '%RecoverPendingWorkAsync%'"""))
        if succeeded > 0:
            break
        assert time.monotonic() < deadline, "the recovery job never succeeded"
        time.sleep(2)
    logs = compose("logs", "app").stdout
    assert "permission was denied" not in logs.lower(), "the app log shows a permission error"
    print("PASS Hangfire's recurring jobs run with the restricted login, and the log has no permission errors")


def run():
    wait_until_healthy()
    check_logins()
    check_app_identity()
    check_proxy()
    check_workflow_with_restricted_login()


if __name__ == "__main__":
    run()
