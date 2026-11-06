"""10 recovery scenarios against a running Compose stack, with synthetic data. Run from the
repo root. It stops and restarts the app and SQL Server containers of that stack."""
import os, subprocess, sys, threading, time, traceback, uuid
from support import (approval, compose, make_csv, make_xlsx, signed_in_admin, sql, sql_value, wait_until_healthy)

MAP3 = {"nameColumn": 0, "emailColumn": 1, "statusColumn": 2}
results = []


def scenario(name):
    def wrap(function):
        def run():
            started = time.monotonic()
            print(f"\n=== {name}", flush=True)
            try:
                evidence = function()
                results.append((name, True, evidence, time.monotonic() - started))
                print(f"PASS  {evidence}", flush=True)
            except Exception as error:
                results.append((name, False, repr(error)[:300], time.monotonic() - started))
                print(f"FAIL  {repr(error)[:300]}", flush=True)
                traceback.print_exc()
        return run
    return wrap


def rows_saved(batch_id):
    return int(sql_value(f"SELECT COUNT(*) FROM ImportRows WHERE ImportBatchId={batch_id}"))


def duplicate_rows(batch_id):
    return int(sql_value(f"SELECT COUNT(*) FROM (SELECT SourceRowNumber FROM ImportRows WHERE ImportBatchId={batch_id} GROUP BY SourceRowNumber HAVING COUNT(*)>1) d"))


def wait_for_rows(batch_id, minimum, timeout=180):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if rows_saved(batch_id) >= minimum:
            return
        time.sleep(0.3)
    raise TimeoutError(f"Fewer than {minimum} rows saved")


def check_import(client, batch_id, expected):
    batch = client.wait_for_job(batch_id, timeout=400)
    assert batch["job"]["stage"] == "Completed" and batch["status"] == "Uploaded", batch["job"]
    assert batch["rowCount"] == expected, batch["rowCount"]
    assert rows_saved(batch_id) == expected
    assert duplicate_rows(batch_id) == 0
    return batch


def interrupted(file_name, content, expected):
    client = signed_in_admin()
    batch_id = client.upload(file_name, content)
    wait_for_rows(batch_id, 3000)
    compose("kill", "app")
    at_kill = rows_saved(batch_id)
    assert 0 < at_kill < expected, at_kill
    stage = sql_value(f"SELECT Stage FROM ImportBatchJobs WHERE ImportBatchId={batch_id}")
    restart = time.monotonic()
    compose("up", "-d", "--no-deps", "app")
    wait_until_healthy()
    check_import(client, batch_id, expected)
    return f"killed at {at_kill}/{expected} rows in stage {stage}; finished {time.monotonic() - restart:.0f}s after restart with {expected} rows, 0 duplicates"


def prepared_batch(client, content, file_name):
    batch_id = client.upload(file_name, content)
    batch = client.wait_for_job(batch_id)
    assert batch["job"]["stage"] == "Completed", batch["job"]
    return batch_id


@scenario("1. CSV job interrupted")
def interrupted_csv():
    return interrupted("recovery-csv.csv", make_csv(f"csv{uuid.uuid4().hex[:6]}", 30000), 30000)


@scenario("2. Excel job interrupted")
def interrupted_excel():
    return interrupted("recovery-xlsx.xlsx", make_xlsx(f"xl{uuid.uuid4().hex[:6]}", 30000), 30000)


@scenario("3. Saved work that wasn't queued")
def saved_not_queued():
    client = signed_in_admin()
    source = prepared_batch(client, make_csv(f"q{uuid.uuid4().hex[:6]}", 50), "source.csv")
    compose("stop", "app")
    sql(f"""INSERT INTO ImportBatches(FileName,OriginalHeadersJson,Status,UploadedAtUtc) VALUES('not-queued.csv','[]','Pending',SYSUTCDATETIME());
        DECLARE @id int=SCOPE_IDENTITY();
        INSERT INTO ImportBatchFiles(ImportBatchId,Content) SELECT @id,Content FROM ImportBatchFiles WHERE ImportBatchId={source};
        INSERT INTO ImportBatchJobs(ImportBatchId,Kind,Stage,RunNumber,UpdatedAtUtc) VALUES(@id,'Parse','Queued',0,SYSUTCDATETIME())""")
    batch_id = int(sql_value("SELECT MAX(Id) FROM ImportBatches"))
    jobs = int(sql_value(f"SELECT COUNT(*) FROM HangFire.Job WHERE Arguments LIKE '[[]\"{batch_id}\"%'"))
    assert jobs == 0, jobs
    compose("up", "-d", "--no-deps", "app")
    wait_until_healthy()
    client = signed_in_admin()
    check_import(client, batch_id, 50)
    return f"batch {batch_id} had 0 Hangfire jobs while the app was down; recovery queued it, and it finished with 50 rows"


