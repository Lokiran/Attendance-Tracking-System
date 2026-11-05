"""End-to-end smoke test against a running ATS stack (seeded demo data).

Usage (PowerShell, backend + frontend running):
    $env:SEED_ADMIN_PASSWORD = "..."; $env:SEED_DEMO_PASSWORD = "..."
    backend\\.venv\\Scripts\\python.exe scripts\\smoke_test.py [--base http://127.0.0.1:5173]

It goes through the Vite proxy by default, exactly like the browser, and exercises login, CSRF,
check-in/out, scoping, dashboards, corrections, CSV export and logout. Each check prints PASS/FAIL.
"""

import argparse
import os
import sys
import time

import httpx

RESULTS: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    RESULTS.append((name, ok, detail))
    print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail and not ok else ""))
    return ok


class Session:
    def __init__(self, base: str):
        self.c = httpx.Client(base_url=base, timeout=30)
        self.csrf = None

    def login(self, email: str, password: str) -> httpx.Response:
        r = self.c.post("/api/auth/login", json={"email": email, "password": password})
        if r.status_code == 200:
            self.csrf = r.json()["csrf_token"]
        return r

    def get(self, path, **params):
        return self.c.get(path, params=params)

    def post(self, path, json=None, csrf=True):
        headers = {"X-CSRF-Token": self.csrf} if csrf and self.csrf else {}
        return self.c.post(path, json=json if json is not None else {}, headers=headers)


def wait_for(base: str) -> None:
    for _ in range(60):
        try:
            if httpx.get(base + "/api/health/db", timeout=2).status_code == 200:
                return
        except httpx.HTTPError:
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
    index = httpx.get(args.base + "/")
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

    # --- check-in / check-out
    cur = emp.get("/api/attendance/current").json()
    if cur["active_session"]:
        emp.post("/api/attendance/check-out", {"break_minutes": 0})
    check("state change without CSRF rejected", emp.post("/api/attendance/check-in", csrf=False).status_code == 403)
    r = emp.post("/api/attendance/check-in")
    check("check-in", r.status_code == 201, r.text[:200])
    check("duplicate check-in rejected", emp.post("/api/attendance/check-in").status_code == 409)
    cur = emp.get("/api/attendance/current").json()
    check("current shows active session", cur["active_session"] is not None and cur["today_record"]["status"] == "PRESENT")
    r = emp.post("/api/attendance/check-out", {"break_minutes": 0})
    check("check-out", r.status_code == 200 and r.json()["status"] == "COMPLETED", r.text[:200])
    check("check-out without session rejected", emp.post("/api/attendance/check-out").status_code == 409)

    today = cur["today"]
    month_start = today[:8] + "01"
    s = emp.get(f"/api/attendance/employees/{me['id']}/summary", start=month_start, end=today).json()
    check("summary has real figures", s["expected_days"] > 0 and s["net_minutes"] > 0 and s["attendance_percentage"] is not None, str(s)[:200])
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
    check("manager sees only own reports", names == ["Farah Khan", "Rahul Reddy", "Sneha Rao"], str(names))
    ov = mgr.get("/api/dashboard/overview", start=month_start, end=today).json()
    snap = ov["snapshot"]
    check("snapshot categories sum to team size",
          sum(snap[k] for k in ("present", "absent", "not_checked_in", "on_leave", "incomplete", "off")) == ov["headcount"])
    pending = mgr.get("/api/corrections", scope="review", status="PENDING").json()
    check("manager sees pending corrections", pending["total"] >= 1, str(pending)[:200])

    # --- admin
    adm = Session(args.base)
    check("admin login", adm.login(admin_email, admin_pw).status_code == 200)
    users = adm.get("/api/admin/users", page_size=100).json()
    arjun = next(u for u in users["items"] if u["full_name"] == "Arjun Kumar")
    check("manager cannot read unrelated employee", mgr.get(f"/api/attendance/employees/{arjun['id']}/summary").status_code == 404)
    org = adm.get("/api/dashboard/overview", start=month_start, end=today).json()
    check("admin org headcount", org["headcount"] == 12, str(org["headcount"]))
    depts = adm.get("/api/dashboard/departments", start=month_start, end=today).json()
    check("department comparison", len(depts) >= 4)
    managers = adm.get("/api/dashboard/managers", start=month_start, end=today).json()
    vikram = next(m for m in managers if m["manager_name"] == "Vikram Desai")
    check("nested manager subtree", vikram["team_size"] > vikram["direct_reports"])
    hier = adm.get("/api/org/hierarchy").json()
    check("hierarchy", len(hier["departments"]) >= 4)
    audit = adm.get("/api/admin/audit-logs", page_size=5).json()
    check("audit log", audit["total"] > 0)

    # --- correction approval
    if pending["items"]:
        cid = pending["items"][0]["id"]
        r = mgr.post(f"/api/corrections/{cid}/approve", {"comment": "Smoke test approval"})
        check("manager approves correction", r.status_code == 200 and r.json()["status"] == "APPROVED", r.text[:200])

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
