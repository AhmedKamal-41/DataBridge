"""Two accounts: a reviewer's approval of a preview that another user has since corrected.

Needs Seed__Users__0 (Admin) and Seed__Users__1 (Reviewer) in the secrets folder."""
import uuid

from support import approval, audit_entries, ready_batch, signed_in_admin, signed_in_reviewer, sql, sql_value, wait_until_healthy

MAPPING = {"nameColumn": 0, "emailColumn": 1, "locationColumn": 2, "statusColumn": 3}


def run():
    wait_until_healthy()
    admin, reviewer = signed_in_admin(), signed_in_reviewer()
    email = f"stale-{uuid.uuid4().hex[:8]}@example.test"
    sql(f"INSERT INTO Customers(Name,Email,NormalizedEmail,Location,Status) VALUES('Pat Stale','{email}','{email}','Queens','Active')")
    batch_id = ready_batch(admin, f"Name,Email,Location,Status\nPat Stale,{email},Brooklyn,Active\n".encode(), "stale.csv", MAPPING)
    url = f"/api/imports/{batch_id}"

    # The reviewer previews a change to Brooklyn.
    preview = reviewer.rows(batch_id)[0]
    assert preview["matchResult"] == "Conflict" and preview["draftLocation"] == "Brooklyn", preview
    assert preview["rowVersion"], "the preview must include a row version"

    # Another user corrects the draft to Albany.
    correction = {"draftName": "Pat Stale", "draftEmail": email, "draftLocation": "Albany", "draftStatus": "Active"}
    status, body = admin.put(f"{url}/rows/{preview['id']}", correction)
    assert status == 200, (status, body)
    audit_before = audit_entries(admin, batch_id)

    # The reviewer's approval of the old preview is refused and changes nothing.
    status, body = reviewer.put(f"{url}/rows/{preview['id']}/decision", approval(preview))
    assert status == 409 and "changed after you viewed it" in body["detail"], (status, body)
    current = reviewer.rows(batch_id)[0]
    assert current["reviewDecision"] == "Pending", current
    assert audit_entries(admin, batch_id) == audit_before
    print("PASS approving the old Brooklyn preview returns 409 with the decision and audit history unchanged")

    status, body = reviewer.put(f"{url}/rows/{preview['id']}/decision", {"decision": "Approved", "confirmUpdate": True,
                                                                         "customerVersion": current["matchedCustomerVersion"]})
    assert status == 400 and "rowVersion" in body["detail"], (status, body)
    print("PASS approving without a row version returns 400")

    # The reloaded preview shows Albany, with a new version.
    assert current["draftLocation"] == "Albany" and current["rowVersion"] != preview["rowVersion"], current

    # The existing customer version check still applies, with a current row version.
    sql(f"UPDATE Customers SET Notes='Changed elsewhere' WHERE NormalizedEmail='{email}'")
    status, body = reviewer.put(f"{url}/rows/{current['id']}/decision", approval(current))
    assert status == 409 and "customer changed" in body["detail"], (status, body)
    assert reviewer.rows(batch_id)[0]["reviewDecision"] == "Pending"
    print("PASS a current row version with an old customer version returns 409")

    # A fresh review of the current values succeeds.
    fresh = reviewer.rows(batch_id)[0]
    status, body = reviewer.put(f"{url}/rows/{fresh['id']}/decision", approval(fresh))
    assert status == 200 and body["reviewDecision"] == "Approved", (status, body)

    status, _ = reviewer.post(f"{url}/commit")
    assert status == 403, status
    status, summary = admin.post(f"{url}/commit")
    assert status == 200 and summary["customersUpdated"] == 1, (status, summary)
    location = sql_value(f"SELECT Location FROM Customers WHERE NormalizedEmail='{email}'")
    assert location == "Albany", location

    entries = audit_entries(admin, batch_id)
    approvals = [entry for entry in entries if entry["action"] == "UpdateApproved"]
    corrections = [entry for entry in entries if entry["action"] == "RowCorrected"]
    assert len(approvals) == 1 and len(corrections) == 1, entries
    assert approvals[0]["actor"] != corrections[0]["actor"], "two accounts should appear in the audit trail"
    print("PASS the fresh approval commits Albany, and the audit trail shows each account's action")


if __name__ == "__main__":
    run()
