"""Scale behaviour: one admin, a few managers, many employees.

* Dashboard queries must not grow with headcount (no per-employee queries).
* The shared record cache must return exactly what a fresh computation returns, and must never
  serve stale numbers - including after a write made by another server process.
* Identical concurrent requests compute once; the cache stays inside its memory budget.
"""

import threading
import time as _time
from datetime import UTC, date, datetime, timedelta

import pytest
from sqlalchemy import event, insert, text

from app.db.session import engine
from app.models import AttendanceDaily, DailyStatus, Role, User, WorkMode
from app.services import attendance_data, record_cache
from app.tests.conftest import login

RANGE = {"start": "2026-03-02", "end": "2026-03-10", "on_date": "2026-03-10"}
DASHBOARD_CALLS = [
    ("/api/dashboard/overview", RANGE),
    ("/api/dashboard/employees", {**RANGE, "page_size": 10}),
    ("/api/dashboard/employees", {**RANGE, "page_size": 200, "sort": "attendance_percentage", "order": "desc"}),
    ("/api/dashboard/trends", {"start": RANGE["start"], "end": RANGE["end"], "granularity": "day"}),
    ("/api/dashboard/exceptions", {"start": RANGE["start"], "end": RANGE["end"], "page_size": 8}),
    ("/api/dashboard/departments", RANGE),
    ("/api/dashboard/managers", RANGE),
]


def build_org(factory, db, employees: int, managers: int = 3):
    """1 admin, `managers` managers, `employees` employees with a week of imported timesheet days each
    (a committed timesheet covers 2-9 March)."""
    factory.default_schedule()
    dept = factory.department("Operations")
    admin = factory.user("Admin User", Role.ADMIN)
    admin.attendance_tracked = False
    db.commit()
    mgrs = [factory.user(f"Manager {i}", Role.MANAGER, manager=admin, department=dept) for i in range(managers)]
    db.execute(insert(User), [
        dict(employee_code=f"S{i:05d}", full_name=f"Staff {i:05d}", email=f"staff{i}@scale.example",
             password_hash=mgrs[0].password_hash, role=Role.EMPLOYEE, department_id=dept.id,
             manager_id=mgrs[i % managers].id, is_active=True, attendance_tracked=True, date_joined=date(2025, 1, 1))
        for i in range(employees)
    ])
    ids = [i for (i,) in db.execute(text("SELECT id FROM users WHERE role = 'EMPLOYEE'"))]
    rows = []
    for uid in ids:
        for day in range(2, 10):  # Mon 2 .. Mon 9 March 2026 (weekend skipped below)
            d = datetime(2026, 3, day, 4, 0, tzinfo=UTC) + timedelta(minutes=(uid * 7 + day) % 50)
            if d.isoweekday() > 5 or (uid + day) % 9 == 0:
                continue
            rows.append(daily(uid, d.date(), d, 9 * 60 - 45))
    db.execute(insert(AttendanceDaily), rows)
    db.commit()
    factory.cover(date(2026, 3, 2), date(2026, 3, 9), ids + [m.id for m in mgrs])
    return admin, mgrs


def daily(uid: int, d: date, first_in: datetime | None, minutes: int | None) -> dict:
    return dict(employee_id=uid, work_date=d, office_minutes=0, wfh_minutes=0, status=DailyStatus.PRESENT,
                corrected=False, conflicts=[], upload_work_mode=WorkMode.OFFICE, upload_minutes=minutes,
                first_in=first_in)


def _dashboard_json(client) -> list:
    out = []
    for path, params in DASHBOARD_CALLS:
        r = client.get(path, params=params)
        assert r.status_code == 200, r.text
        body = r.json()
        if isinstance(body, dict):
            body.pop("generated_at", None)
        out.append(body)
    return out


