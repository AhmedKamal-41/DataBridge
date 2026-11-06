"""Real-browser check of the review changes, with an Admin and a Reviewer.

Needs Seed__Users__0 (Admin), Seed__Users__1 (Reviewer), and: pip install playwright"""
import uuid

from playwright.sync_api import expect, sync_playwright

from support import (BASE_URL, INSECURE_TLS, ready_batch, read_secret, signed_in_admin, sql, wait_until_healthy)

MAPPING = {"nameColumn": 0, "emailColumn": 1, "locationColumn": 2, "statusColumn": 3}
browser_errors = []


def sign_in(browser, number):
    context = browser.new_context(viewport={"width": 1280, "height": 900}, ignore_https_errors=INSECURE_TLS)
    page = context.new_page()
    page.on("pageerror", lambda error: browser_errors.append(str(error)))
    page.on("console", lambda message: browser_errors.append(message.text) if message.type == "error" else None)
    page.goto(BASE_URL + "/login")
    page.get_by_label("Email").fill(read_secret(f"Seed__Users__{number}__Email"))
    page.get_by_label("Password").fill(read_secret(f"Seed__Users__{number}__Password"))
    page.get_by_role("main").get_by_role("button", name="Sign in").click()
    page.wait_for_url("**/workspace")
    return page


def csv(lines):
    return ("Name,Email,Location,Status\n" + "\n".join(lines) + "\n").encode()


def open_batch(page, batch_id):
    page.goto(f"{BASE_URL}/imports/{batch_id}")
    expect(page.locator("[data-source-row]").first).to_be_visible(timeout=30_000)


def check_stale_approval(admin_api, reviewer):
    email = f"browser-{uuid.uuid4().hex[:8]}@example.test"
    sql(f"INSERT INTO Customers(Name,Email,NormalizedEmail,Location,Status) VALUES('Pat Browser','{email}','{email}','Queens','Active')")
    batch_id = ready_batch(admin_api, csv([f"Pat Browser,{email},Brooklyn,Active"]), "browser-stale.csv", MAPPING)

    open_batch(reviewer, batch_id)
    card = reviewer.locator("[data-source-row]").first
    expect(card.get_by_role("cell", name="Brooklyn").first).to_be_visible()
    confirm = card.get_by_role("checkbox")
    confirm.check()
    approve = card.get_by_role("button", name="Approve update")
    expect(approve).to_be_enabled()

    row = admin_api.rows(batch_id)[0]
    status, body = admin_api.put(f"/api/imports/{batch_id}/rows/{row['id']}", {
        "draftName": "Pat Browser", "draftEmail": email, "draftLocation": "Albany", "draftStatus": "Active"})
    assert status == 200, (status, body)

    approve.click()
    expect(card.get_by_role("alert")).to_contain_text("changed after you viewed it")
    expect(card.get_by_role("cell", name="Albany").first).to_be_visible()
    expect(card.locator(".badge.good")).to_have_count(0)
    expect(confirm).not_to_be_checked()
    expect(approve).to_be_disabled()
    assert admin_api.rows(batch_id)[0]["reviewDecision"] == "Pending"
    print("PASS stale approval: the page shows the conflict, reloads Albany, and clears the confirmation")

    confirm.check()
    approve.click()
    expect(card.locator(".badge.good")).to_be_visible()
    print("PASS a fresh confirmation approves the current values")


