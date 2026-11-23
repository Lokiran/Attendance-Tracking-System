"""Compare the Python and .NET backends on writes (docs/dotnet-migration-plan.md §11): timesheet uploads (preview,
create-employees, commit, discard), day overrides and the WFO policy.

Each backend must run on its OWN copy of the same database snapshot (writes change it), with DEV_LOGIN_ENABLED=true.
The same requests are sent to both in the same order; responses are compared, then the resulting tables (ignoring ids
of new rows, timestamps, request ids and password hashes).

    python backend-dotnet/tools/parity/compare_writes.py --python http://127.0.0.1:8010 --dotnet http://127.0.0.1:8011 \
        --admin admin@nexa.example --python-db A.sqlite3 --dotnet-db B.sqlite3 FILE [FILE ...]

Files are uploaded in the order given (CSV, Excel or PDF). Exit code 1 when anything differs.
"""
import argparse
import json
import mimetypes
import sqlite3
import sys
from pathlib import Path

import httpx

IGNORED = {"generated_at", "server_time", "request_id", "created_at", "committed_at", "overridden_at", "updated_at", "computed_at"}


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
    elif a != b:
        out.append((path, a, b))
    return out


class Side:
    def __init__(self, base: str, admin: str):
        self.client = httpx.Client(base_url=base, timeout=300)
        r = self.client.post("/api/auth/dev-login", json={"email": admin})
        r.raise_for_status()
        self.csrf = r.json()["csrf_token"]

    def send(self, method: str, path: str, **kw):
        headers = {"X-CSRF-Token": self.csrf} if method != "GET" else {}
        r = self.client.request(method, path, headers=headers, **kw)
        body = r.json() if "json" in r.headers.get("content-type", "") else r.text
        return r.status_code, body


def table(db: str, sql: str) -> list[dict]:
    con = sqlite3.connect(db)
    con.row_factory = sqlite3.Row
    try:
        return [dict(r) for r in con.execute(sql)]
    finally:
        con.close()