def _count_statements(client) -> list[int]:
    counts = []
    n = [0]

    def on_exec(*_a, **_k):
        n[0] += 1

    event.listen(engine, "before_cursor_execute", on_exec)
    try:
        for path, params in DASHBOARD_CALLS:
            n[0] = 0
            assert client.get(path, params=params).status_code == 200
            counts.append(n[0])
    finally:
        event.remove(engine, "before_cursor_execute", on_exec)
    return counts


def test_dashboard_queries_do_not_grow_with_headcount(client, factory, db):
    """Count SQL statements per dashboard endpoint, add 60 more people, count again: must be equal."""
    admin, mgrs = build_org(factory, db, 10)
    login(client, admin)
    record_cache.clear()
    before = _count_statements(client)
    db.execute(insert(User), [
        dict(employee_code=f"X{i:05d}", full_name=f"Extra {i:05d}", email=f"extra{i}@scale.example",
             password_hash=mgrs[0].password_hash, role=Role.EMPLOYEE, manager_id=mgrs[1].id, is_active=True,
             attendance_tracked=True, date_joined=date(2025, 1, 1)) for i in range(60)
    ])
    db.commit()
    record_cache.clear()
    assert _count_statements(client) == before, "a dashboard endpoint issues per-employee queries"


def test_cached_results_equal_fresh_results(client, factory, db, monkeypatch):
    admin, _ = build_org(factory, db, 60)
    login(client, admin)
    monkeypatch.setattr(attendance_data, "CACHE_MIN_EMPLOYEES", 10**9)  # caching off
    fresh = _dashboard_json(client)
    monkeypatch.setattr(attendance_data, "CACHE_MIN_EMPLOYEES", 0)
    record_cache.clear()
    first = _dashboard_json(client)   # fills the cache
    second = _dashboard_json(client)  # served from the cache
    assert fresh == first == second


def test_imported_day_shows_up_immediately(client, factory, db, frozen):
    admin, _ = build_org(factory, db, 30)
    staff = db.query(User).filter(User.email == "staff1@scale.example").one()
    login(client, admin)
    before = client.get("/api/dashboard/overview", params=RANGE).json()["snapshot"]
    # same minute, frozen clock: only the in-process write version can tell the data changed
    db.add(AttendanceDaily(**daily(staff.id, date(2026, 3, 10), None, None)))
    db.commit()
    after = client.get("/api/dashboard/overview", params=RANGE).json()["snapshot"]
    assert after["present"] == before["present"] + 1
    assert after["no_timesheet"] == before["no_timesheet"] - 1


def test_update_with_unchanged_timestamp_shows_up(client, factory, db, frozen):
    """Frozen clock: an ORM update keeps the same updated_at, count and max id, so the timesheet
    fingerprint cannot see it; the in-process write version must."""
    admin, _ = build_org(factory, db, 30)
    login(client, admin)
    s = db.query(AttendanceDaily).order_by(AttendanceDaily.id).first()
    s.updated_at = frozen.now  # make sure the fingerprint really cannot tell
    db.commit()
    record_cache.clear()
    before = client.get("/api/dashboard/overview", params=RANGE).json()["period"]["net_minutes"]
    s.upload_minutes += 30
    db.commit()
    assert s.updated_at == frozen.now
    after = client.get("/api/dashboard/overview", params=RANGE).json()["period"]["net_minutes"]
    assert after == before + 30


def test_write_from_another_process_is_picked_up(client, factory, db):
    """A raw UPDATE (as another server process would do) bypasses this process's write tracking;
    the timesheet fingerprint (updated_at) must still invalidate the cached result."""
    admin, _ = build_org(factory, db, 30)
    login(client, admin)
    before = client.get("/api/dashboard/overview", params=RANGE).json()["period"]["net_minutes"]
    version = record_cache.write_version()
    with engine.begin() as conn:
        conn.execute(text("UPDATE attendance_daily SET upload_minutes = upload_minutes + 60, updated_at = :t "
                          "WHERE id = (SELECT min(id) FROM attendance_daily)"),
                     {"t": datetime(2030, 1, 1, tzinfo=UTC)})
    assert record_cache.write_version() == version  # this process did not notice the write...
    after = client.get("/api/dashboard/overview", params=RANGE).json()["period"]["net_minutes"]
    assert after == before + 60  # ...but the dashboard is still correct