@scenario("4. Duplicate job delivery")
def duplicate_delivery():
    client = signed_in_admin()
    batch_id = client.upload("duplicate-delivery.csv", make_csv(f"d{uuid.uuid4().hex[:6]}", 20000))
    wait_for_rows(batch_id, 2000)
    job_id = sql_value(f"SELECT TOP 1 Id FROM HangFire.Job WHERE Arguments LIKE '[[]\"{batch_id}\"%' ORDER BY Id")
    # A second copy of the same job while the first is still running.
    sql(f"INSERT INTO HangFire.JobQueue(JobId,Queue) VALUES({job_id},'default')")
    check_import(client, batch_id, 20000)
    # And a replay of the finished work from the stored file.
    sql(f"UPDATE ImportBatchJobs SET Stage='Queued',EnqueuedAtUtc=NULL WHERE ImportBatchId={batch_id}")
    check_import(client, batch_id, 20000)
    follow_ups = int(sql_value(f"SELECT COUNT(*) FROM HangFire.Job WHERE Arguments LIKE '[[]\"{batch_id}\"%'"))
    return f"second delivery during the job, then a replay after it: 20000 rows, 0 duplicates, {follow_ups} Hangfire jobs for the batch"


@scenario("5. Temporary database failure")
def temporary_database_failure():
    client = signed_in_admin()
    batch_id = client.upload("database-failure.csv", make_csv(f"t{uuid.uuid4().hex[:6]}", 30000))
    wait_for_rows(batch_id, 3000)
    before_kill = rows_saved(batch_id)
    compose("kill", "sqlserver")
    down_at = time.monotonic()
    time.sleep(15)
    compose("up", "-d", "--no-deps", "--wait", "sqlserver")
    wait_until_healthy()
    client = signed_in_admin()
    check_import(client, batch_id, 30000)
    # Proves the job really hit the outage, and didn't just finish before it.
    failed_attempts = compose("logs", "app").stdout.count(f"Import job for batch {batch_id} failed.")
    hangfire_failures = int(sql_value(f"SELECT COUNT(*) FROM HangFire.[State] s JOIN HangFire.Job j ON j.Id=s.JobId WHERE j.Arguments LIKE '[[]\"{batch_id}\"%' AND s.Name='Failed'"))
    assert failed_attempts >= 1 and hangfire_failures >= 1, f"the job never saw the outage: {failed_attempts} logged, {hangfire_failures} in Hangfire"
    state = compose("ps", "app", "--format", "{{.State}}").stdout.strip()
    return (f"SQL Server killed with {before_kill}/30000 rows saved and down for 15s; the job failed {failed_attempts} time(s) and Hangfire retried it, "
            f"then finished {time.monotonic() - down_at:.0f}s after the kill with 30000 rows, 0 duplicates; app {state}")


@scenario("6. Permanently invalid file")
def permanent_failure():
    client = signed_in_admin()
    batch_id = client.upload("broken.xlsx", b"this is not a workbook")
    batch = client.wait_for_job(batch_id)
    assert batch["job"]["stage"] == "Failed" and "can't be read as an Excel workbook" in batch["job"]["error"], batch["job"]
    time.sleep(12)  # Longer than the first retry delay.
    state = sql_value(f"SELECT TOP 1 StateName FROM HangFire.Job WHERE Arguments LIKE '[[]\"{batch_id}\"%' ORDER BY Id")
    retries = int(sql_value(f"SELECT COUNT(*) FROM HangFire.[State] s JOIN HangFire.Job j ON j.Id=s.JobId WHERE j.Arguments LIKE '[[]\"{batch_id}\"%' AND s.Name='Scheduled'"))
    assert retries == 0 and rows_saved(batch_id) == 0
    status, _ = client.put(f"/api/imports/{batch_id}/mapping", MAP3)
    assert status == 409, status
    return f"job Failed with a clear error, 0 retries, 0 rows, Hangfire {state}, mapping returns 409"


