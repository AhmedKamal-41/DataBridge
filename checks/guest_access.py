"""Regression check for single-use guest entry and isolation of guest imports."""
from concurrent.futures import ThreadPoolExecutor
from copy import copy
from pathlib import Path
from urllib.parse import urlencode

from support import Client, signed_in_admin, wait_until_healthy


SAMPLE = Path(__file__).resolve().parents[1] / "wwwroot" / "sample-customers.csv"
MAPPING = {
    "nameColumn": 0, "companyColumn": 1, "emailColumn": 2, "phoneColumn": 3,
    "locationColumn": 4, "statusColumn": 5, "notesColumn": 6,
}


def issue_token(client):
    client.refresh_token()
    status, body = client.send("POST", "/account/guest-token")
    assert status == 200, status
    return body["token"]


def redeem(client, token):
    status, _ = client.send("POST", "/account/guest", urlencode({"token": token}).encode(),
                            "application/x-www-form-urlencoded")
    assert status == 302, status
    client.refresh_token()
    return client.get("/api/customers")[0]


def customers(client):
    status, body = client.get("/api/customers")
    assert status == 200, (status, body)
    return body


def run():
    wait_until_healthy()
    member = signed_in_admin()
    member_count = customers(member)["totalCount"]
    first, second = Client(), Client()
    assert first.get("/api/customers")[0] == 401
    token = issue_token(first)
    second.refresh_token()
    with ThreadPoolExecutor(max_workers=2) as pool:
        attempts = [pool.submit(redeem, client, token) for client in (first, second)]
        results = [attempt.result() for attempt in attempts]
    assert sorted(results) == [200, 401], results

    guest, other = (first, second) if results[0] == 200 else (second, first)
    assert redeem(other, issue_token(other)) == 200
    guest_ids = {row["id"] for row in customers(guest)["items"]}
    other_ids = {row["id"] for row in customers(other)["items"]}
    assert len(guest_ids) == len(other_ids) == 3 and not guest_ids & other_ids
    print("PASS single-use redemption and separate sample data")

    batch_id = guest.upload(SAMPLE.name, SAMPLE.read_bytes())
    batch_url = f"/api/imports/{batch_id}"
    parsed = guest.wait_for_job(batch_id, timeout=45)
    assert parsed["status"] == "Uploaded" and parsed["rowCount"] == 4, parsed
    for outsider in (other, member):
        assert outsider.get(batch_url)[0] == 404
        assert outsider.get(batch_url + "/rows")[0] == 404
        assert outsider.get(batch_url + "/audit")[0] == 404
        assert outsider.put(batch_url + "/mapping", MAPPING)[0] == 404
    assert other.get("/api/imports")[1]["totalCount"] == 0
    assert guest.put(batch_url + "/mapping", MAPPING)[0] == 200
    assert guest.post(batch_url + "/validate")[0] == 202
    validated = guest.wait_for_job(batch_id, timeout=45)
    assert validated["status"] == "ReadyForReview", validated
    rows = guest.rows(batch_id)
    assert [row["matchResult"] for row in rows] == ["Conflict", "New", "Duplicate", None], rows
    for row in rows:
        decision = {"decision": "Rejected", "reason": "Guest isolation check"}
        if row["matchResult"] == "New":
            decision = {"decision": "Approved"}
        if row["matchResult"] == "Conflict":
            assert row["matchedCustomerId"] in guest_ids
            decision = {"decision": "Approved", "confirmUpdate": True,
                        "customerVersion": row["matchedCustomerVersion"]}
        status, body = guest.put(f"{batch_url}/rows/{row['id']}/decision", decision)
        assert status == 200, (status, body)

    status, result = guest.post(batch_url + "/commit")
    assert status == 200, (status, result)
    assert (result["customersCreated"], result["customersUpdated"], result["rowsRejected"]) == (1, 1, 2), result
    assert customers(guest)["totalCount"] == 4
    assert customers(other)["totalCount"] == 3
    assert customers(member)["totalCount"] == member_count
    print("PASS upload through commit with cross-workspace access denied")

    copied = Client()
    for cookie in guest.jar:
        copied.jar.set_cookie(copy(cookie))
    assert guest.send("POST", "/account/logout")[0] == 302
    assert guest.get("/api/customers")[0] == 401
    assert copied.get("/api/customers")[0] == 401
    print("PASS guest exit revokes a copied session cookie")


if __name__ == "__main__":
    run()
