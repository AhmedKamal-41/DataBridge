"""Runs the 30 reference questions against a running stack with an empty Customers table.
Use checks/run_ai_eval.sh, which starts an isolated stack for it."""
import json, os, re, sys, time
from support import compose, signed_in_admin, sql, sql_value

mode = sys.argv[1]
HERE = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(HERE, "ai_reference_questions.json")) as file:
    reference = json.load(file)

if int(sql_value("SELECT COUNT(*) FROM Customers")) != 0:
    sys.exit("This suite needs an empty Customers table, so it can set exact customer IDs.")


def quoted(value):
    return "NULL" if value is None else "'" + value.replace("'", "''") + "'"


values = ",".join(
    f"({c['id']},{quoted(c['name'])},{quoted(c['company'])},'customer{c['id']}@example.test','customer{c['id']}@example.test',{quoted(c['location'])},'{c['status']}')"
    for c in reference["customers"])
sql(f"SET IDENTITY_INSERT Customers ON; INSERT INTO Customers(Id,Name,Company,Email,NormalizedEmail,Location,Status) VALUES {values}; SET IDENTITY_INSERT Customers OFF;")

client = signed_in_admin()
FIELDS = ["location", "status", "company", "name"]


def normalized(filters):
    return {field: str((filters or {}).get(field) or "").strip().lower() for field in FIELDS}


passed = 0
failures = []
started = time.monotonic()
for number, item in enumerate(reference["questions"], 1):
    status, reply = client.post("/api/customers/search/ai", {"question": item["question"]})
    outcome = reply.get("outcome") if isinstance(reply, dict) else None
    problems = []
    if status != 200:
        problems.append(f"status {status}: {reply}")
    if outcome != item["outcome"]:
        problems.append(f"outcome {outcome}, expected {item['outcome']}")
    if item["outcome"] == "Applied" and outcome == "Applied":
        if normalized(reply["filters"]) != normalized(item["filters"]):
            problems.append(f"filters {reply['filters']}, expected {item['filters']}")
        ids = sorted(customer["id"] for customer in reply["customers"]["items"])
        if ids != item["customerIds"]:
            problems.append(f"customers {ids}, expected {item['customerIds']}")
    if problems:
        failures.append((number, item["question"], problems))
    else:
        passed += 1
    print(f"{'PASS' if not problems else 'FAIL'} {number:>2}  {item['outcome']:<11} {item['question']}" + (f"  -> {'; '.join(problems)}" if problems else ""), flush=True)

logs = compose("logs", "app", "--no-log-prefix").stdout
models = sorted(set(re.findall(r"OpenAI answered with model ([^\s]+?)\.?\n", logs)))
writes = int(sql_value("SELECT COUNT(*) FROM Customers")) != len(reference["customers"])
applied = sum(1 for q in reference["questions"] if q["outcome"] == "Applied")
print(f"\nMode: {mode}. Model reported by the API: {', '.join(models) or 'none'}. "
      f"{passed} of {len(reference['questions'])} passed in {time.monotonic() - started:.1f}s "
      f"({applied} searches, {sum(1 for q in reference['questions'] if q['outcome'] == 'NoFilters')} clarifications, "
      f"{sum(1 for q in reference['questions'] if q['outcome'] == 'Unsupported')} rejections). "
      f"Customer count changed: {writes}.")
sys.exit(0 if passed == len(reference["questions"]) and not writes else 1)
