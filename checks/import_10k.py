"""10,000-row import through the API, with synthetic data. Run from the repo root against a
running Compose stack. Rows: 9,700 new, 100 exact duplicates, 100 conflicts, 100 invalid."""
import json, sys, time, uuid
from support import approval, signed_in_admin, sql, sql_value

NEW, DUPLICATE, CONFLICT, INVALID = 9700, 100, 100, 100
TOTAL = NEW + DUPLICATE + CONFLICT + INVALID
run = uuid.uuid4().hex[:8]
HEADER = "Full Name,Organization,Email Address,Telephone,City,Account State,Comments"
stages = []


def stage(name, seconds, **counts):
    seconds = round(seconds, 2)
    stages.append({"stage": name, "seconds": seconds, **counts})
    print(f"{name:<26} {seconds:>8.2f}s  {counts}", flush=True)


def counts(batch_id):
    rows = sql(f"""SELECT COUNT(*), SUM(CASE WHEN MatchResult='New' THEN 1 ELSE 0 END),
        SUM(CASE WHEN MatchResult='Duplicate' THEN 1 ELSE 0 END), SUM(CASE WHEN MatchResult='Conflict' THEN 1 ELSE 0 END),
        SUM(CASE WHEN x.IssueCount>0 THEN 1 ELSE 0 END),
        SUM(CASE WHEN ReviewDecision='Approved' THEN 1 ELSE 0 END), SUM(CASE WHEN ReviewDecision='Rejected' THEN 1 ELSE 0 END)
        FROM ImportRows r OUTER APPLY (SELECT COUNT(*) IssueCount FROM ImportIssues i WHERE i.ImportRowId=r.Id) x
        WHERE ImportBatchId={batch_id}""")[0]
    return dict(zip(["staged", "new", "duplicate", "conflict", "withIssues", "approved", "rejected"], [int(v) for v in rows]))


# Existing customers, so the file can contain real duplicates and conflicts.
values = ",".join(
    f"('Existing {run} {i}','Old Co','{run}-existing-{i}@example.test','{run}-existing-{i}@example.test','Queens','Active','Keep this note')"
    for i in range(DUPLICATE + CONFLICT))
sql(f"INSERT INTO Customers(Name,Company,Email,NormalizedEmail,Location,Status,Notes) VALUES {values}")
customers_before = int(sql_value("SELECT COUNT(*) FROM Customers"))

lines = [HEADER]
lines += [f"New {run} {i},Globex,{run}-new-{i}@example.test,555-0100,Albany,Active,\"Note, {i}\"" for i in range(NEW)]
lines += [f"Existing {run} {i},Old Co,{run}-existing-{i}@example.test,,Queens,Active,Keep this note" for i in range(DUPLICATE)]
lines += [f"Existing {run} {i},New Co,{run}-existing-{i}@example.test,,Brooklyn,Active," for i in range(DUPLICATE, DUPLICATE + CONFLICT)]
lines += [f",Globex,not-an-email-{i},abc,Albany,Actve,Bad row" for i in range(INVALID)]
content = ("\n".join(lines) + "\n").encode()
print(f"File: {TOTAL} rows, {len(content)} bytes. Customers before: {customers_before}", flush=True)

client = signed_in_admin()

started = time.monotonic()
batch_id = client.upload(f"synthetic-{run}.csv", content)
stage("Upload accepted (202)", time.monotonic() - started, batchId=batch_id)

started = time.monotonic()
batch = client.wait_for_job(batch_id)
elapsed = time.monotonic() - started
assert batch["job"]["stage"] == "Completed", batch["job"]
stage("Parse and stage (job)", elapsed, reportedRows=batch["rowCount"], staged=counts(batch_id)["staged"])

started = time.monotonic()
status, _ = client.put(f"/api/imports/{batch_id}/mapping", {
    "nameColumn": 0, "companyColumn": 1, "emailColumn": 2, "phoneColumn": 3, "locationColumn": 4, "statusColumn": 5, "notesColumn": 6})
elapsed = time.monotonic() - started
assert status == 200, status
stage("Save mapping", elapsed, status=status)

started = time.monotonic()
status, _ = client.post(f"/api/imports/{batch_id}/validate")
assert status == 202, status
batch = client.wait_for_job(batch_id)
elapsed = time.monotonic() - started
assert batch["status"] == "ReadyForReview", batch
stage("Validate (job)", elapsed, **counts(batch_id))

started = time.monotonic()
rows = client.rows(batch_id)
stage("Read review preview", time.monotonic() - started, rowsRead=len(rows))

started = time.monotonic()
approved = rejected = 0
for row in rows:
    if row["issues"] or row["matchResult"] == "Duplicate":
        request = {"decision": "Rejected", "reason": "Synthetic review"}
        rejected += 1
    else:
        request = approval(row)
        approved += 1
    status, reply = client.put(f"/api/imports/{batch_id}/rows/{row['id']}/decision", request)
    assert status == 200, (status, reply)
elapsed = time.monotonic() - started
stage("Review all rows", elapsed, requests=len(rows), **{k: v for k, v in counts(batch_id).items() if k in ("approved", "rejected")})

started = time.monotonic()
status, summary = client.post(f"/api/imports/{batch_id}/commit")
elapsed = time.monotonic() - started
assert status == 200, (status, summary)
stage("Commit", elapsed, created=summary["customersCreated"], updated=summary["customersUpdated"], rejected=summary["rowsRejected"])

total_seconds = round(sum(item["seconds"] for item in stages), 2)
customers_after = int(sql_value("SELECT COUNT(*) FROM Customers"))
final = counts(batch_id)
duplicates = int(sql_value(f"SELECT COUNT(*) FROM (SELECT SourceRowNumber FROM ImportRows WHERE ImportBatchId={batch_id} GROUP BY SourceRowNumber HAVING COUNT(*)>1) d"))
preserved = int(sql_value(f"SELECT COUNT(*) FROM Customers WHERE Email LIKE '{run}-existing-%' AND Company='New Co' AND Notes='Keep this note'"))
audit = int(sql_value(f"SELECT COUNT(*) FROM ImportAuditEntries WHERE ImportBatchId={batch_id}"))

checks = {
    "staged": final["staged"] == TOTAL and duplicates == 0,
    "validated": (final["new"], final["duplicate"], final["conflict"], final["withIssues"]) == (NEW, DUPLICATE, CONFLICT, INVALID),
    "reviewed": (final["approved"], final["rejected"]) == (NEW + CONFLICT, DUPLICATE + INVALID),
    "committed": (summary["customersCreated"], summary["customersUpdated"], summary["rowsRejected"]) == (NEW, CONFLICT, DUPLICATE + INVALID),
    "customers": customers_after - customers_before == NEW,
    "notes preserved on update": preserved == CONFLICT,
}
print(f"\nTotal {total_seconds}s. Customers {customers_before} -> {customers_after}. Audit entries {audit}. Duplicate source rows {duplicates}.")
for name, passed in checks.items():
    print(("PASS " if passed else "FAIL ") + name)
print(json.dumps({"stages": stages, "totalSeconds": total_seconds, "final": final}))
sys.exit(0 if all(checks.values()) else 1)
