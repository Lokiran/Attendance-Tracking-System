"""Alembic migration executed against PostgreSQL: extension, exclusion constraint, partial index."""

from datetime import UTC, datetime

import pytest
from sqlalchemy import inspect, text
from sqlalchemy.exc import IntegrityError

from app.db.session import engine
from app.tests.migration_utils import app_tables, current_revision, downgrade, upgrade

from .conftest import INSERT_SESSION, session_row

EXCLUSION_VIOLATION = "23P01"
UNIQUE_VIOLATION = "23505"


def _scalar(sql: str, **params):
    with engine.connect() as conn:
        return conn.execute(text(sql), params).scalar()


def test_upgrade_installs_extension_and_postgres_constraints(migrated):
    assert current_revision(engine) == "0003"
    assert app_tables() <= set(inspect(engine).get_table_names())
    assert _scalar("SELECT extname FROM pg_extension WHERE extname = 'btree_gist'") == "btree_gist"

    with engine.connect() as conn:
        contype, definition = conn.execute(text(
            "SELECT contype, pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ex_attendance_no_overlap'"
        )).one()
    assert contype == "x"  # exclusion constraint
    assert "gist" in definition.lower() and "tstzrange" in definition and "VOIDED" in definition

    index = _scalar("SELECT indexdef FROM pg_indexes WHERE indexname = 'uq_attendance_one_open_session'")
    assert "UNIQUE" in index and "WHERE" in index and "'OPEN'" in index

    with engine.connect() as conn:
        types = dict(conn.execute(text(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'attendance_sessions'"
        )).all())
    assert types["check_in_at"] == types["check_out_at"] == "timestamp with time zone"


def test_downgrade_then_reupgrade(migrated):
    downgrade("base")
    assert not (app_tables() & set(inspect(engine).get_table_names()))
    assert current_revision(engine) is None
    # btree_gist is database-wide and intentionally kept; the constraint went with its table.
    assert _scalar("SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist'") == 1
    assert _scalar("SELECT count(*) FROM pg_constraint WHERE conname = 'ex_attendance_no_overlap'") == 0

    upgrade()
    assert current_revision(engine) == "0003"
    assert _scalar("SELECT count(*) FROM pg_constraint WHERE conname = 'ex_attendance_no_overlap'") == 1


def _insert(rows):
    with engine.begin() as conn:
        for r in rows:
            conn.execute(INSERT_SESSION, r)


def _t(hour: int, minute: int = 0) -> datetime:
    return datetime(2026, 3, 9, hour, minute, tzinfo=UTC)


@pytest.fixture
def employee_id(migrated):
    with engine.begin() as conn:
        return conn.execute(text(
            "INSERT INTO users (employee_code, full_name, email, password_hash, role, is_active, attendance_tracked, "
            "date_joined, created_at, updated_at) VALUES ('PG001', 'Pg User', 'pg.user@test.example', 'x', "
            "'EMPLOYEE', true, true, '2025-01-01', now(), now()) RETURNING id"
        )).scalar()


def test_migrated_exclusion_constraint_rejects_overlap(employee_id):
    _insert([session_row(employee_id, _t(4), _t(7))])
    with pytest.raises(IntegrityError) as exc:
        _insert([session_row(employee_id, _t(6), _t(9))])
    assert exc.value.orig.sqlstate == EXCLUSION_VIOLATION


def test_migrated_exclusion_constraint_allows_adjacent_and_voided(employee_id):
    _insert([session_row(employee_id, _t(4), _t(6)), session_row(employee_id, _t(6), _t(8))])  # [4,6) [6,8)
    _insert([session_row(employee_id, _t(5), _t(7), status="VOIDED")])  # history rows may overlap
    assert _scalar("SELECT count(*) FROM attendance_sessions") == 3


def test_migrated_constraint_is_per_employee(employee_id):
    with engine.begin() as conn:
        second = conn.execute(text(
            "INSERT INTO users (employee_code, full_name, email, password_hash, role, is_active, attendance_tracked, "
            "date_joined, created_at, updated_at) VALUES ('PG002', 'Pg Two', 'pg.two@test.example', 'x', "
            "'EMPLOYEE', true, true, '2025-01-01', now(), now()) RETURNING id")).scalar()
    # Identical times for two different employees are fine.
    _insert([session_row(employee_id, _t(4), _t(7)), session_row(second, _t(4), _t(7))])


