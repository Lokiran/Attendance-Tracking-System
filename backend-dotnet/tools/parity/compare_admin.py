"""Compare the Python and .NET backends on the administration endpoints (phase 6): users, departments, schedules,
holidays, policy, audit log, office sites, devices (and the device import) and the automatic-attendance settings.

Like compare_writes.py, each backend runs on its OWN copy of the same snapshot (for example two databases loaded with
`seed`), with DEV_LOGIN_ENABLED=true. The same requests go to both in the same order; responses are compared (ids of
new rows, timestamps and request ids aside), then the resulting tables.

    python backend-dotnet/tools/parity/compare_admin.py --python http://127.0.0.1:8010 --dotnet http://127.0.0.1:8011 \
        --admin admin@nexa.example --manager priya.sharma@nexa.example --python-db A.sqlite3 --dotnet-db B.sqlite3
"""
import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare_writes import Side, diff, normalise_json, table  # noqa: E402

TABLES = {
    "users": "select id, employee_code, full_name, email, role, job_title, department_id, manager_id, is_active, attendance_tracked, date_joined from users order by id",
    "departments": "select id, name, description, department_head_id from departments order by id",
    "work_schedules": "select id, name, employee_id, department_id, start_time, end_time, required_minutes, working_days, effective_from, effective_to from work_schedules order by id",
    "holidays": "select id, name, holiday_date, department_id from holidays order by id",
    "attendance_policy": "select * from attendance_policy",
    "office_sites": "select id, name, code, network_name, address, is_active from office_sites order by id",
    "devices": "select id, serial_number, asset_tag, mac_address, hostname, model, user_id, is_active, source, notes from devices order by id",
    "user_sessions_revoked": "select user_id, revoked_at is not null as revoked from user_sessions where user_id not in (select id from users where email like 'admin@%') order by user_id, id",
    "audit_logs": "select actor_user_id, action, entity_type, entity_id, before_values, after_values from audit_logs where action not in ('LOGIN_SUCCEEDED', 'LOGIN_FAILED', 'LOGOUT', 'DEV_LOGIN') order by id",
}
SKIP_COLUMNS = {"updated_at", "updated_by_id"}