def normalise_json(v):
    if v == "null":
        return None  # SQLAlchemy writes JSON null as the text 'null', EF Core as SQL NULL (parity note P11)
    if isinstance(v, str) and v[:1] in "[{":
        try:
            return json.loads(v)
        except ValueError:
            return v
    return v


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--python", required=True)
    ap.add_argument("--dotnet", required=True)
    ap.add_argument("--admin", required=True)
    ap.add_argument("--python-db", required=True)
    ap.add_argument("--dotnet-db", required=True)
    ap.add_argument("files", nargs="+")
    args = ap.parse_args()
    py, net = Side(args.python, args.admin), Side(args.dotnet, args.admin)
    same = different = 0

    def both(label: str, method: str, path: str, **kw):
        nonlocal same, different
        files = kw.pop("files_factory", None)
        s1, b1 = py.send(method, path, **({**kw, "files": files()} if files else kw))
        s2, b2 = net.send(method, path, **({**kw, "files": files()} if files else kw))
        problems = ([("$status", s1, s2)] if s1 != s2 else []) + (diff(b1, b2) if isinstance(b1, (dict, list)) else [])
        if problems:
            different += 1
            print(f"[{label}] {method} {path}")
            for p, x, y in problems[:8]:
                print(f"    {p}: python={str(x)[:100]!r} dotnet={str(y)[:100]!r}")
        else:
            same += 1
        return s1, b1

    uploads = []
    for f in args.files:
        p = Path(f)
        content = p.read_bytes()
        mime = mimetypes.guess_type(p.name)[0] or "application/octet-stream"
        for params in ({}, {"presence_mode": "WFH"}) if p.suffix == ".csv" else ({},):
            s, body = both(p.name, "POST", "/api/admin/attendance-uploads", params=params,
                           files_factory=lambda: {"file": (p.name, content, mime)})
            if s == 201:
                uploads.append(body)
    both("list", "GET", "/api/admin/attendance-uploads")
    # Validation and errors.
    both("no file", "POST", "/api/admin/attendance-uploads", data={"x": "1"})
    both("bad mode", "POST", "/api/admin/attendance-uploads", params={"presence_mode": "LEAVE"},
         files_factory=lambda: {"file": ("x.csv", b"Employee,Date\nE1,2026-10-05\n", "text/csv")})
    both("bad enum", "POST", "/api/admin/attendance-uploads", params={"presence_mode": "NOPE", "sheet": "x" * 101})
    both("missing", "GET", "/api/admin/attendance-uploads/999999")
    both("bad id", "POST", "/api/admin/attendance-uploads/abc/commit", json={})

    # Lifecycle: add unknown people where possible, then commit every other upload (the last with overwrite), discard the rest.
    for i, up in enumerate(uploads):
        uid = up["id"]
        if up["summary"]["unknown_employees"] and any(u["can_add"] for u in up["summary"]["unknown_employees"]):
            both(f"create {uid}", "POST", f"/api/admin/attendance-uploads/{uid}/create-employees", json={})
        if i % 2 == 0:
            both(f"commit {uid}", "POST", f"/api/admin/attendance-uploads/{uid}/commit", json={"overwrite_overrides": i == len(uploads) - 1})
            both(f"again {uid}", "POST", f"/api/admin/attendance-uploads/{uid}/commit", json={})
        else:
            both(f"discard {uid}", "POST", f"/api/admin/attendance-uploads/{uid}/discard")
        both(f"get {uid}", "GET", f"/api/admin/attendance-uploads/{uid}")

    # Overrides and policy on the first tracked employees.
    ids = [r["id"] for r in table(args.python_db, "select id from users where is_active and attendance_tracked order by id limit 3")]
    for eid in ids:
        for d, mode in (("2026-10-06", "WFH"), ("2026-10-07", "OFFICE"), ("2026-11-02", "LEAVE")):
            both("override", "PUT", f"/api/admin/attendance/{eid}/{d}/work-mode", json={"work_mode": mode, "note": "  checked  "})
        both("clear", "DELETE", f"/api/admin/attendance/{eid}/2026-10-07/work-mode")
        both("clear none", "DELETE", f"/api/admin/attendance/{eid}/2026-10-08/work-mode")
    both("override bad", "PUT", f"/api/admin/attendance/{ids[0]}/2020-01-01/work-mode", json={"work_mode": "WFH"})
    both("override far", "PUT", f"/api/admin/attendance/{ids[0]}/2030-01-01/work-mode", json={"work_mode": "WFH"})
    both("override enum", "PUT", f"/api/admin/attendance/{ids[0]}/2026-10-06/work-mode", json={"work_mode": "NOPE", "note": "x" * 501})
    both("override 404", "PUT", "/api/admin/attendance/999999/2026-10-06/work-mode", json={"work_mode": "WFH"})
    both("policy", "PUT", "/api/admin/wfo/policy", json={"min_days_per_week": 2, "min_office_minutes": None})
    both("policy same", "PUT", "/api/admin/wfo/policy", json={"min_days_per_week": 2})
    both("policy bad", "PUT", "/api/admin/wfo/policy", json={"min_days_per_week": 9, "min_days_per_month": -1})
    both("policy back", "PUT", "/api/admin/wfo/policy", json={"min_days_per_week": 3})
    if ids:
        both("wfo after", "GET", f"/api/team/wfo/employees/{ids[0]}", params={"month": "2026-10"})

    # Resulting tables.
    queries = {
        "attendance_daily": "select employee_id, work_date, first_in, last_out, office_minutes, wfh_minutes, status, corrected, work_mode, "
                            "wfo_source, conflicts, upload_work_mode, upload_minutes, badge_minutes, wifi_minutes, upload_id, "
                            "override_work_mode, override_note, overridden_by_id from attendance_daily order by employee_id, work_date",
        "attendance_uploads": "select id, filename, file_sha256, file_size, status, row_count, error_count, summary, commit_summary, "
                              "overwrite_overrides, uploaded_by_id, committed_by_id, period_start, period_end from attendance_uploads order by id",
        "attendance_upload_rows": "select upload_id, row_number, employee_ref, employee_id, work_date, raw_work_mode, work_mode, raw_source, "
                                  "is_badge, minutes, first_in, last_out, error from attendance_upload_rows order by upload_id, row_number",
        "users": "select id, employee_code, full_name, email, role, is_active, attendance_tracked, date_joined from users order by id",
        "audit_logs": "select actor_user_id, action, entity_type, entity_id, before_values, after_values from audit_logs order by id",
        "attendance_policy": "select min_wfo_days_per_week, min_wfo_days_per_month, min_office_minutes_for_wfo_day, updated_by_id from attendance_policy",
    }
    for name, sql in queries.items():
        a = [{k: normalise_json(v) for k, v in r.items()} for r in table(args.python_db, sql)]
        b = [{k: normalise_json(v) for k, v in r.items()} for r in table(args.dotnet_db, sql)]
        if name == "audit_logs":  # sessions and logins differ; compare the write actions only
            keep = {"ATTENDANCE_UPLOAD_PREVIEWED", "ATTENDANCE_UPLOAD_EMPLOYEES_ADDED", "ATTENDANCE_UPLOAD_COMMITTED",
                    "ATTENDANCE_UPLOAD_DISCARDED", "USER_CREATED", "USER_UPDATED", "WORK_MODE_OVERRIDDEN", "WORK_MODE_OVERRIDE_CLEARED",
                    "WFO_POLICY_UPDATED"}
            a, b = [r for r in a if r["action"] in keep], [r for r in b if r["action"] in keep]
        problems = diff(a, b, name)
        if problems:
            different += 1
            print(f"[table] {name}: {len(problems)} differences")
            for p, x, y in problems[:8]:
                print(f"    {p}: python={str(x)[:100]!r} dotnet={str(y)[:100]!r}")
        else:
            same += 1
            print(f"[table] {name}: {len(a)} rows identical")
    print(f"\n{same} identical, {different} different ({len(uploads)} uploads)")
    return 1 if different else 0


if __name__ == "__main__":
    sys.exit(main())
