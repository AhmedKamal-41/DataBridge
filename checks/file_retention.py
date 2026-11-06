"""Opt-in cleanup of raw files. It restarts the app with the setting off, then on, then off."""
import os
import time
import uuid

from support import approval, audit_entries, compose, new_guest, ready_batch, signed_in_admin, sql_value, wait_until_healthy

MAPPING = {"nameColumn": 0, "emailColumn": 1, "statusColumn": 2}


def restart_app(enabled):
    os.environ["IMPORT_RAW_FILE_RETENTION_ENABLED"] = "true" if enabled else "false"
    os.environ["IMPORT_RAW_FILE_RETENTION_DAYS"] = "0" if enabled else "90"
    compose("up", "-d", "--no-deps", "--force-recreate", "--wait", "app")
    wait_until_healthy()


def has_file(batch_id):
    return int(sql_value(f"SELECT COUNT(*) FROM ImportBatchFiles WHERE ImportBatchId={batch_id}")) == 1


def committed_batch(client, name, mapping=MAPPING, content=None):
    prefix = uuid.uuid4().hex[:8]
    content = content or f"Name,Email,Status\nPerson {prefix},{prefix}@example.test,Active\n".encode()
    batch_id = ready_batch(client, content, name, mapping)
    for row in client.rows(batch_id):
        request = approval(row) if row["matchResult"] in ("New", "Conflict") else {"decision": "Rejected", "reason": "Not needed"}
        status, reply = client.put(f"/api/imports/{batch_id}/rows/{row['id']}/decision", request)
        assert status == 200, (status, reply)
    status, reply = client.post(f"/api/imports/{batch_id}/commit")
    assert status == 200, (status, reply)
    return batch_id


def run():
    restart_app(False)
    try:
        admin = signed_in_admin()
        committed = committed_batch(admin, "retention-committed.csv")
        prefix = uuid.uuid4().hex[:8]
        in_review = ready_batch(admin, f"Name,Email,Status\nPerson,{prefix}@example.test,Active\n".encode(), "retention-review.csv", MAPPING)
        guest = new_guest()
        sample = open("wwwroot/sample-customers.csv", "rb").read()
        guest_batch = committed_batch(guest, "sample-customers.csv", {
            "nameColumn": 0, "companyColumn": 1, "emailColumn": 2, "phoneColumn": 3, "locationColumn": 4, "statusColumn": 5, "notesColumn": 6}, sample)

        restart_app(False)
        time.sleep(10)
        assert has_file(committed) and has_file(in_review) and has_file(guest_batch)
        print("PASS with cleanup off by default, a committed member file is kept")

        restart_app(True)
        deadline = time.monotonic() + 120
        while has_file(committed):
            assert time.monotonic() < deadline, "the committed file wasn't removed"
            time.sleep(1)
        assert has_file(in_review), "a batch still in review needs its file"
        assert has_file(guest_batch), "guest files go with their workspace"
        print("PASS with cleanup on, a committed member file is removed, and files in review and guest files are kept")

        admin = signed_in_admin()
        deleted = [entry for entry in audit_entries(admin, committed) if entry["action"] == "RawFileDeleted"]
        assert len(deleted) == 1 and deleted[0]["actor"] == "system", deleted
        assert admin.batch(committed)["status"] == "Committed"
        assert admin.batch(committed)["commitSummary"]["customersCreated"] == 1
        print("PASS the removal is audited, and the committed batch's history stays readable")
    finally:
        os.environ.pop("IMPORT_RAW_FILE_RETENTION_ENABLED", None)
        os.environ.pop("IMPORT_RAW_FILE_RETENTION_DAYS", None)
        compose("up", "-d", "--no-deps", "--force-recreate", "--wait", "app")
        wait_until_healthy()


if __name__ == "__main__":
    run()
