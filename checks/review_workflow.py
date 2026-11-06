"""Finishing with zero approved rows, bulk approval, and upload and mapping audit entries."""
import json
import uuid

from support import (approval, audit_entries, new_guest, ready_batch, signed_in_admin, sql, sql_value,
                     wait_until_healthy)

MAPPING = {"nameColumn": 0, "emailColumn": 1, "statusColumn": 2}


def people(prefix, count):
    return [f"{prefix} {i},{prefix}-{i}@example.test,Active" for i in range(count)]


def csv(lines):
    return ("Name,Email,Status\n" + "\n".join(lines) + "\n").encode()


def reject(client, batch_id, row):
    status, body = client.put(f"/api/imports/{batch_id}/rows/{row['id']}/decision",
                              {"decision": "Rejected", "reason": "Not needed", "rowVersion": row["rowVersion"]})
    assert status == 200, (status, body)


def check_finish_with_zero_approved(admin):
    prefix = f"z{uuid.uuid4().hex[:8]}"
    batch_id = ready_batch(admin, csv(people(prefix, 3)), "zero.csv", MAPPING)
    url = f"/api/imports/{batch_id}/commit"
    customers = sql_value("SELECT COUNT(*) FROM Customers")
    rows = admin.rows(batch_id)

    status, body = admin.post(url)
    assert status == 400 and "Every row needs a decision" in body["detail"], (status, body)
    reject(admin, batch_id, rows[0])
    reject(admin, batch_id, rows[1])
    status, body = admin.post(url)
    assert status == 400 and str(rows[2]["sourceRowNumber"]) in body["detail"], (status, body)
    assert admin.batch(batch_id)["status"] == "ReadyForReview"
    print("PASS pending rows still block finishing")

    reject(admin, batch_id, rows[2])
    status, summary = admin.post(url)
    assert status == 200, (status, summary)
    assert (summary["customersCreated"], summary["customersUpdated"], summary["rowsRejected"]) == (0, 0, 3), summary
    assert summary["status"] == "Committed", summary
    status, repeat = admin.post(url)
    assert status == 200 and repeat == summary, (status, repeat)
    assert sql_value("SELECT COUNT(*) FROM Customers") == customers
    assert [entry["action"] for entry in audit_entries(admin, batch_id)].count("BatchCommitted") == 1
    assert admin.batch(batch_id)["commitSummary"] == summary
    print("PASS a fully rejected batch finishes with 0 inserts, 0 updates, and 3 rejected, and repeating returns the same result")


def bulk(client, batch_id, rows):
    return client.post(f"/api/imports/{batch_id}/rows/bulk-approve",
                       {"rows": [{"rowId": row["id"], "rowVersion": row["rowVersion"]} for row in rows]})


def decisions(client, batch_id):
    return {row["id"]: row["reviewDecision"] for row in client.rows(batch_id)}


def check_bulk_approval(admin):
    prefix = f"b{uuid.uuid4().hex[:8]}"
    conflict_email = f"{prefix}-conflict@example.test"
    duplicate_email = f"{prefix}-same@example.test"
    sql(f"INSERT INTO Customers(Name,Email,NormalizedEmail,Status) VALUES('Old Name','{conflict_email}','{conflict_email}','Active'),"
        f"('Same Name','{duplicate_email}','{duplicate_email}','Active')")
    lines = people(prefix, 4) + [f"New Name,{conflict_email},Active", f"Same Name,{duplicate_email},Active", "Bad Row,not-an-email,Active"]
    batch_id = ready_batch(admin, csv(lines), "bulk.csv", MAPPING)
    url = f"/api/imports/{batch_id}"
    rows = admin.rows(batch_id)
    new, conflict, duplicate, invalid = rows[:4], rows[4], rows[5], rows[6]
    assert [row["matchResult"] for row in rows] == ["New"] * 4 + ["Conflict", "Duplicate", None], rows

    status, body = bulk(admin, batch_id, new[:2])
    assert status == 200 and body["approvedCount"] == 2, (status, body)
    entries = [entry for entry in audit_entries(admin, batch_id) if entry["action"] == "InsertApproved"]
    assert sorted(entry["importRowId"] for entry in entries) == sorted(row["id"] for row in new[:2]), entries
    assert all(entry["actorName"] and entry["timestampUtc"] for entry in entries), entries
    current = decisions(admin, batch_id)
    assert current[new[2]["id"]] == "Pending" and current[new[3]["id"]] == "Pending", current
    print("PASS only the selected New rows are approved, each with its own audit entry")

    # A selection with one bad row leaves every row in it unchanged, and names the bad row.
    fresh = {row["id"]: row for row in admin.rows(batch_id)}
    good = fresh[new[2]["id"]]
    audit_count = len(audit_entries(admin, batch_id))
    for bad, expected in ((fresh[conflict["id"]], "needs its own review"),
                          (fresh[duplicate["id"]], "needs its own review"),
                          (fresh[invalid["id"]], "validation issues"),
                          (fresh[new[0]["id"]], "already has a decision")):
        status, body = bulk(admin, batch_id, [good, bad])
        assert status == 409, (status, body)
        assert f"Source row {bad['sourceRowNumber']} " in body["detail"] and expected in body["detail"], body
        assert "Nothing was approved" in body["detail"], body
        assert decisions(admin, batch_id)[good["id"]] == "Pending"
    assert len(audit_entries(admin, batch_id)) == audit_count
    print("PASS a conflict, duplicate, invalid, or decided row in a selection leaves the whole selection unchanged")

    # A row that changed after the preview.
    stale = fresh[new[3]["id"]]
    status, body = admin.put(f"{url}/rows/{stale['id']}", {"draftName": "Corrected Name", "draftEmail": stale["draftEmail"], "draftStatus": "Active"})
    assert status == 200, (status, body)
    audit_count = len(audit_entries(admin, batch_id))
    status, body = bulk(admin, batch_id, [good, stale])
    assert status == 409 and f"Source row {stale['sourceRowNumber']} changed" in body["detail"], (status, body)
    current = decisions(admin, batch_id)
    assert current[good["id"]] == "Pending" and current[stale["id"]] == "Pending", current
    assert len(audit_entries(admin, batch_id)) == audit_count
    print("PASS a stale selection is refused, with the row named and nothing saved")

    assert bulk(admin, batch_id, [good, good])[0] == 400
    assert bulk(admin, batch_id, [])[0] == 400
    assert admin.post(f"{url}/rows/bulk-approve", {"rows": [{"rowId": good["id"]}]})[0] == 400
    print("PASS repeated, empty, and versionless selections return 400")

    # Conflicts keep their individual confirmation.
    row = fresh[conflict["id"]]
    status, body = admin.put(f"{url}/rows/{row['id']}/decision", {"decision": "Approved", "rowVersion": row["rowVersion"],
                                                                  "customerVersion": row["matchedCustomerVersion"]})
    assert status == 400 and "confirmUpdate" in body["detail"], (status, body)
    print("PASS a conflict still needs its own confirmation")
    return batch_id, good


