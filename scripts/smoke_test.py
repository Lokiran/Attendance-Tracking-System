r"""End-to-end smoke test against a running ATS stack (seeded demo data).

Usage (PowerShell, API + frontend running, database seeded with `dotnet run --project src\ATS.Api -- seed`):
    $env:SEED_ADMIN_PASSWORD = "..."; $env:SEED_DEMO_PASSWORD = "..."
    python scripts\smoke_test.py [--base http://127.0.0.1:5173]

It goes through the Vite proxy by default, exactly like the browser, and exercises login, CSRF,
timesheet-based attendance, scoping, dashboards, a timesheet import preview, CSV export and logout. Each check prints PASS/FAIL.
Standard library only, so any Python 3.11+ runs it.
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid
from datetime import date, timedelta
from email.message import Message
from http.cookies import SimpleCookie

RESULTS: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    RESULTS.append((name, ok, detail))
    print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail and not ok else ""))
    return ok


class Response:
    def __init__(self, status: int, body: bytes, headers: Message):
        self.status_code, self.content, self.headers = status, body, headers

    @property
    def text(self) -> str:
        return self.content.decode("utf-8", errors="replace")

    def json(self):
        return json.loads(self.content)


def request(url: str, method: str = "GET", data: bytes | None = None, headers: dict | None = None,
            timeout: float = 30) -> Response:
    req = urllib.request.Request(url, data=data, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return Response(r.status, r.read(), r.headers)
    except urllib.error.HTTPError as e:
        return Response(e.code, e.read(), e.headers)


class Session:
    def __init__(self, base: str):
        self.base, self.cookies, self.csrf = base, SimpleCookie(), None

    def _call(self, method: str, path: str, data: bytes | None = None, headers: dict | None = None) -> Response:
        headers = dict(headers or {})
        if self.cookies:
            headers["Cookie"] = "; ".join(f"{k}={m.value}" for k, m in self.cookies.items())
        r = request(self.base + path, method, data, headers)
        for value in r.headers.get_all("Set-Cookie") or []:
            self.cookies.load(value)
            for k, m in list(self.cookies.items()):
                if m["max-age"] == "0" or not m.value:
                    del self.cookies[k]
        return r

    def login(self, email: str, password: str) -> Response:
        r = self._call("POST", "/api/auth/login", json.dumps({"email": email, "password": password}).encode(),
                       {"Content-Type": "application/json"})
        if r.status_code == 200:
            self.csrf = r.json()["csrf_token"]
        return r

    def get(self, path, **params):
        query = "&".join(f"{k}={v}" for k, v in params.items())
        return self._call("GET", path + (f"?{query}" if query else ""))

    def post(self, path, json_body=None, csrf=True):
        headers = {"Content-Type": "application/json"}
        if csrf and self.csrf:
            headers["X-CSRF-Token"] = self.csrf
        return self._call("POST", path, json.dumps(json_body if json_body is not None else {}).encode(), headers)

    def upload(self, path, filename: str, content: bytes):
        boundary = uuid.uuid4().hex
        head = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\n'
                'Content-Type: text/csv\r\n\r\n')
        body = head.encode() + content + f"\r\n--{boundary}--\r\n".encode()
        return self._call("POST", path, body, {"Content-Type": f"multipart/form-data; boundary={boundary}",
                                               "X-CSRF-Token": self.csrf or ""})


def wait_for(base: str) -> None:
    for _ in range(60):
        try:
            if request(base + "/api/health/db", timeout=2).status_code == 200:
                return
        except OSError:
            pass
        time.sleep(1)
    sys.exit(f"Stack at {base} did not become healthy")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="http://127.0.0.1:5173")
    args = ap.parse_args()
    demo_pw = os.environ["SEED_DEMO_PASSWORD"]
    admin_pw = os.environ["SEED_ADMIN_PASSWORD"]
    admin_email = os.environ.get("SEED_ADMIN_EMAIL", "admin@nexa.example")
    wait_for(args.base)

    # --- frontend served
    index = request(args.base + "/")
    check("frontend index served", index.status_code == 200 and '<div id="root">' in index.text)

    # --- authentication
    anon = Session(args.base)
    check("unauthenticated /me rejected", anon.get("/api/auth/me").status_code == 401)
    bad = anon.login("rahul.reddy@nexa.example", "wrong-password-1")
    check("wrong password rejected", bad.status_code == 401)

    emp = Session(args.base)
    r = emp.login("rahul.reddy@nexa.example", demo_pw)
    check("employee login", r.status_code == 200, r.text[:200])
    me = r.json()["user"]
    check("profile has manager + department", me["manager"]["full_name"] == "Priya Sharma" and me["department"]["name"] == "AI Engineering")
    check("no password hash in profile", "password" not in r.text)
    cookie = r.headers.get("set-cookie", "")
    check("session cookie is HttpOnly", "ats_session=" in cookie and "httponly" in cookie.lower())

    # --- attendance comes from imported timesheets (no check-in / check-out)
    cur = emp.get("/api/attendance/current").json()
    check("state change without CSRF rejected", emp.post("/api/auth/logout", csrf=False).status_code == 403)
    check("check-in is gone", emp.post("/api/attendance/check-in").status_code in (404, 405))
    check("corrections are gone", emp.get("/api/corrections").status_code == 404)
    check("timesheets imported", cur["timesheet_through"] is not None and cur["my_timesheet_through"] is not None, str(cur)[:200])

    today = cur["today"]
    # The seeded timesheets reach yesterday; a 30-day window always holds imported days.
    month_start = (date.fromisoformat(today) - timedelta(days=30)).isoformat()
    s = emp.get(f"/api/attendance/employees/{me['id']}/summary", start=month_start, end=today).json()
    check("summary has real figures", s["expected_days"] > 0 and s["present_days"] > 0 and s["hours_days"] > 0
          and s["attendance_percentage"] is not None, str(s)[:200])
    days = emp.get(f"/api/attendance/employees/{me['id']}/days", start=month_start, end=today, page_size=5).json()
    check("history paginated", len(days["items"]) <= 5 and days["total"] >= len(days["items"]))
    trends = emp.get(f"/api/attendance/employees/{me['id']}/trends", granularity="month").json()
    check("employee trends", trends["items"] and trends["items"][-1]["label"])

    # --- employee restrictions
    check("employee blocked from team dashboard", emp.get("/api/dashboard/overview").status_code == 403)
    check("employee blocked from admin", emp.get("/api/admin/users").status_code == 403)

    # --- manager scope
    mgr = Session(args.base)
    check("manager login", mgr.login("priya.sharma@nexa.example", demo_pw).status_code == 200)
    rows = mgr.get("/api/dashboard/employees", start=month_start, end=today, page_size=50).json()["items"]
    names = sorted(x["full_name"] for x in rows)
    check("manager sees only own reports", names == ["Farah Khan", "Neha Joshi", "Rahul Reddy", "Sneha Rao"], str(names))
    ov = mgr.get("/api/dashboard/overview", start=month_start, end=today).json()
    snap = ov["snapshot"]
    check("snapshot categories sum to team size",
          sum(snap[k] for k in ("present", "wfh", "absent", "leave", "no_timesheet", "off")) == ov["headcount"])
    check("manager cannot import timesheets", mgr.get("/api/admin/attendance-uploads").status_code == 403)

    # --- admin
    adm = Session(args.base)
    check("admin login", adm.login(admin_email, admin_pw).status_code == 200)
    users = adm.get("/api/admin/users", page_size=100).json()
    arjun = next(u for u in users["items"] if u["full_name"] == "Arjun Kumar")
    check("manager cannot read unrelated employee", mgr.get(f"/api/attendance/employees/{arjun['id']}/summary").status_code == 404)
    org = adm.get("/api/dashboard/overview", start=month_start, end=today).json()
    check("admin org headcount", org["headcount"] == 14, str(org["headcount"]))  # 15 demo users minus the untracked admin
    depts = adm.get("/api/dashboard/departments", start=month_start, end=today).json()
    check("department comparison", len(depts) >= 4)
    managers = adm.get("/api/dashboard/managers", start=month_start, end=today).json()
    vikram = next(m for m in managers if m["manager_name"] == "Vikram Desai")
    check("nested manager subtree", vikram["team_size"] > vikram["direct_reports"])
    hier = adm.get("/api/org/hierarchy").json()
    check("hierarchy", len(hier["departments"]) >= 4)
    audit = adm.get("/api/admin/audit-logs", page_size=5).json()
    check("audit log", audit["total"] > 0)

    # --- timesheet import preview (discarded, so the data is unchanged)
    csv_body = f"Employee ID,Date\n{me['employee_code']},{month_start}\n".encode()
    r = adm.upload("/api/admin/attendance-uploads", "smoke.csv", csv_body)
    check("timesheet preview", r.status_code == 201 and r.json()["summary"]["presence_list"], r.text[:200])
    if r.status_code == 201:
        d = adm.post(f"/api/admin/attendance-uploads/{r.json()['id']}/discard")
        check("timesheet preview discarded", d.status_code == 200 and d.json()["status"] == "DISCARDED")

    # --- CSV export
    r = mgr.get("/api/reports/team-attendance.csv", start=month_start, end=today)
    check("CSV export", r.status_code == 200 and r.headers["content-type"].startswith("text/csv") and "Rahul Reddy" in r.text)
    check("CSV scope enforced", "Arjun Kumar" not in r.text)

    # --- logout
    check("logout", emp.post("/api/auth/logout").status_code == 200)
    check("session revoked after logout", emp.get("/api/auth/me").status_code == 401)

    failed = [n for n, ok, _ in RESULTS if not ok]
    print(f"\n{len(RESULTS) - len(failed)}/{len(RESULTS)} checks passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
