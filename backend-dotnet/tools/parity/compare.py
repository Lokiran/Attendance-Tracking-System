"""Compare the Python and .NET backends response by response (docs/dotnet-migration-plan.md §11).

Both backends must use the same database and SECRET_KEY, with DEV_LOGIN_ENABLED=true (use a local COPY of the data).
Sessions are created on the Python backend and reused on .NET, so both answer as the same signed-in person.

    python backend-dotnet/tools/parity/compare.py --python http://127.0.0.1:8010 --dotnet http://127.0.0.1:8011 \
        --admin admin@nexa.example

Prints the number of identical / different responses and, for each difference, the request and the JSON paths that
differ (values truncated). Exit code 1 when anything differs.
"""
import argparse
import json
import sys
import urllib.error
import urllib.request
from datetime import date, timedelta
from urllib.parse import urlencode

IGNORED = {"generated_at", "server_time", "request_id"}


def call(base: str, path: str, cookie: str | None = None, method: str = "GET", body: dict | None = None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    if cookie:
        req.add_header("Cookie", cookie)
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            raw, status, headers = r.read(), r.status, r.headers
    except urllib.error.HTTPError as e:
        raw, status, headers = e.read(), e.code, e.headers
    ctype = headers.get("Content-Type", "")
    parsed = json.loads(raw) if raw and "json" in ctype else raw
    return status, parsed, headers


def login(base: str, email: str) -> tuple[str, dict]:
    status, body, headers = call(base, "/api/auth/dev-login", method="POST", body={"email": email})
    if status != 200:
        sys.exit(f"dev-login as {email} failed: {status} {body}")
    cookies = [c.split(";", 1)[0] for c in headers.get_all("Set-Cookie") or []]
    return "; ".join(cookies), body


def diff(a, b, path="$", out=None):
    out = [] if out is None else out
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            if k in IGNORED:
                continue
            if k not in a or k not in b:
                out.append((f"{path}.{k}", a.get(k, "<missing>"), b.get(k, "<missing>")))
            else:
                diff(a[k], b[k], f"{path}.{k}", out)
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append((f"{path}.length", len(a), len(b)))
        for i, (x, y) in enumerate(zip(a, b)):
            diff(x, y, f"{path}[{i}]", out)
    elif isinstance(a, bool) or isinstance(b, bool) or not (isinstance(a, (int, float)) and isinstance(b, (int, float))):
        if a != b:
            out.append((path, a, b))
    elif a != b:  # numbers by value: 100 == 100.0
        out.append((path, a, b))
    return out


def requests_for(me: dict, ids: list[int], today: date, role: str) -> list[str]:
    months = [(date(2026, m, 1), (date(2026, m + 1, 1) - timedelta(days=1))) for m in range(6, 12)]
    ranges = [("2026-06-01", today.isoformat()), ("2026-09-15", "2026-10-20"), (today.isoformat(), today.isoformat())]
    ranges += [(a.isoformat(), b.isoformat()) for a, b in months]
    out = ["/api/auth/me", "/api/attendance/current", "/api/holidays", "/api/holidays?start=2026-08-01&end=2026-12-31",
           "/api/departments", "/api/policy", "/api/me/wfo"] + [f"/api/me/wfo?month=2026-{m:02d}" for m in range(6, 13)]
    for eid in ids:
        base = f"/api/attendance/employees/{eid}"
        out.append(f"{base}/summary")
        for a, b in ranges:
            q = urlencode({"start": a, "end": b})
            out += [f"{base}/summary?{q}", f"{base}/months?{q}", f"{base}/days?{q}&page_size=200", f"{base}/days?{q}&order=asc&page=2&page_size=7",
                    f"{base}/days?{q}&status=ABSENT", f"{base}/days?{q}&exceptions_only=true&page_size=50"]
            out += [f"{base}/trends?{q}&granularity={g}" for g in ("day", "week", "month")]
        out += [f"{base}/days/2026-10-{d:02d}" for d in (1, 2, 5, 14, 31)] + [f"{base}/days/{today.isoformat()}", f"{base}/days/2026-12-25"]
    # validation and scope errors
    eid = ids[0]
    out += [f"/api/attendance/employees/{eid}/summary?start=2026-13-01", f"/api/attendance/employees/{eid}/summary?start=2026-10-05&end=2026-10-01",
            f"/api/attendance/employees/{eid}/summary?start=2025-01-01&end=2026-10-01", f"/api/attendance/employees/x/days?page=0&page_size=900",
            f"/api/attendance/employees/{eid}/days?status=NOPE&order=up&exceptions_only=maybe", f"/api/attendance/employees/{eid}/days/2026-02-30",
            "/api/attendance/employees/999999/summary", "/api/me/wfo?month=2026-13", "/api/me/wfo?month=26-10"]
    if role in ("MANAGER", "ADMIN"):
        for a, b in ranges[:5]:
            q = urlencode({"start": a, "end": b})
            out += [f"/api/dashboard/overview?{q}", f"/api/dashboard/overview?{q}&on_date=2026-10-14", f"/api/dashboard/trends?{q}",
                    f"/api/dashboard/trends?{q}&granularity=week", f"/api/dashboard/trends?{q}&granularity=month",
                    f"/api/dashboard/exceptions?{q}&page_size=100", f"/api/dashboard/exceptions?{q}&exception_type=SHORT_HOURS&page=2"]
            for sort in ("name", "code", "department", "check_in", "net_minutes", "average_net_minutes", "attendance_percentage", "status", "exceptions"):
                for order in ("asc", "desc"):
                    out.append(f"/api/dashboard/employees?{q}&sort={sort}&order={order}&page_size=200")
            out += [f"/api/dashboard/employees?{q}&search=a&page=2&page_size=5", f"/api/dashboard/employees?{q}&status=PRESENT&on_date=2026-10-14",
                    f"/api/dashboard/employees?{q}&include_inactive=true&page_size=200"]
        out += ["/api/team/members", "/api/team/members?search=a&limit=5", "/api/team/members?include_inactive=1"]
        out += [f"/api/team/members/{i}" for i in ids] + ["/api/team/members/999999"]
        out += ["/api/team/wfo"] + [f"/api/team/wfo?month=2026-{m:02d}" for m in range(6, 13)]
        out += [f"/api/team/wfo/employees/{i}?month=2026-10" for i in ids] + ["/api/team/wfo/employees/999999", "/api/team/wfo?employee_id=999999"]
        out += ["/api/dashboard/employees?sort=first_in", "/api/dashboard/trends?granularity=year", "/api/dashboard/overview?manager_id=999999"]
    else:
        out += ["/api/dashboard/overview", "/api/team/members", "/api/team/wfo", "/api/org/wfo"]
    if role == "ADMIN":
        for a, b in ranges[:4]:
            q = urlencode({"start": a, "end": b})
            out += [f"/api/dashboard/departments?{q}", f"/api/dashboard/managers?{q}", f"/api/dashboard/departments?{q}&on_date=2026-10-14"]
        out += ["/api/org/hierarchy", "/api/org/hierarchy?include_inactive=true", "/api/admin/wfo/policy"]
        out += ["/api/org/wfo"] + [f"/api/org/wfo?month=2026-{m:02d}" for m in range(6, 13)]
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--python", required=True)
    ap.add_argument("--dotnet", required=True)
    ap.add_argument("--admin", required=True, help="email of an active admin")
    ap.add_argument("--show", type=int, default=25, help="differences to print")
    args = ap.parse_args()

    admin_cookie, admin_me = login(args.python, args.admin)
    today = date.fromisoformat(call(args.python, "/api/attendance/current", admin_cookie)[1]["today"])
    members = call(args.python, "/api/team/members?include_inactive=true", admin_cookie)[1]
    managers = [m for m in members if m["role"] == "MANAGER" and m["is_active"]]
    employees = [m for m in members if m["role"] == "EMPLOYEE" and m["is_active"] and m["manager_id"]]
    personas = [("ADMIN", args.admin)]
    for role, people in (("MANAGER", managers), ("EMPLOYEE", employees)):
        if people:
            person = call(args.python, f"/api/team/members/{people[0]['id']}", admin_cookie)[1]
            personas.append((role, person))
    same = different = 0
    shown = 0
    for role, who in personas:
        if role == "ADMIN":
            cookie, me = admin_cookie, admin_me
        else:
            status, body, _ = call(args.python, "/api/auth/dev-login", method="POST", body={"email": who["employee_code"]})
            cookie, me = login(args.python, who["employee_code"]) if status == 200 else (None, None)
            if cookie is None:
                continue
        my_id = me["user"]["id"]
        in_scope = [m["id"] for m in call(args.python, "/api/team/members", cookie)[1]][:3] if role != "EMPLOYEE" else []
        out_of_scope = [m["id"] for m in members if m["id"] != my_id][:1] if role == "EMPLOYEE" else []
        for path in requests_for(me, [my_id, *in_scope, *out_of_scope], today, role):
            s1, b1, _ = call(args.python, path, cookie)
            s2, b2, _ = call(args.dotnet, path, cookie)
            problems = [("$status", s1, s2)] if s1 != s2 else []
            problems += diff(b1, b2) if isinstance(b1, (dict, list)) and isinstance(b2, (dict, list)) else ([] if b1 == b2 else [("$body", str(b1)[:80], str(b2)[:80])])
            if problems:
                different += 1
                if shown < args.show:
                    shown += 1
                    print(f"[{role}] {path}")
                    for p, x, y in problems[:6]:
                        print(f"    {p}: python={str(x)[:60]!r} dotnet={str(y)[:60]!r}")
            else:
                same += 1
    print(f"\n{same} identical, {different} different ({len(personas)} personas, today {today})")
    return 1 if different else 0


if __name__ == "__main__":
    sys.exit(main())
