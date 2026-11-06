"""Shared helpers for the DataBridge checks. Python standard library only."""
import urllib.parse
import http.cookiejar, io, json, os, ssl, subprocess, time, urllib.error, urllib.request, uuid, zipfile

BASE_URL = os.environ.get("DATABRIDGE_BASE_URL") or f"http://localhost:{os.environ.get('DATABRIDGE_PORT', '8088')}"
SECRETS_DIR = os.environ.get("DATABRIDGE_SECRETS_DIR", "./secrets")
# Only for the local production check, where Caddy serves its own test certificate.
INSECURE_TLS = os.environ.get("DATABRIDGE_INSECURE_TLS") == "1"
TLS_CONTEXT = ssl._create_unverified_context() if INSECURE_TLS else None


def read_secret(name):
    with open(os.path.join(SECRETS_DIR, "app", name)) as file:
        return file.read().strip()


def compose(*args, check=True):
    return subprocess.run(["docker", "compose", *args], check=check, capture_output=True, text=True)


def sql(query):
    """Runs a query in the Compose SQL Server and returns its rows as lists of strings."""
    script = ('SQLCMDPASSWORD="$(cat /run/secrets/sql_sa_password)" /opt/mssql-tools18/bin/sqlcmd '
              '-C -S localhost -U sa -d DataBridge -b -I -W -h -1 -s "|" -Q "$1"')
    result = compose("exec", "-T", "sqlserver", "bash", "-c", script, "_", "SET NOCOUNT ON; " + query, check=False)
    if result.returncode != 0:
        raise RuntimeError("SQL failed: " + (result.stdout + result.stderr)[:400])
    return [line.split("|") for line in result.stdout.splitlines() if line.strip()]


def sql_value(query):
    return sql(query)[0][0]


class Client:
    """A signed-in API client that keeps its cookies and sends the antiforgery token."""

    def __init__(self):
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar), NoRedirect(),
                                                  urllib.request.HTTPSHandler(context=TLS_CONTEXT))
        self.token = None

    def sign_in(self, email, password):
        self.refresh_token()
        body = urllib.parse.urlencode({"email": email, "password": password}).encode()
        status, _ = self.send("POST", "/account/login", body, "application/x-www-form-urlencoded")
        cookie_names = [cookie.name for cookie in self.jar]
        if status != 302 or ".AspNetCore.Identity.Application" not in cookie_names:
            raise RuntimeError(f"Sign-in failed with status {status}")
        self.refresh_token()

    def refresh_token(self):
        _, body = self.send("GET", "/api/account/antiforgery")
        self.token = body["token"]

    def send(self, method, path, body=None, content_type=None):
        request = urllib.request.Request(BASE_URL + path, data=body, method=method)
        if content_type:
            request.add_header("Content-Type", content_type)
        if self.token and method != "GET":
            request.add_header("X-CSRF-TOKEN", self.token)
        try:
            with self.opener.open(request, timeout=300) as response:
                return response.status, parse_json(response.read())
        except urllib.error.HTTPError as error:
            return error.code, parse_json(error.read())

    def get(self, path):
        return self.send("GET", path)

    def post(self, path, value=None):
        return self.send("POST", path, None if value is None else json.dumps(value).encode(), "application/json")

    def put(self, path, value):
        return self.send("PUT", path, json.dumps(value).encode(), "application/json")

    def upload(self, file_name, content):
        boundary = uuid.uuid4().hex
        body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{file_name}"\r\n'
                f"Content-Type: application/octet-stream\r\n\r\n").encode() + content + f"\r\n--{boundary}--\r\n".encode()
        status, reply = self.send("POST", "/api/imports", body, f"multipart/form-data; boundary={boundary}")
        if status != 202:
            raise RuntimeError(f"Upload of {file_name} returned {status}: {reply}")
        return reply["batchId"]

    def batch(self, batch_id):
        status, body = self.get(f"/api/imports/{batch_id}")
        if status != 200:
            raise RuntimeError(f"GET batch returned {status}")
        return body

    def wait_for_job(self, batch_id, timeout=300):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                batch = self.batch(batch_id)
                if not batch["job"]["isActive"]:
                    return batch
            except (RuntimeError, urllib.error.URLError, ConnectionError, TimeoutError):
                pass  # The app may be restarting during a recovery scenario.
            time.sleep(0.5)
        raise TimeoutError(f"Batch {batch_id}'s job didn't finish within {timeout}s")

    def rows(self, batch_id):
        items, page = [], 1
        while True:
            status, body = self.get(f"/api/imports/{batch_id}/rows?page={page}&pageSize=100")
            if status != 200:
                raise RuntimeError(f"GET rows returned {status}")
            items += body["items"]
            if page >= body["totalPages"]:
                return items
            page += 1


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def parse_json(raw):
    try:
        return json.loads(raw) if raw else None
    except json.JSONDecodeError:
        return None