@scenario("7. Newer mapping during validation")
def newer_mapping():
    client = signed_in_admin()
    prefix = f"m{uuid.uuid4().hex[:6]}"
    batch_id = prepared_batch(client, make_csv(prefix, 500), "newer-mapping.csv")
    status, _ = client.put(f"/api/imports/{batch_id}/mapping", MAP3)
    assert status == 200, status
    # Both saves add an audit entry, whose foreign key needs the Customers table. Holding the
    # table makes the job's save and the new mapping's save overlap, and either can go first.
    holder = threading.Thread(target=lambda: sql("BEGIN TRAN; SELECT TOP 0 Id FROM Customers WITH (TABLOCKX,HOLDLOCK); WAITFOR DELAY '00:00:20'; COMMIT"))
    holder.start()
    deadline = time.monotonic() + 15
    while not int(sql_value("""SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type='OBJECT' AND request_mode='X'
            AND request_status='GRANT' AND resource_associated_entity_id=OBJECT_ID('Customers')""")):
        assert time.monotonic() < deadline, "the table lock never started"
        time.sleep(0.2)
    status, _ = client.post(f"/api/imports/{batch_id}/validate")
    assert status == 202, status
    deadline = time.monotonic() + 10
    while client.batch(batch_id)["job"]["stage"] == "Queued" and time.monotonic() < deadline:
        time.sleep(0.2)
    stage_during = client.batch(batch_id)["job"]["stage"]
    new_mapping = {"nameColumn": 2, "emailColumn": 1, "statusColumn": 0}
    status, _ = client.put(f"/api/imports/{batch_id}/mapping", {"nameColumn": 1, "emailColumn": 1, "statusColumn": 2})
    rejected_duplicate_column = status
    assert rejected_duplicate_column == 400, rejected_duplicate_column
    status, _ = client.put(f"/api/imports/{batch_id}/mapping", new_mapping)
    assert status in (200, 409), status
    holder.join()
    time.sleep(5)

    def state():
        matched = int(sql_value(f"SELECT COUNT(*) FROM ImportRows WHERE ImportBatchId={batch_id} AND MatchResult IS NOT NULL"))
        validated = int(sql_value(f"SELECT COUNT(*) FROM ImportAuditEntries WHERE ImportBatchId={batch_id} AND Action='BatchValidated'"))
        draft = sql_value(f"SELECT TOP 1 DraftName FROM ImportRows WHERE ImportBatchId={batch_id} ORDER BY SourceRowNumber")
        return client.batch(batch_id), matched, validated, draft

    if status == 200:
        # The mapping saved first, so the job's results for the old mapping were discarded.
        outcome = "mapping saved first: old results discarded"
        batch, matched, validated, draft = state()
        assert validated == 0, validated
    else:
        # The job saved first and the mapping was refused, so the results match the old mapping.
        outcome = "job saved first: the mapping got 409 with the results still matching the old mapping, then saving it again replaced them"
        batch, matched, validated, draft = state()
        assert batch["status"] == "ReadyForReview" and matched == 500 and validated == 1, (batch["status"], matched, validated)
        assert draft.startswith("Person"), draft
        status, _ = client.put(f"/api/imports/{batch_id}/mapping", new_mapping)
        assert status == 200, status
        batch, matched, validated, draft = state()

    issues = int(sql_value(f"SELECT COUNT(*) FROM ImportIssues i JOIN ImportRows r ON r.Id=i.ImportRowId WHERE r.ImportBatchId={batch_id}"))
    assert batch["status"] == "Uploaded" and batch["job"]["stage"] == "Completed", batch
    assert matched == 0 and issues == 0 and draft == "Active", (matched, issues, draft)
    return f"mapping sent during {stage_during}; {outcome}; 0 matches, 0 issues, drafts use the new mapping"


def ready_for_review(client, content, name):
    batch_id = prepared_batch(client, content, name)
    status, _ = client.put(f"/api/imports/{batch_id}/mapping", MAP3)
    assert status == 200, status
    status, _ = client.post(f"/api/imports/{batch_id}/validate")
    assert status == 202, status
    batch = client.wait_for_job(batch_id)
    assert batch["status"] == "ReadyForReview", batch
    return batch_id


def approve_all(client, batch_id):
    for row in client.rows(batch_id):
        status, reply = client.put(f"/api/imports/{batch_id}/rows/{row['id']}/decision", approval(row))
        assert status == 200, (status, reply)


