"""A fake OpenAI Responses endpoint for the mocked AI suite. It returns the expected answer
from the reference file, so that suite proves the app's pipeline, not a model's judgment."""
import json, os, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(HERE, "ai_reference_questions.json")) as file:
    QUESTIONS = {item["question"]: item for item in json.load(file)["questions"]}

# With MOCK_SABOTAGE set, every search gets a wrong filter, to prove the suite can fail.
SABOTAGE = bool(os.environ.get("MOCK_SABOTAGE"))

# How many answers it has given, so a check can compare them with the app's cap.
calls = 0
calls_lock = threading.Lock()


def reply_for(question):
    item = QUESTIONS.get(question)
    outcome = item["outcome"] if item else "Unsupported"
    filters = (item or {}).get("filters", {})
    reply = {"supported": outcome != "Unsupported", "location": None, "status": None, "company": None, "name": None}
    reply.update(filters)
    if SABOTAGE and outcome == "Applied":
        reply["location"] = "Wrong Place"
    return reply


class Handler(BaseHTTPRequestHandler):
    # Keep-alive, like a real provider. With HTTP/1.0, the connection pool in the app's
    # HttpClient sometimes saw a connection close before its response under concurrent load.
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        data = str(calls).encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        global calls
        body = json.loads(self.read_body())
        authorized = self.headers.get("Authorization") == "Bearer mock-key"
        # The mock fails loudly if the app stops sending what the real API needs.
        has_schema = body.get("text", {}).get("format", {}).get("type") == "json_schema"
        if self.path != "/v1/responses" or not authorized or not has_schema or body.get("store") is not False:
            self.send_response(400)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        with calls_lock:
            calls += 1
        output = {"id": "resp_mock", "model": "mock-model", "status": "completed", "output": [
            {"type": "message", "status": "completed", "role": "assistant",
             "content": [{"type": "output_text", "text": json.dumps(reply_for(body["input"]))}]}]}
        data = json.dumps(output).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def read_body(self):
        if self.headers.get("Content-Length"):
            return self.rfile.read(int(self.headers["Content-Length"]))
        # The app's HttpClient sends JSON with chunked encoding.
        data = b""
        while True:
            size = int(self.rfile.readline().strip(), 16)
            if size == 0:
                self.rfile.readline()
                return data
            data += self.rfile.read(size)
            self.rfile.readline()

    def log_message(self, *args):
        pass


class Server(ThreadingHTTPServer):
    # The default of 5 drops connections when many requests arrive at once.
    request_queue_size = 128


Server(("0.0.0.0", 8080), Handler).serve_forever()