def check_bulk_across_workspaces(admin, member_batch, member_row):
    first, second = new_guest(), new_guest()
    sample = open("wwwroot/sample-customers.csv", "rb").read()
    mapping = {"nameColumn": 0, "companyColumn": 1, "emailColumn": 2, "phoneColumn": 3, "locationColumn": 4, "statusColumn": 5, "notesColumn": 6}
    first_batch = ready_batch(first, sample, "sample-customers.csv", mapping)
    second_batch = ready_batch(second, sample, "sample-customers.csv", mapping)
    first_new = next(row for row in first.rows(first_batch) if row["matchResult"] == "New")
    second_new = next(row for row in second.rows(second_batch) if row["matchResult"] == "New")

    status, _ = bulk(second, first_batch, [first_new])
    assert status == 404, status
    assert bulk(first, member_batch, [member_row])[0] == 404
    status, body = bulk(second, second_batch, [second_new, first_new])
    assert status == 409 and f"Row {first_new['id']} isn't in this batch" in body["detail"], (status, body)
    assert decisions(second, second_batch)[second_new["id"]] == "Pending"
    assert decisions(first, first_batch)[first_new["id"]] == "Pending"
    assert decisions(admin, member_batch)[member_row["id"]] == "Pending"

    status, body = bulk(second, second_batch, [second_new])
    assert status == 200 and body["approvedCount"] == 1, (status, body)
    print("PASS bulk approval can't reach another workspace's batch or rows")


def check_upload_and_mapping_audit(admin):
    prefix = f"a{uuid.uuid4().hex[:8]}"
    content = csv(people(prefix, 2))
    batch_id = ready_batch(admin, content, "audit.csv", MAPPING)
    entries = audit_entries(admin, batch_id)
    actions = [entry["action"] for entry in entries]
    assert actions[:2] == ["FileUploaded", "MappingSaved"], actions
    upload, mapping = entries[0], entries[1]
    assert upload["actorName"] and upload["timestampUtc"], upload
    assert json.loads(upload["details"]) == {"bytes": len(content)}, upload
    assert mapping["actorName"] == upload["actorName"], mapping
    assert json.loads(mapping["details"]) == {"Name": 0, "Email": 1, "Status": 2}, mapping
    assert prefix not in upload["details"] + mapping["details"]
    print("PASS uploads and mapping changes are audited with the actor, time, and no customer data")

    # A failed audit write takes the change with it.
    batches = sql_value("SELECT COUNT(*) FROM ImportBatches")
    sql("EXEC('CREATE TRIGGER FailAudit ON ImportAuditEntries AFTER INSERT AS THROW 50000, ''forced failure'', 1;')")
    try:
        try:
            admin.upload("rollback.csv", csv(people(f"{prefix}r", 1)))
            raise AssertionError("the upload should have failed")
        except RuntimeError as error:
            assert "500" in str(error), error
        status, _ = admin.put(f"/api/imports/{batch_id}/mapping", {"nameColumn": 1, "emailColumn": 0, "statusColumn": 2})
        # 405 rather than 500: the error handler re-runs /Error, which doesn't accept PUT.
        assert status in (405, 500), status
    finally:
        sql("DROP TRIGGER FailAudit")
    assert sql_value("SELECT COUNT(*) FROM ImportBatches") == batches
    assert admin.batch(batch_id)["mapping"] == {"Name": 0, "Email": 1, "Status": 2}
    print("PASS an audit failure also rolls back the upload or mapping change")


def run():
    wait_until_healthy()
    admin = signed_in_admin()
    check_finish_with_zero_approved(admin)
    member_batch, member_row = check_bulk_approval(admin)
    check_bulk_across_workspaces(admin, member_batch, member_row)
    check_upload_and_mapping_audit(admin)


if __name__ == "__main__":
    run()