@scenario("8. Commit rollback")
def commit_rollback():
    client = signed_in_admin()
    prefix = f"r{uuid.uuid4().hex[:6]}"
    batch_id = ready_for_review(client, make_csv(prefix, 20), "rollback.csv")
    approve_all(client, batch_id)
    customers = sql_value("SELECT COUNT(*) FROM Customers")
    audit = sql_value(f"SELECT COUNT(*) FROM ImportAuditEntries WHERE ImportBatchId={batch_id}")
    sql("EXEC('CREATE TRIGGER FailAudit ON ImportAuditEntries AFTER INSERT AS THROW 50000, ''forced failure'', 1;')")
    try:
        status, _ = client.post(f"/api/imports/{batch_id}/commit")
    finally:
        sql("DROP TRIGGER FailAudit")
    assert status == 500, status
    assert sql_value("SELECT COUNT(*) FROM Customers") == customers
    assert sql_value(f"SELECT COUNT(*) FROM ImportAuditEntries WHERE ImportBatchId={batch_id}") == audit
    assert client.batch(batch_id)["status"] == "ReadyForReview"
    status, summary = client.post(f"/api/imports/{batch_id}/commit")
    assert status == 200 and summary["customersCreated"] == 20, (status, summary)
    return "audit insert forced to fail: 500, no customers or audit entries saved, still in review; the retry committed 20"


@scenario("9. Repeated and concurrent commit")
def repeated_concurrent_commit():
    client = signed_in_admin()
    batch_id = ready_for_review(client, make_csv(f"c{uuid.uuid4().hex[:6]}", 20), "concurrent.csv")
    approve_all(client, batch_id)
    customers = int(sql_value("SELECT COUNT(*) FROM Customers"))
    replies = []
    # Signed in first, so the commits all start together.
    workers = [signed_in_admin() for _ in range(8)]
    start = threading.Barrier(len(workers))

    def commit(worker):
        start.wait()
        replies.append(worker.post(f"/api/imports/{batch_id}/commit"))

    threads = [threading.Thread(target=commit, args=(worker,)) for worker in workers]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    repeat = client.post(f"/api/imports/{batch_id}/commit")
    statuses = sorted(reply[0] for reply in replies)
    assert all(status == 200 for status in statuses), statuses
    assert all(reply[1] == repeat[1] for reply in replies), replies
    added = int(sql_value("SELECT COUNT(*) FROM Customers")) - customers
    commits = int(sql_value(f"SELECT COUNT(*) FROM ImportAuditEntries WHERE ImportBatchId={batch_id} AND Action='BatchCommitted'"))
    assert added == 20 and commits == 1, (added, commits)
    return f"8 at once plus 1 repeat: all 200 with the same summary, {added} customers added, {commits} BatchCommitted entry"


@scenario("10. Customer changes after approval")
def customer_changed():
    client = signed_in_admin()
    email = f"changed-{uuid.uuid4().hex[:6]}@example.test"
    sql(f"INSERT INTO Customers(Name,Email,NormalizedEmail,Status,Notes) VALUES('Original Name','{email}','{email}','Active','Keep')")
    batch_id = ready_for_review(client, f"Name,Email,Status\nImported Name,{email},Active\n".encode(), "changed.csv")
    approve_all(client, batch_id)
    sql(f"UPDATE Customers SET Notes='Changed elsewhere' WHERE NormalizedEmail='{email}'")
    status, reply = client.post(f"/api/imports/{batch_id}/commit")
    assert status == 409 and "out of date" in reply["detail"], (status, reply)
    assert sql_value(f"SELECT Name FROM Customers WHERE NormalizedEmail='{email}'") == "Original Name"
    status, _ = client.post(f"/api/imports/{batch_id}/validate")
    assert status == 202, status
    client.wait_for_job(batch_id)
    decision = sql_value(f"SELECT ReviewDecision FROM ImportRows WHERE ImportBatchId={batch_id}")
    assert decision == "Pending", decision
    approve_all(client, batch_id)
    status, summary = client.post(f"/api/imports/{batch_id}/commit")
    assert status == 200 and summary["customersUpdated"] == 1, (status, summary)
    final = sql(f"SELECT Name,Notes FROM Customers WHERE NormalizedEmail='{email}'")[0]
    assert final == ["Imported Name", "Changed elsewhere"], final
    return "commit returned 409 with nothing saved; validating cleared the approval; re-approval committed and kept the newer note"


# RECOVERY_SCENARIOS="7 9" runs only those scenarios.
selected = os.environ.get("RECOVERY_SCENARIOS", "").split()
for number, run in enumerate([interrupted_csv, interrupted_excel, saved_not_queued, duplicate_delivery, temporary_database_failure,
                              permanent_failure, newer_mapping, commit_rollback, repeated_concurrent_commit, customer_changed], 1):
    if not selected or str(number) in selected:
        run()

print("\n=== Summary")
for name, passed, evidence, seconds in results:
    print(f"{'PASS' if passed else 'FAIL'} {seconds:6.1f}s  {name}: {evidence}")
passed = sum(1 for result in results if result[1])
print(f"{passed} of {len(results)} passed")
sys.exit(0 if passed == len(results) else 1)