def test_schedule_change_invalidates(client, factory, db):
    admin, _ = build_org(factory, db, 30)
    login(client, admin)
    before = client.get("/api/dashboard/overview", params=RANGE).json()["period"]["required_minutes"]
    db.execute(text("UPDATE work_schedules SET required_minutes = 420"))
    db.commit()
    after = client.get("/api/dashboard/overview", params=RANGE).json()["period"]["required_minutes"]
    assert after == before * 420 // 480


def test_attendance_bands_count_everyone_not_a_sample(client, factory, db):
    """The distribution chart used to count only the first 200 rows; bands now cover the whole scope."""
    admin, _ = build_org(factory, db, 260)
    login(client, admin)
    o = client.get("/api/dashboard/overview", params=RANGE).json()
    assert [b["label"] for b in o["attendance_bands"]] == ["<70%", "70–80%", "80–90%", "90–95%", "95–100%"]
    assert sum(b["count"] for b in o["attendance_bands"]) == o["headcount"] == 263  # 260 staff + 3 managers


def test_people_search_is_limited_and_scoped(client, factory, db):
    admin, mgrs = build_org(factory, db, 300)
    login(client, admin)
    r = client.get("/api/team/members", params={"search": "staff 001", "limit": 5}).json()
    assert len(r) == 5 and all("Staff 001" in p["full_name"] for p in r)
    assert [p["full_name"] for p in r] == sorted(p["full_name"] for p in r)
    by_code = client.get("/api/team/members", params={"search": "s00042", "limit": 20}).json()
    assert [p["employee_code"] for p in by_code] == ["S00042"]
    assert client.get("/api/team/members", params={"limit": 500}).status_code == 422

    # a manager only finds people in their own subtree, and gets 404 for anyone else
    login(client, mgrs[0])
    mine = client.get("/api/team/members", params={"search": "staff", "limit": 200}).json()
    assert mine and all(p["manager_id"] == mgrs[0].id for p in mine)
    other = db.query(User).filter(User.manager_id == mgrs[1].id).first()
    assert client.get(f"/api/team/members/{other.id}").status_code == 404
    own = mine[0]
    assert client.get(f"/api/team/members/{own['id']}").json()["full_name"] == own["full_name"]
    assert client.get("/api/team/members/999999").status_code == 404


def test_identical_concurrent_requests_compute_once():
    record_cache.clear()
    calls = []
    gate = threading.Event()

    def compute():
        calls.append(1)
        gate.wait(2)
        return {"value": 42}

    results = []
    threads = [threading.Thread(target=lambda: results.append(record_cache.get_or_compute(("k",), 1, compute)))
               for _ in range(6)]
    for t in threads:
        t.start()
    _time.sleep(0.2)
    gate.set()
    for t in threads:
        t.join(5)
    assert len(calls) == 1
    assert results == [{"value": 42}] * 6


def test_cache_stays_within_budget(monkeypatch):
    record_cache.clear()
    monkeypatch.setattr(record_cache, "MAX_DAY_RECORDS", 100)
    for i in range(10):
        record_cache.get_or_compute(("entry", i), 30, lambda i=i: i)
    assert record_cache._total <= 100
    assert len(record_cache._entries) == 3
    # the newest entries are kept
    assert record_cache.get_or_compute(("entry", 9), 30, lambda: "recomputed") == 9
    # a value larger than the whole budget is returned but not kept
    assert record_cache.get_or_compute(("huge",), 1000, lambda: "big") == "big"
    assert ("huge",) not in record_cache._entries


def test_failed_computation_does_not_block_others():
    record_cache.clear()

    def boom():
        raise RuntimeError("database unavailable")

    with pytest.raises(RuntimeError):
        record_cache.get_or_compute(("bad",), 1, boom)
    assert record_cache.get_or_compute(("bad",), 1, lambda: "ok") == "ok"
