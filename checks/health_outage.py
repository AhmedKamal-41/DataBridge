"""Readiness and liveness before, during, and after a SQL Server outage. Stops SQL Server."""
import json
import subprocess
import time
import urllib.error
import urllib.request

from support import BASE_URL, TLS_CONTEXT, compose, wait_until_healthy


def status_of(path):
    started = time.monotonic()
    try:
        with urllib.request.urlopen(BASE_URL + path, timeout=15, context=TLS_CONTEXT) as response:
            return response.status, time.monotonic() - started
    except urllib.error.HTTPError as error:
        return error.code, time.monotonic() - started


def wait_for(path, wanted, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if status_of(path)[0] == wanted:
            return
        time.sleep(1)
    raise TimeoutError(f"{path} didn't return {wanted} within {timeout}s")


def run():
    wait_until_healthy()
    config = compose("config").stdout
    assert "/api/health/ready" in config, "Compose should health-check readiness"
    assert status_of("/api/health/ready")[0] == 200 and status_of("/api/health/live")[0] == 200
    print("PASS before the outage: readiness 200, liveness 200")

    compose("kill", "sqlserver")
    try:
        wait_for("/api/health/ready", 503, 30)
        for _ in range(5):
            ready, ready_seconds = status_of("/api/health/ready")
            live, live_seconds = status_of("/api/health/live")
            assert ready == 503, ready
            assert live == 200, live
            assert ready_seconds < 5, f"readiness took {ready_seconds:.1f}s"
            assert live_seconds < 1, f"liveness took {live_seconds:.1f}s"
        print(f"PASS during the outage: readiness 503 in {ready_seconds:.1f}s, liveness 200 in {live_seconds:.2f}s")

        # Compose's own health check is seeing the outage too.
        container = compose("ps", "-q", "app").stdout.strip()
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            log = json.loads(subprocess.run(["docker", "inspect", "--format", "{{json .State.Health.Log}}", container],
                                            capture_output=True, text=True, check=True).stdout)
            if log[-1]["ExitCode"] != 0:
                break
            time.sleep(1)
        assert log[-1]["ExitCode"] != 0, log[-1]
        assert compose("ps", "app", "--format", "{{.State}}").stdout.strip() == "running"
        print("PASS during the outage: the Compose health check fails, and the app process keeps running")
    finally:
        compose("up", "-d", "--no-deps", "--wait", "sqlserver")

    wait_for("/api/health/ready", 200, 60)
    assert status_of("/api/health/live")[0] == 200
    deadline = time.monotonic() + 60
    while compose("ps", "app", "--format", "{{.Health}}").stdout.strip() != "healthy":
        assert time.monotonic() < deadline, "Compose didn't report the app as healthy again"
        time.sleep(2)
    print("PASS after the outage: readiness 200, liveness 200, and Compose reports healthy")


if __name__ == "__main__":
    run()
