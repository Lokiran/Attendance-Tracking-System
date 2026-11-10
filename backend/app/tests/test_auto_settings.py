"""Automatic attendance settings: HR admin only, validated, audited; the jobs read them via AutoRules."""

from sqlalchemy import select

from app.models import AuditLog
from app.services.auto_settings import rules
from app.services.policy import get_policy
from app.tests.conftest import login

URL = "/api/admin/auto-attendance/settings"
DEFAULTS = {"auto_attendance_enabled": False, "auto_shadow_mode": True, "gap_minutes": 30, "full_day_minutes": 480,
            "half_day_minutes": 240, "wifi_poll_minutes": 3, "presence_poll_minutes": 5, "no_data_alert_minutes": 15,
            "raw_retention_days": 90, "wfh_presence_states": ["Available", "Busy", "InAMeeting"]}


def test_only_hr_admin_can_read_settings(client, org):
    assert client.get(URL).status_code == 401
    login(client, org.rahul)
    assert client.get(URL).status_code == 403
    login(client, org.priya)
    assert client.get(URL).status_code == 403
    login(client, org.admin)
    body = client.get(URL).json()
    assert {k: body[k] for k in DEFAULTS} == DEFAULTS


def test_hr_admin_updates_settings_and_it_is_audited(client, org, db):
    h = login(client, org.admin)
    r = client.put(URL, headers=h, json={"gap_minutes": 20, "wfh_presence_states": [" Available ", "Busy", "Busy"]})
    assert r.status_code == 200, r.text
    assert r.json()["gap_minutes"] == 20 and r.json()["full_day_minutes"] == 480  # untouched fields kept
    assert r.json()["wfh_presence_states"] == ["Available", "Busy"]  # trimmed, de-duplicated
    entry = db.scalar(select(AuditLog).where(AuditLog.action == "AUTO_ATTENDANCE_SETTINGS_UPDATED"))
    assert entry.before_values["gap_minutes"] == 30 and entry.after_values["gap_minutes"] == 20
    assert entry.actor_user_id == org.admin.id
    db.expire_all()
    assert rules(get_policy(db)).gap_minutes == 20  # what every job will read
    assert rules(get_policy(db)).wfh_presence_states == frozenset({"Available", "Busy"})


def test_settings_validation(client, org):
    h = login(client, org.admin)
    assert client.put(URL, headers=h, json={"half_day_minutes": 500}).status_code == 422  # > full day (480)
    assert client.put(URL, headers=h, json={"full_day_minutes": 200}).status_code == 422  # < half day (240)
    assert client.put(URL, headers=h, json={"full_day_minutes": 420, "half_day_minutes": 210}).status_code == 200
    for bad in ({"gap_minutes": 0}, {"wifi_poll_minutes": 61}, {"raw_retention_days": 1},
                {"no_data_alert_minutes": 2}, {"wfh_presence_states": []}, {"wfh_presence_states": ["x" * 40]}):
        assert client.put(URL, headers=h, json=bad).status_code == 422, bad


def test_settings_changes_need_csrf_and_hr_admin(client, org):
    login(client, org.admin)
    assert client.put(URL, json={"gap_minutes": 25}).status_code == 403  # no CSRF header
    h = login(client, org.priya)
    assert client.put(URL, headers=h, json={"gap_minutes": 25}).status_code == 403


def test_unchanged_update_writes_no_audit_entry(client, org, db):
    h = login(client, org.admin)
    assert client.put(URL, headers=h, json={"gap_minutes": 30}).status_code == 200
    assert db.scalar(select(AuditLog).where(AuditLog.action == "AUTO_ATTENDANCE_SETTINGS_UPDATED")) is None


def test_seed_creates_ten_employees_with_identities_settings_and_holidays(frozen, db, monkeypatch):
    import re
    from datetime import date, time

    from sqlalchemy import func

    from app import seed
    from app.models import Holiday, Role, User
    monkeypatch.setenv("SEED_ADMIN_EMAIL", "admin@nexa.example")
    monkeypatch.setenv("SEED_ADMIN_PASSWORD", "AdminPassw0rd!")
    monkeypatch.setenv("SEED_DEMO_PASSWORD", "DemoPassw0rd!")
    frozen.set_local(date(2026, 3, 10), time(12, 0))
    seed.run(db)

    employees = list(db.scalars(select(User).where(User.role == Role.EMPLOYEE)))
    assert len(employees) == 10
    tracked = list(db.scalars(select(User).where(User.attendance_tracked.is_(True))))
    assert all(re.fullmatch(r"02:a7:5e(:[0-9a-f]{2}){3}", u.wifi_identity) for u in tracked)  # device MAC
    assert all(re.fullmatch(r"[0-9a-f-]{36}", u.graph_user_id) for u in tracked)  # Entra object id
    assert len({u.wifi_identity for u in tracked}) == len({u.graph_user_id for u in tracked}) == len(tracked)
    admin = db.scalar(select(User).where(User.role == Role.ADMIN))
    assert admin.wifi_identity is None and admin.graph_user_id is None  # not tracked, no devices
    assert db.scalar(select(func.count()).select_from(Holiday)) >= 3
    assert rules(get_policy(db)).full_day_minutes == 480

    # Re-running never overwrites an identity HR has set.
    rahul = db.scalar(select(User).where(User.employee_code == "EMP001"))
    rahul.wifi_identity = "aa:bb:cc:dd:ee:ff"
    db.commit()
    seed.run(db)
    db.refresh(rahul)
    assert rahul.wifi_identity == "aa:bb:cc:dd:ee:ff"
    assert db.scalar(select(func.count()).select_from(User).where(User.role == Role.EMPLOYEE)) == 10