def main() -> int:
    ap = argparse.ArgumentParser()
    for name in ("--python", "--dotnet", "--admin", "--manager", "--python-db", "--dotnet-db"):
        ap.add_argument(name, required=True)
    args = ap.parse_args()
    py, net = Side(args.python, args.admin), Side(args.dotnet, args.admin)
    same = different = 0

    def both(label, method, path, sides=(None, None), **kw):
        nonlocal same, different
        a, b = sides if sides[0] else (py, net)
        files = kw.pop("files_factory", None)
        s1, b1 = a.send(method, path, **({**kw, "files": files()} if files else kw))
        s2, b2 = b.send(method, path, **({**kw, "files": files()} if files else kw))
        problems = ([("$status", s1, s2)] if s1 != s2 else []) + (diff(b1, b2) if isinstance(b1, (dict, list)) else [])
        if problems:
            different += 1
            print(f"[{label}] {method} {path}")
            for p, x, y in problems[:8]:
                print(f"    {p}: python={str(x)[:110]!r} dotnet={str(y)[:110]!r}")
        else:
            same += 1
        return s1, b1

    get = lambda label, path, **kw: both(label, "GET", path, **kw)  # noqa: E731

    # ---------------------------------------------------------------- reads and query validation
    for q in ("", "?page_size=5", "?page=2&page_size=4", "?search=sharma", "?search=EMP0", "?role=MANAGER", "?is_active=true", "?is_active=0",
              "?department_id=1", "?manager_id=3", "?page=0&page_size=500&role=NOPE&is_active=maybe", "?search=%25"):
        get("users", f"/api/admin/users{q}")
    for path in ("/api/admin/users/1", "/api/admin/users/999", "/api/admin/users/x", "/api/managers", "/api/admin/schedules", "/api/holidays",
                 "/api/departments", "/api/policy", "/api/admin/sites", "/api/admin/devices", "/api/admin/devices/summary",
                 "/api/admin/auto-attendance/settings", "/api/admin/audit-logs", "/api/admin/audit-logs?action=USER_CREATED&page_size=3",
                 "/api/admin/audit-logs?search=seed", "/api/admin/audit-logs?entity_type=attendance_upload", "/api/admin/audit-logs?start=2026-01-01&end=2030-01-01",
                 "/api/admin/audit-logs?actor_id=x&start=nope", "/api/admin/devices?active=false", "/api/admin/devices?assigned=maybe"):
        get("read", path)

    # ---------------------------------------------------------------- users
    new_user = {"employee_code": " emp100 ", "full_name": " Test Person ", "email": "Test.Person@Nexa.Example", "password": "Str0ngPassw0rd!",
                "department_id": 1, "manager_id": 3, "job_title": "Analyst", "date_joined": "2026-06-01"}
    both("create user", "POST", "/api/admin/users", json=new_user)
    both("dup email", "POST", "/api/admin/users", json={**new_user, "employee_code": "EMP101"})
    both("dup code", "POST", "/api/admin/users", json={**new_user, "email": "other@nexa.example"})
    both("weak pw", "POST", "/api/admin/users", json={**new_user, "email": "w@nexa.example", "employee_code": "EMP102", "password": "short"})
    both("bad fields", "POST", "/api/admin/users", json={"employee_code": "-x", "full_name": "A", "email": "nope", "password": "", "role": "BOSS",
                                                         "department_id": "abc", "attendance_tracked": "perhaps", "date_joined": "2026-02-30"})
    both("missing", "POST", "/api/admin/users", json={})
    both("not object", "POST", "/api/admin/users", json=[1, 2])
    both("bad manager", "POST", "/api/admin/users", json={**new_user, "email": "m@nexa.example", "employee_code": "EMP103", "manager_id": 6})
    both("no dept", "POST", "/api/admin/users", json={**new_user, "email": "d@nexa.example", "employee_code": "EMP104", "department_id": 999})
    users = {u["email"]: u["id"] for u in py.send("GET", "/api/admin/users?page_size=200")[1]["items"]}
    tp = users["test.person@nexa.example"]
    for body in ({"job_title": None, "full_name": " Renamed Person "}, {"manager_id": 2}, {"role": "MANAGER"}, {"is_active": False}, {"is_active": True},
                 {"email": "admin@nexa.example"}, {"employee_code": "emp001"}, {"manager_id": tp}, {"department_id": None, "date_joined": None},
                 {"attendance_tracked": False, "employee_code": "EMP100X"}, {"role": "NOPE", "is_active": "x", "email": "bad"}):
        both("patch user", "PATCH", f"/api/admin/users/{tp}", json=body)
    both("cycle", "PATCH", "/api/admin/users/2", json={"manager_id": 3})
    both("self role", "PATCH", "/api/admin/users/1", json={"role": "EMPLOYEE"})
    both("self off", "PATCH", "/api/admin/users/1", json={"is_active": False})
    both("reports off", "PATCH", "/api/admin/users/3", json={"is_active": False})
    both("reports role", "PATCH", "/api/admin/users/3", json={"role": "EMPLOYEE"})
    both("patch 404", "PATCH", "/api/admin/users/999", json={"full_name": "Nobody Here"})
    both("reset pw", "POST", f"/api/admin/users/{tp}/reset-password", json={"password": "An0therPassw0rd"})
    both("reset weak", "POST", f"/api/admin/users/{tp}/reset-password", json={"password": "abc"})
    both("reset none", "POST", f"/api/admin/users/{tp}/reset-password", json={})

    # ---------------------------------------------------------------- departments
    both("dept", "POST", "/api/admin/departments", json={"name": " Finance ", "description": "Money", "department_head_id": 2})
    both("dept dup", "POST", "/api/admin/departments", json={"name": "finance"})
    both("dept bad head", "POST", "/api/admin/departments", json={"name": "Legal", "department_head_id": 999})
    both("dept short", "POST", "/api/admin/departments", json={"name": "L"})
    depts = {d["name"]: d["id"] for d in py.send("GET", "/api/departments")[1]}
    fin = depts["Finance"]
    both("dept patch", "PATCH", f"/api/admin/departments/{fin}", json={"description": None, "department_head_id": None})
    both("dept rename", "PATCH", f"/api/admin/departments/{fin}", json={"name": "AI Engineering"})
    both("dept same", "PATCH", f"/api/admin/departments/{fin}", json={})
    both("dept del busy", "DELETE", f"/api/admin/departments/{depts['Operations']}")
    both("dept del", "DELETE", f"/api/admin/departments/{fin}")
    both("dept del 404", "DELETE", f"/api/admin/departments/{fin}")

    # ---------------------------------------------------------------- schedules, holidays, policy
    sched = {"name": "Late shift", "employee_id": tp, "start_time": "13:00", "end_time": "22:00", "required_minutes": 480,
             "working_days": [5, 1, 3], "effective_from": "2026-01-01"}
    both("sched", "POST", "/api/admin/schedules", json=sched)
    both("sched overlap", "POST", "/api/admin/schedules", json={**sched, "effective_from": "2026-06-01"})
    both("sched both", "POST", "/api/admin/schedules", json={**sched, "department_id": 1})
    both("sched days", "POST", "/api/admin/schedules", json={**sched, "employee_id": 6, "working_days": [1, 1]})
    both("sched equal", "POST", "/api/admin/schedules", json={**sched, "employee_id": 6, "end_time": "13:00"})
    both("sched invalid", "POST", "/api/admin/schedules", json={"name": "X", "start_time": "25:00", "end_time": "9", "required_minutes": 0,
                                                                "working_days": [], "effective_from": "x"})
    sid = max(s["id"] for s in py.send("GET", "/api/admin/schedules")[1])
    both("sched patch", "PATCH", f"/api/admin/schedules/{sid}", json={"working_days": [2, 4], "effective_to": "2026-12-31"})
    both("sched noop", "PATCH", f"/api/admin/schedules/{sid}", json={})
    both("sched bad to", "PATCH", f"/api/admin/schedules/{sid}", json={"effective_to": "2025-01-01"})
    both("sched list", "GET", "/api/admin/schedules")
    both("sched del", "DELETE", f"/api/admin/schedules/{sid}")
    both("holiday", "POST", "/api/admin/holidays", json={"name": " Test Day ", "holiday_date": "2026-12-24"})
    both("holiday dup", "POST", "/api/admin/holidays", json={"name": "Again", "holiday_date": "2026-12-24"})
    both("holiday dept", "POST", "/api/admin/holidays", json={"name": "Dept Day", "holiday_date": "2026-12-24", "department_id": 999})
    hid = max(h["id"] for h in py.send("GET", "/api/holidays")[1])
    both("holiday del", "DELETE", f"/api/admin/holidays/{hid}")
    both("holiday del 404", "DELETE", f"/api/admin/holidays/{hid}")
    both("policy", "PUT", "/api/admin/policy", json={"late_grace_minutes": 20, "notes": "Reviewed", "manager_can_approve": None})
    both("policy bad", "PUT", "/api/admin/policy", json={"max_session_minutes": 30, "correction_window_days": 0})

    # ---------------------------------------------------------------- sites and devices
    both("site", "POST", "/api/admin/sites", json={"name": " Head Office ", "code": "hq", "network_name": "  Corp   WiFi ", "address": " 1 Main St "})
    both("site dup", "POST", "/api/admin/sites", json={"name": "head office", "code": "HQ2"})
    both("site code", "POST", "/api/admin/sites", json={"name": "Branch", "code": "b r"})
    site = py.send("GET", "/api/admin/sites")[1][0]["id"]
    both("site patch", "PATCH", f"/api/admin/sites/{site}", json={"is_active": False, "address": "", "name": None})
    both("sites", "GET", "/api/admin/sites")
    both("device", "POST", "/api/admin/devices", json={"serial_number": " pf3abc12 ", "mac_address": "AA-BB-CC-DD-EE-01", "user_id": 6, "model": " ThinkPad  T14 "})
    both("device random", "POST", "/api/admin/devices", json={"serial_number": "PF3ABC13", "mac_address": "02bb.ccdd.ee02"})
    both("device dup", "POST", "/api/admin/devices", json={"serial_number": "PF3ABC12"})
    both("device mac", "POST", "/api/admin/devices", json={"mac_address": "zz:zz"})
    both("device empty", "POST", "/api/admin/devices", json={"serial_number": "To be filled by O.E.M."})
    devs = py.send("GET", "/api/admin/devices?page_size=200")[1]["items"]
    did = devs[0]["id"]
    both("device patch", "PATCH", f"/api/admin/devices/{did}", json={"notes": " spare ", "user_id": None, "is_active": None})
    both("device clear", "PATCH", f"/api/admin/devices/{did}", json={"serial_number": None, "mac_address": None})
    both("device owner", "PATCH", f"/api/admin/devices/{did}", json={"user_id": 999})
    csv = ("Serial Number,Wi-Fi MAC,Primary user UPN,Device name,Model\n"
           "PF3ABC12,aa:bb:cc:dd:ee:01,rahul.reddy@nexa.example,LT-001,ThinkPad T14\n"
           "PF3XYZ99,AA:BB:CC:DD:EE:03,sneha.rao@nexa.example,LT-002,\n"
           "PF3XYZ99,AA:BB:CC:DD:EE:04,,,\n"
           "NEW00001,06:11:22:33:44:55,nobody@nexa.example,,\n"
           ",,,,\n"
           "BAD00001,not-a-mac,,,\n"
           "PF3ABC13,aa:bb:cc:dd:ee:01,,,\n").encode()
    for params in ({}, {"apply": "true"}, {"apply": "true"}):
        both("import", "POST", "/api/admin/devices/import", params=params, files_factory=lambda: {"file": ("intune.csv", csv, "text/csv")})
    both("import bad", "POST", "/api/admin/devices/import", files_factory=lambda: {"file": ("x.csv", b"Name,Day\nA,B\n", "text/csv")})
    both("import type", "POST", "/api/admin/devices/import", files_factory=lambda: {"file": ("x.pdf", b"%PDF", "application/pdf")})
    for q in ("", "?search=pf3", "?search=AABBCC", "?search=rahul", "?assigned=true", "?has_mac=false", "?active=false"):
        get("devices", f"/api/admin/devices{q}")
    get("summary", "/api/admin/devices/summary")

    # ---------------------------------------------------------------- automatic attendance settings
    both("auto", "PUT", "/api/admin/auto-attendance/settings", json={"gap_minutes": 45, "wfh_presence_states": [" Busy ", "Available", "Busy", ""]})
    both("auto same", "PUT", "/api/admin/auto-attendance/settings", json={"wfh_presence_states": ["Busy", "Available"]})
    both("auto half", "PUT", "/api/admin/auto-attendance/settings", json={"half_day_minutes": 500, "full_day_minutes": 480})
    both("auto bad", "PUT", "/api/admin/auto-attendance/settings", json={"gap_minutes": 0, "wfh_presence_states": ["x" * 40]})
    both("auto empty", "PUT", "/api/admin/auto-attendance/settings", json={"wfh_presence_states": []})

    # ---------------------------------------------------------------- the audit log after all that, and a manager's view
    for q in ("?page_size=50", "?action=USER_UPDATED", "?entity_type=device", "?search=renamed"):
        get("audit", f"/api/admin/audit-logs{q}")
    mpy, mnet = Side(args.python, args.manager), Side(args.dotnet, args.manager)
    for method, path, body in (("GET", "/api/admin/users", None), ("POST", "/api/admin/users", new_user), ("GET", "/api/admin/devices", None),
                               ("PUT", "/api/admin/auto-attendance/settings", {}), ("GET", "/api/managers", None)):
        both("manager", method, path, sides=(mpy, mnet), **({"json": body} if body is not None else {}))

    for name, sql in TABLES.items():
        rows = []
        for db in (args.python_db, args.dotnet_db):
            rows.append([{k: normalise_json(v) for k, v in r.items() if k not in SKIP_COLUMNS} for r in table(db, sql)])
        problems = diff(rows[0], rows[1], name)
        if problems:
            different += 1
            print(f"[table] {name}: {len(problems)} differences")
            for p, x, y in problems[:8]:
                print(f"    {p}: python={str(x)[:110]!r} dotnet={str(y)[:110]!r}")
        else:
            same += 1
            print(f"[table] {name}: {len(rows[0])} rows identical")
    print(f"\n{same} identical, {different} different")
    return 1 if different else 0


if __name__ == "__main__":
    sys.exit(main())