def test_migrated_partial_unique_index_allows_one_open_session(employee_id):
    _insert([session_row(employee_id, _t(4), status="OPEN")])
    with pytest.raises(IntegrityError) as exc:
        _insert([session_row(employee_id, _t(9), status="OPEN")])
    assert exc.value.orig.sqlstate == UNIQUE_VIOLATION
    # Unresolved (MISSING_CHECKOUT) sessions are outside the partial index.
    _insert([session_row(employee_id, _t(1), status="MISSING_CHECKOUT")])


def test_migrated_check_constraints(employee_id):
    bad = session_row(employee_id, _t(8), _t(7))
    bad["net_minutes"] = 0
    with pytest.raises(IntegrityError) as exc:
        _insert([bad])
    assert exc.value.orig.sqlstate == "23514"  # check_violation: check-out must follow check-in


def test_0002_widens_source_check_in_place_and_keeps_exclusion_constraint(migrated):
    assert current_revision(engine) == "0003"
    definition = _scalar("SELECT pg_get_constraintdef(oid) FROM pg_constraint "
                         "WHERE conname = 'ck_attendance_sessions_session_source'")
    assert "AUTO_WIFI" in definition and "AUTO_TEAMS" in definition and "SEED" in definition
    assert _scalar("SELECT count(*) FROM pg_constraint WHERE conname = 'ex_attendance_no_overlap'") == 1
    assert _scalar("SELECT data_type FROM information_schema.columns "
                   "WHERE table_name = 'wifi_events' AND column_name = 'ts_bucket'") == "timestamp with time zone"


def test_0002_downgrade_to_0001_and_back(employee_id):
    downgrade("0001")
    assert current_revision(engine) == "0001"
    definition = _scalar("SELECT pg_get_constraintdef(oid) FROM pg_constraint "
                         "WHERE conname = 'ck_attendance_sessions_session_source'")
    assert "AUTO_WIFI" not in definition
    assert "signals" not in inspect(engine).get_table_names()
    upgrade()
    assert current_revision(engine) == "0003"


def test_0002_auto_session_respects_exclusion_constraint(employee_id):
    auto = session_row(employee_id, _t(4), _t(7))
    with engine.begin() as conn:
        conn.execute(INSERT_SESSION, session_row(employee_id, _t(4), _t(7)))
    with pytest.raises(IntegrityError) as exc:
        with engine.begin() as conn:
            conn.execute(text(INSERT_SESSION.text.replace("'ADMIN'", "'AUTO_WIFI'")), auto)
    assert exc.value.orig.sqlstate == EXCLUSION_VIOLATION


def test_0003_settings_defaults_checks_and_round_trip(migrated):
    assert current_revision(engine) == "0003"
    assert set(inspect(engine).get_table_names()) >= {"wifi_events", "presence_events", "attendance_daily",
                                                      "attendance_daily_corrections"}
    assert "signals" not in inspect(engine).get_table_names()
    with engine.begin() as conn:
        conn.execute(text("INSERT INTO attendance_policy (id, min_present_minutes, count_incomplete_as_present, "
                          "max_session_minutes, late_grace_minutes, correction_window_days, manager_can_approve, "
                          "updated_at) VALUES (1, 0, false, 960, 15, 30, true, now()) ON CONFLICT DO NOTHING"))
    assert _scalar("SELECT wfh_presence_states::text FROM attendance_policy") == '["Available", "Busy", "InAMeeting"]'
    assert _scalar("SELECT gap_minutes FROM attendance_policy") == 30
    with pytest.raises(IntegrityError) as exc, engine.begin() as conn:
        conn.execute(text("UPDATE attendance_policy SET half_day_minutes = full_day_minutes + 1"))
    assert exc.value.orig.sqlstate == "23514"
    with engine.begin() as conn:
        conn.execute(text("DELETE FROM attendance_policy"))
    downgrade("0002")
    assert "signals" in inspect(engine).get_table_names()
    upgrade()
    assert current_revision(engine) == "0003"
