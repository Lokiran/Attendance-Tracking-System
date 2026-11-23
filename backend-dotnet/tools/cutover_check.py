"""Read-only check of a running API on real data.

Signs in with a real account, reads the health endpoints, the signed-in person's attendance and (for a manager or an
administrator) the dashboards, CSV reports, WFO views and administration lists, then signs out. With --compare it sends
the same reads to a second deployment on the same database (for example a new version next to the current one) and
reports any response that differs.

    $env:ATS_CHECK_EMAIL = "you@company.example"        # the password is asked for (or ATS_CHECK_PASSWORD)
    python backend-dotnet/tools/cutover_check.py --base http://127.0.0.1:8001 [--compare http://127.0.0.1:8002]

Nothing is changed except what any sign-in writes: a session row and the LOGIN / LOGOUT audit entries.
The password is never printed or logged. Exit code 1 when a check fails or a compared response differs.
Standard library only, so any Python 3.11+ runs it.
"""
import argparse
import getpass
import json
import os
import sys
import urllib.error
import urllib.request
from datetime import date, timedelta
from http.cookies import SimpleCookie

IGNORED = {"generated_at", "server_time", "request_id", "csrf_token"}  # csrf_token belongs to each sign-in
FAILED: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail and not ok else ""))
    if not ok:
        FAILED.append(name)
    return ok


class Client:
    def __init__(self, base: str):
        self.base, self.cookie, self.csrf = base.rstrip("/"), None, None

    def call(self, path: str, method: str = "GET", body: dict | None = None):
        data = json.dumps(body).encode() if body is not None else (b"{}" if method == "POST" else None)
        req = urllib.request.Request(self.base + path, data=data, method=method)
        if data is not None:
            req.add_header("Content-Type", "application/json")
        if self.cookie:
            req.add_header("Cookie", self.cookie)
        if self.csrf and method != "GET":
            req.add_header("X-CSRF-Token", self.csrf)
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                raw, status, headers = r.read(), r.status, r.headers
        except urllib.error.HTTPError as e:
            raw, status, headers = e.read(), e.code, e.headers
        if "json" in headers.get("Content-Type", "") and raw:
            return status, json.loads(raw), headers
        return status, raw, headers

    def login(self, email: str, password: str) -> dict | None:
        status, body, headers = self.call("/api/auth/login", "POST", {"email": email, "password": password})
        if status != 200:
            return None
        jar = SimpleCookie()
        for value in headers.get_all("Set-Cookie") or []:
            jar.load(value)
        self.cookie = "; ".join(f"{k}={m.value}" for k, m in jar.items())
        self.csrf = body.get("csrf_token")
        return body["user"]


def strip(value):
    if isinstance(value, dict):
        return {k: strip(v) for k, v in value.items() if k not in IGNORED}
    if isinstance(value, list):
        return [strip(v) for v in value]
    return value


def media(headers) -> str:
    """The media type without parameters (deployments may differ in the charset parameter)."""
    return headers.get("Content-Type", "").split(";")[0].strip().lower()


def reads(user: dict) -> list[str]:
    today = date.today()
    start, end = today.replace(day=1) - timedelta(days=31), today
    rng = f"start={start.replace(day=1).isoformat()}&end={end.isoformat()}"
    month = today.strftime("%Y-%m")
    paths = ["/api/auth/me", "/api/attendance/current", "/api/holidays", "/api/policy", "/api/departments",
             f"/api/attendance/employees/{user['id']}/summary?{rng}",
             f"/api/attendance/employees/{user['id']}/days?{rng}&page_size=50",
             f"/api/attendance/employees/{user['id']}/trends?granularity=month",
             f"/api/me/wfo?month={month}"]
    if user["role"] in ("MANAGER", "ADMIN"):
        paths += [f"/api/dashboard/overview?{rng}", f"/api/dashboard/employees?{rng}&page_size=100",
                  f"/api/dashboard/trends?{rng}", f"/api/dashboard/exceptions?{rng}", f"/api/team/wfo?month={month}",
                  f"/api/reports/team-attendance.csv?{rng}",
                  f"/api/reports/monthly-attendance.csv?year={today.year}&month={today.month}"]
    if user["role"] == "ADMIN":
        paths += [f"/api/dashboard/departments?{rng}", f"/api/dashboard/managers?{rng}", "/api/org/hierarchy",
                  f"/api/org/wfo?month={month}", "/api/admin/users?page_size=100", "/api/admin/wfo/policy",
                  "/api/admin/attendance-uploads"]
    return paths


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:8001", help="the API to check (default: the .NET API)")
    ap.add_argument("--compare", help="a second deployment on the same database")
    args = ap.parse_args()
    email = os.environ.get("ATS_CHECK_EMAIL") or input("Email: ").strip()
    password = os.environ.get("ATS_CHECK_PASSWORD") or getpass.getpass("Password (not shown): ")

    api = Client(args.base)
    status, health, _ = api.call("/api/health")
    check("health", status == 200 and isinstance(health, dict) and health.get("status") == "ok", str(health)[:200])
    status, db, _ = api.call("/api/health/db")
    check("database reachable", status == 200, str(db)[:200])
    if isinstance(db, dict):
        print(f"     database: {db.get('database')}")
    status, _, _ = api.call("/api/auth/providers")
    check("sign-in providers", status == 200)
    check("signed-out /me is 401", api.call("/api/auth/me")[0] == 401)

    user = api.login(email, password)
    if not check("sign in", user is not None):
        return 1
    print(f"     signed in as role {user['role']}")
    other = None
    if args.compare:
        other = Client(args.compare)
        if not check("sign in on the compared backend", other.login(email, password) is not None):
            other = None

    same = 0
    for path in reads(user):
        status, body, headers = api.call(path)
        check(f"GET {path.split('?')[0]}", status == 200, f"status {status}")
        if other is not None:
            o_status, o_body, o_headers = other.call(path)
            equal = status == o_status and strip(body) == strip(o_body) and media(headers) == media(o_headers)
            same += equal
            if not equal:
                check(f"same response from both backends: {path}", False, f"status {status} vs {o_status}")

    for client in filter(None, (api, other)):
        check(f"sign out ({client.base})", client.call("/api/auth/logout", "POST")[0] == 200)
        check(f"session ended ({client.base})", client.call("/api/auth/me")[0] == 401)
    if other is not None:
        print(f"\n{same}/{len(reads(user))} compared responses identical")
    print(f"{'All checks passed' if not FAILED else f'{len(FAILED)} check(s) failed'}")
    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())