def check_bulk_approval(admin_api, admin):
    prefix = f"bulk{uuid.uuid4().hex[:8]}"
    existing = f"{prefix}-old@example.test"
    sql(f"INSERT INTO Customers(Name,Email,NormalizedEmail,Location,Status) VALUES('Old Name','{existing}','{existing}','Queens','Active')")
    lines = [f"{prefix} {i},{prefix}-{i}@example.test,Albany,Active" for i in range(3)] + [f"New Name,{existing},Brooklyn,Active"]
    batch_id = ready_batch(admin_api, csv(lines), "browser-bulk.csv", MAPPING)

    open_batch(admin, batch_id)
    cards = admin.locator("[data-source-row]")
    bulk_button = admin.get_by_role("button", name="Approve selected")
    expect(bulk_button).to_have_text("Approve selected (0)")
    expect(bulk_button).to_be_disabled()
    expect(cards.nth(3).get_by_label("for bulk approval")).to_have_count(0)
    print("PASS conflicts have no bulk selection box")

    cards.nth(0).get_by_label("for bulk approval").check()
    cards.nth(1).get_by_label("for bulk approval").check()
    expect(bulk_button).to_have_text("Approve selected (2)")
    bulk_button.click()
    expect(admin.get_by_role("status").filter(has_text="2 new row(s) were approved")).to_be_visible()
    expect(cards.nth(0).locator(".badge.good")).to_be_visible()
    expect(cards.nth(1).locator(".badge.good")).to_be_visible()
    expect(cards.nth(2).locator(".badge.good")).to_have_count(0)
    expect(bulk_button).to_have_text("Approve selected (0)")
    print("PASS only the selected rows are approved")

    third = cards.nth(2).get_by_label("for bulk approval")
    third.check()
    row = admin_api.rows(batch_id)[2]
    status, body = admin_api.put(f"/api/imports/{batch_id}/rows/{row['id']}", {
        "draftName": "Corrected Elsewhere", "draftEmail": row["draftEmail"], "draftLocation": "Albany", "draftStatus": "Active"})
    assert status == 200, (status, body)
    bulk_button.click()
    expect(admin.get_by_role("alert").filter(has_text="Nothing was approved")).to_contain_text(f"Source row {row['sourceRowNumber']}")
    expect(cards.nth(2).get_by_role("cell", name="Corrected Elsewhere")).to_be_visible()
    expect(cards.nth(2).locator(".badge.good")).to_have_count(0)
    expect(bulk_button).to_have_text("Approve selected (0)")
    print("PASS a stale selection is explained, the page reloads, and the selection is cleared")

    admin.get_by_role("button", name="Select all new rows on this page").click()
    expect(bulk_button).to_have_text("Approve selected (1)")
    bulk_button.click()
    expect(cards.nth(2).locator(".badge.good")).to_be_visible()
    expect(admin.get_by_test_id("bulk-actions")).to_have_count(0)
    audit = admin.get_by_test_id("audit")
    expect(audit.get_by_role("cell", name="InsertApproved")).to_have_count(3)
    expect(audit.get_by_role("cell", name="FileUploaded")).to_have_count(1)
    expect(audit.get_by_role("cell", name="MappingSaved")).to_have_count(1)
    print("PASS select all works, and the audit table shows each approval, the upload, and the mapping")


def check_finish_with_zero_approved(admin_api, admin):
    prefix = f"none{uuid.uuid4().hex[:8]}"
    batch_id = ready_batch(admin_api, csv([f"{prefix} {i},{prefix}-{i}@example.test,Albany,Active" for i in range(2)]), "browser-none.csv", MAPPING)
    open_batch(admin, batch_id)
    cards = admin.locator("[data-source-row]")
    commit = admin.get_by_role("button", name="Commit batch")

    commit.click()
    expect(admin.get_by_role("alert").filter(has_text="Every row needs a decision")).to_be_visible()
    print("PASS pending rows block finishing in the page")

    for index in range(2):
        card = cards.nth(index)
        card.get_by_label("Rejection reason").fill("Not needed")
        card.get_by_role("button", name="Reject").click()
        expect(card.locator(".badge.bad")).to_be_visible()
    commit.click()
    summary = admin.get_by_test_id("commit-summary")
    expect(summary).to_be_visible()
    assert summary.inner_text().split() == ["Inserted", "0", "Updated", "0", "Rejected", "2"], summary.inner_text()
    expect(admin.get_by_test_id("batch-status")).to_have_text("Committed")
    print("PASS a fully rejected batch shows the summary of 0 inserted, 0 updated, and 2 rejected")


def main():
    wait_until_healthy()
    admin_api = signed_in_admin()
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch()
        print(f"Chromium {browser.version}, against {BASE_URL}")
        admin, reviewer = sign_in(browser, 0), sign_in(browser, 1)
        check_stale_approval(admin_api, reviewer)
        check_bulk_approval(admin_api, admin)
        check_finish_with_zero_approved(admin_api, admin)
        browser.close()
    # The browser logs a 4xx response as an error, and these flows expect 400 and 409.
    unexpected = [text for text in browser_errors if "status of 4" not in text]
    assert not unexpected, unexpected
    print("PASS no unexpected browser errors")


if __name__ == "__main__":
    main()
