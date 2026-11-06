"""The shared AI call cap under concurrent requests from several guest workspaces.

Uses the fake OpenAI server, so run it with COMPOSE_FILE=compose.yaml:compose.mock.yaml and
the app's OpenAI__ApiKey set to mock-key. It restarts the app with a low cap, then restores it."""
import os
import threading

from support import compose, new_guest, signed_in_admin, sql, sql_value, wait_until_healthy

CAP = 12
GUESTS = 4
PER_GUEST = 5
QUESTION = {"question": "Show me active customers in Queens"}


def restart_app(cap):
    if cap is None:
        os.environ.pop("OPENAI_MAX_CALLS_PER_DAY", None)
    else:
        os.environ["OPENAI_MAX_CALLS_PER_DAY"] = str(cap)
    compose("up", "-d", "--no-deps", "--force-recreate", "--wait", "app")
    wait_until_healthy()


def provider_calls():
    result = compose("exec", "-T", "openai-mock", "python", "-c",
                     "import urllib.request; print(urllib.request.urlopen('http://127.0.0.1:8080/').read().decode())")
    return int(result.stdout.strip())


def run():
    restart_app(CAP)
    try:
        sql("DELETE FROM AiUsageDays")
        guests = [new_guest() for _ in range(GUESTS)]
        admin = signed_in_admin()
        calls_before = provider_calls()

        replies = []
        lock = threading.Lock()
        barrier = threading.Barrier(GUESTS * PER_GUEST)

        def search(guest):
            barrier.wait()
            reply = guest.post("/api/customers/search/ai", QUESTION)
            with lock:
                replies.append(reply)

        threads = [threading.Thread(target=search, args=(guest,)) for guest in guests for _ in range(PER_GUEST)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()

        statuses = sorted(reply[0] for reply in replies)
        assert statuses == [200] * CAP + [429] * (GUESTS * PER_GUEST - CAP), statuses
        limited = [reply[1]["detail"] for reply in replies if reply[0] == 429]
        assert all("shared limit" in detail for detail in limited), limited
        made = provider_calls() - calls_before
        assert made == CAP, f"the provider received {made} calls"
        assert int(sql_value("SELECT Calls FROM AiUsageDays")) == CAP
        print(f"PASS {GUESTS * PER_GUEST} concurrent requests from {GUESTS} guest workspaces: exactly {CAP} reached the provider, "
              f"{GUESTS * PER_GUEST - CAP} returned 429")

        # A request refused by the shared cap doesn't use a guest's own search.
        used = int(sql_value("SELECT SUM(AiSearches) FROM GuestWorkspaces WHERE AiSearches > 0"))
        assert used == CAP, used
        print("PASS searches stopped by the shared cap are given back to the guest's own quota")

        status, body = admin.post("/api/customers/search/ai", QUESTION)
        assert status == 429 and "shared limit" in body["detail"], (status, body)
        assert provider_calls() - calls_before == CAP
        print("PASS the cap applies to members too")

        # The cap survives a restart, because it's in the database.
        restart_app(CAP)
        status, body = signed_in_admin().post("/api/customers/search/ai", QUESTION)
        assert status == 429, (status, body)
        print("PASS the count survives a restart")

        # The guest's own limit of 5 still applies below the shared cap.
        restart_app(100)
        guest = new_guest()
        statuses = [guest.post("/api/customers/search/ai", QUESTION)[0] for _ in range(6)]
        assert statuses == [200] * 5 + [429], statuses
        status, body = guest.post("/api/customers/search/ai", QUESTION)
        assert status == 429 and "used its AI searches" in body["detail"], (status, body)
        print("PASS the guest quota of 5 still holds")
    finally:
        sql("DELETE FROM AiUsageDays")
        restart_app(None)


if __name__ == "__main__":
    run()