def signed_in_admin():
    client = Client()
    client.sign_in(read_secret("Seed__Users__0__Email"), read_secret("Seed__Users__0__Password"))
    return client


def signed_in_reviewer():
    client = Client()
    client.sign_in(read_secret("Seed__Users__1__Email"), read_secret("Seed__Users__1__Password"))
    return client


def new_guest():
    """A signed-in guest with its own workspace. Uses 2 of the 16 guest entries per 5 minutes."""
    client = Client()
    client.refresh_token()
    status, body = client.send("POST", "/account/guest-token")
    assert status == 200, status
    status, _ = client.send("POST", "/account/guest", urllib.parse.urlencode({"token": body["token"]}).encode(),
                            "application/x-www-form-urlencoded")
    assert status == 302, status
    client.refresh_token()
    return client


def approval(row, **extra):
    """The decision request for approving a row as shown in this preview."""
    request = {"decision": "Approved", "rowVersion": row["rowVersion"], **extra}
    if row["matchResult"] == "Conflict":
        request.update(confirmUpdate=True, customerVersion=row["matchedCustomerVersion"])
    return request


def audit_entries(client, batch_id):
    items, page = [], 1
    while True:
        status, body = client.get(f"/api/imports/{batch_id}/audit?page={page}&pageSize=100")
        assert status == 200, status
        items += body["items"]
        if page >= body["totalPages"]:
            return items
        page += 1


def ready_batch(client, content, file_name, mapping):
    """Uploads a file, saves its mapping, and validates it."""
    batch_id = client.upload(file_name, content)
    batch = client.wait_for_job(batch_id, timeout=120)
    assert batch["job"]["stage"] == "Completed", batch["job"]
    status, reply = client.put(f"/api/imports/{batch_id}/mapping", mapping)
    assert status == 200, (status, reply)
    status, reply = client.post(f"/api/imports/{batch_id}/validate")
    assert status == 202, (status, reply)
    batch = client.wait_for_job(batch_id, timeout=120)
    assert batch["status"] == "ReadyForReview", batch
    return batch_id


def wait_until_healthy(timeout=180):
    """Waits for readiness, which includes SQL Server."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(BASE_URL + "/api/health/ready", timeout=5, context=TLS_CONTEXT) as response:
                if response.status == 200:
                    return
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        time.sleep(1)
    raise TimeoutError("The app didn't become healthy")


def make_csv(prefix, count):
    lines = ["Full Name,Email Address,Account State"]
    lines += [f"Person {prefix} {i},{prefix}-{i}@example.test,Active" for i in range(count)]
    return ("\n".join(lines) + "\n").encode()


def make_xlsx(prefix, count):
    """A minimal workbook built without third-party packages."""
    def row(number, values):
        cells = "".join(f'<c r="{chr(65 + i)}{number}" t="inlineStr"><is><t>{v}</t></is></c>' for i, v in enumerate(values))
        return f'<row r="{number}">{cells}</row>'

    rows = [row(1, ["Full Name", "Email Address", "Account State"])]
    rows += [row(i + 2, [f"Person {prefix} {i}", f"{prefix}-{i}@example.test", "Active"]) for i in range(count)]
    sheet = ('<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
             "<sheetData>" + "".join(rows) + "</sheetData></worksheet>")
    parts = {
        "[Content_Types].xml": '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>',
        "_rels/.rels": '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        "xl/workbook.xml": '<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels": '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
        "xl/worksheets/sheet1.xml": sheet,
    }
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, text in parts.items():
            archive.writestr(name, text)
    return buffer.getvalue()
