"""Migration tests that run on any configured database, plus offline PostgreSQL rendering checks.

The rendering checks compile the migration to PostgreSQL SQL *without* a server. They catch
dialect/DDL mistakes but are not a substitute for app/tests/postgres (which executes it).
"""

import tempfile
from pathlib import Path

import pytest
from sqlalchemy import inspect

from app.core.config import normalize_database_url
from app.db.session import engine
from app.tests.migration_utils import app_tables, current_revision, downgrade, offline_sql, reset_schema, upgrade

PG_URL = "postgresql+psycopg://user:pw@localhost:5432/ats_test"


@pytest.fixture
def empty_schema():
    reset_schema(engine)
    yield
    reset_schema(engine)


HEAD = "0003"


def test_upgrade_downgrade_reupgrade(empty_schema):
    upgrade()
    assert current_revision(engine) == HEAD
    assert app_tables() <= set(inspect(engine).get_table_names())

    downgrade("base")
    remaining = set(inspect(engine).get_table_names())
    assert not (app_tables() & remaining)
    assert current_revision(engine) is None

    upgrade()
    assert current_revision(engine) == HEAD
    assert app_tables() <= set(inspect(engine).get_table_names())


def test_migrated_schema_matches_models(empty_schema):
    from alembic.autogenerate import compare_metadata
    from alembic.migration import MigrationContext

    from app.db.base import Base

    upgrade()
    with engine.connect() as conn:
        ctx = MigrationContext.configure(conn, opts={"compare_type": True})
        diffs = compare_metadata(ctx, Base.metadata)
    # Expression indexes (lower(name), COALESCE(...)) cannot be compared reliably by reflection.
    expression_indexes = {"uq_departments_name_lower", "uq_holidays_date_department"}
    relevant = [d for d in diffs if not (isinstance(d, tuple) and d[0] in ("add_index", "remove_index")
                                         and d[1].name in expression_indexes)]
    assert relevant == [], relevant


def test_offline_postgres_upgrade_sql():
    sql = offline_sql(PG_URL)
    ext = sql.index("CREATE EXTENSION IF NOT EXISTS btree_gist")
    exclusion = sql.index("ADD CONSTRAINT ex_attendance_no_overlap EXCLUDE USING gist")
    assert ext < exclusion, "btree_gist must be installed before the exclusion constraint"
    assert "tstzrange(check_in_at, COALESCE(check_out_at, check_in_at), '[)') WITH &&" in sql
    assert "WHERE (status <> 'VOIDED')" in sql
    assert "CREATE UNIQUE INDEX uq_attendance_one_open_session ON attendance_sessions (employee_id) " \
           "WHERE status = 'OPEN'" in sql
    assert "check_in_at TIMESTAMP WITH TIME ZONE NOT NULL" in sql
    assert "CREATE UNIQUE INDEX uq_departments_name_lower ON departments (lower(name))" in sql
    # The circular departments -> users FK is added only after users exists.
    assert sql.index("CREATE TABLE users") < sql.index("ADD CONSTRAINT fk_departments_head_users")
    assert sql.strip().startswith("BEGIN;") or "BEGIN;" in sql  # transactional DDL
    assert "COMMIT;" in sql


def test_offline_postgres_downgrade_sql():
    sql = offline_sql(PG_URL, revision="base", downgrade_from="0001")
    assert sql.index("DROP TABLE attendance_corrections") < sql.index("DROP TABLE attendance_sessions")
    assert sql.index("DROP CONSTRAINT fk_departments_head_users") < sql.index("DROP TABLE users")
    assert sql.index("DROP TABLE users") < sql.index("DROP TABLE departments")
    assert "DROP EXTENSION" not in sql  # btree_gist is database-wide; it is intentionally kept


# An absolute path on whichever OS runs the tests (C:/... is not absolute on Linux).
ABS_SQLITE = "sqlite:///" + (Path(tempfile.gettempdir()) / "x.db").resolve().as_posix()


@pytest.mark.parametrize("raw, expected", [
    ("postgresql://u:p@h:5432/db", "postgresql+psycopg://u:p@h:5432/db"),
    ("postgres://u:p@h/db", "postgresql+psycopg://u:p@h/db"),
    ("postgresql+psycopg://u:p@h/db", "postgresql+psycopg://u:p@h/db"),
    (ABS_SQLITE, ABS_SQLITE),
    ("sqlite://", "sqlite://"),
])
def test_database_url_uses_psycopg3(raw, expected):
    assert normalize_database_url(raw) == expected


def test_settings_normalize_url():
    from app.core.config import Settings
    s = Settings(database_url="postgres://u:p@h/db", secret_key="k" * 40, _env_file=None)
    assert s.database_url == "postgresql+psycopg://u:p@h/db"


# --------------------------------------------------------------------------- 0002 (auto attendance)

def _exec(sql: str, **params):
    from sqlalchemy import text
    with engine.begin() as conn:
        return conn.execute(text(sql), params)


def _seed_0001_rows():
    """A user, an open + a completed session and a correction pointing at a session (schema 0001)."""
    _exec("INSERT INTO users (id, employee_code, full_name, email, password_hash, role, is_active, "
          "attendance_tracked, date_joined, created_at, updated_at) VALUES (1, 'E1', 'Mig User', 'mig@test.example', "
          "'x', 'EMPLOYEE', true, true, '2025-01-01', '2026-01-01 00:00:00', '2026-01-01 00:00:00')")
    _exec("INSERT INTO attendance_sessions (id, employee_id, check_in_at, check_out_at, break_minutes, net_minutes, "
          "status, source, created_at, updated_at) VALUES (1, 1, '2026-03-09 04:00:00', '2026-03-09 12:00:00', 0, 480, "
          "'COMPLETED', 'WEB', '2026-03-09 04:00:00', '2026-03-09 12:00:00')")
    _exec("INSERT INTO attendance_sessions (id, employee_id, check_in_at, break_minutes, status, source, created_at, "
          "updated_at) VALUES (2, 1, '2026-03-10 04:00:00', 0, 'OPEN', 'WEB', '2026-03-10 04:00:00', '2026-03-10 04:00:00')")
    _exec("INSERT INTO attendance_corrections (id, employee_id, attendance_date, request_type, original_session_id, "
          "requested_check_in_at, requested_check_out_at, requested_break_minutes, reason, status, created_at, updated_at) "
          "VALUES (1, 1, '2026-03-09', 'MODIFY_SESSION', 1, '2026-03-09 04:00:00', '2026-03-09 13:00:00', 0, "
          "'stayed late', 'PENDING', '2026-03-09 13:00:00', '2026-03-09 13:00:00')")


def _rows(table: str):
    from sqlalchemy import text
    with engine.connect() as conn:
        return [tuple(r) for r in conn.execute(text(f"SELECT * FROM {table} ORDER BY 1"))]


def test_0002_upgrade_preserves_existing_rows_and_accepts_auto_sources(empty_schema):
    from sqlalchemy.exc import IntegrityError
    upgrade("0001")
    _seed_0001_rows()
    sessions, corrections, users = _rows("attendance_sessions"), _rows("attendance_corrections"), _rows("users")
    upgrade("0002")
    assert _rows("attendance_sessions") == sessions
    assert _rows("attendance_corrections") == corrections  # FK values survive the SQLite rebuild
    width = len(users[0])
    assert [r[:width] for r in _rows("users")] == users  # new columns are appended; old values unchanged
    _exec("INSERT INTO attendance_sessions (id, employee_id, check_in_at, break_minutes, status, source, "
          "created_at, updated_at) VALUES (3, 1, '2026-03-11 04:00:00', 0, 'MISSING_CHECKOUT', 'AUTO_WIFI', "
          "'2026-03-11 04:00:00', '2026-03-11 04:00:00')")
    with pytest.raises(IntegrityError):
        _exec("INSERT INTO attendance_sessions (id, employee_id, check_in_at, break_minutes, status, source, "
              "created_at, updated_at) VALUES (4, 1, '2026-03-12 04:00:00', 0, 'MISSING_CHECKOUT', 'BOGUS', "
              "'2026-03-12 04:00:00', '2026-03-12 04:00:00')")
    with pytest.raises(IntegrityError):  # the one-open-session index still works after the rebuild
        _exec("INSERT INTO attendance_sessions (id, employee_id, check_in_at, break_minutes, status, source, "
              "created_at, updated_at) VALUES (4, 1, '2026-03-12 05:00:00', 0, 'OPEN', 'WEB', '2026-03-12 05:00:00', "
              "'2026-03-12 05:00:00')")


def test_0002_new_defaults_are_off_and_shadow(empty_schema):
    upgrade("0001")
    _exec("INSERT INTO attendance_policy (id, min_present_minutes, count_incomplete_as_present, max_session_minutes, "
          "late_grace_minutes, correction_window_days, manager_can_approve, updated_at) "
          "VALUES (1, 0, false, 960, 15, 30, true, '2026-01-01 00:00:00')")
    upgrade("0002")
    from sqlalchemy import text
    with engine.connect() as conn:
        enabled, shadow = conn.execute(text(
            "SELECT auto_attendance_enabled, auto_shadow_mode FROM attendance_policy")).one()
    assert not enabled and shadow


def test_0002_downgrade_refuses_while_auto_sessions_exist(empty_schema):
    upgrade("0001")
    _seed_0001_rows()
    before = _rows("attendance_sessions")
    upgrade("0002")
    _exec("INSERT INTO attendance_sessions (id, employee_id, check_in_at, break_minutes, status, source, "
          "created_at, updated_at) VALUES (3, 1, '2026-03-11 04:00:00', 0, 'MISSING_CHECKOUT', 'AUTO_TEAMS', "
          "'2026-03-11 04:00:00', '2026-03-11 04:00:00')")
    with pytest.raises(RuntimeError, match="Cannot downgrade 0002"):
        downgrade("0001")
    _exec("DELETE FROM attendance_sessions WHERE source = 'AUTO_TEAMS'")
    downgrade("0001")
    assert current_revision(engine) == "0001"
    assert _rows("attendance_sessions") == before
    assert "signals" not in inspect(engine).get_table_names()
    assert "graph_user_id" not in {c["name"] for c in inspect(engine).get_columns("users")}


def test_offline_postgres_0002_sql():
    sql = offline_sql(PG_URL)
    assert "DROP CONSTRAINT ck_attendance_sessions_session_source" in sql
    assert "ADD CONSTRAINT ck_attendance_sessions_session_source CHECK (source IN ('WEB', 'CORRECTION', 'ADMIN', " \
           "'SEED', 'AUTO_WIFI', 'AUTO_TEAMS'))" in sql
    assert "CREATE TABLE signals" in sql and "CREATE TABLE auto_attendance_days" in sql
    assert "auto_attendance_enabled BOOLEAN DEFAULT false NOT NULL" in sql
    assert "auto_shadow_mode BOOLEAN DEFAULT true NOT NULL" in sql
    # PostgreSQL alters the CHECK in place: attendance_sessions is never rebuilt or dropped.
    assert "DROP TABLE attendance_sessions" not in sql and "_alembic_tmp" not in sql


# --------------------------------------------------------------------------- 0003 (automatic attendance foundation)

LEGACY_0002 = {"signals", "auto_attendance_days", "worker_jobs", "ingest_api_keys"}
NEW_0003 = {"wifi_events", "presence_events", "attendance_daily", "attendance_daily_corrections"}
POLICY_0001 = ("INSERT INTO attendance_policy (id, min_present_minutes, count_incomplete_as_present, max_session_minutes, "
               "late_grace_minutes, correction_window_days, manager_can_approve, updated_at) "
               "VALUES (1, 0, false, 960, 15, 30, true, '2026-01-01 00:00:00')")


def test_0003_replaces_empty_legacy_tables_and_keeps_existing_data(empty_schema):
    upgrade("0001")
    _seed_0001_rows()
    _exec(POLICY_0001)
    upgrade("0002")
    sessions, corrections, users = _rows("attendance_sessions"), _rows("attendance_corrections"), _rows("users")
    upgrade("0003")
    tables = set(inspect(engine).get_table_names())
    assert not (LEGACY_0002 & tables) and NEW_0003 <= tables
    assert _rows("attendance_sessions") == sessions
    assert _rows("attendance_corrections") == corrections
    assert _rows("users") == users
    from sqlalchemy import text
    with engine.connect() as conn:
        row = conn.execute(text("SELECT gap_minutes, full_day_minutes, half_day_minutes, wifi_poll_minutes, "
                                "presence_poll_minutes, no_data_alert_minutes, raw_retention_days, wfh_presence_states, "
                                "min_present_minutes FROM attendance_policy")).one()
    assert tuple(row[:7]) == (30, 480, 240, 3, 5, 15, 90)
    import json
    states = row[7] if isinstance(row[7], list) else json.loads(row[7])
    assert states == ["Available", "Busy", "InAMeeting"]
    assert row[8] == 0  # existing settings untouched


def test_0003_refuses_to_drop_legacy_tables_that_have_rows(empty_schema):
    upgrade("0002")
    _exec("INSERT INTO worker_jobs (name, consecutive_failures, run_count, updated_at) "
          "VALUES ('wifi', 0, 1, '2026-01-01 00:00:00')")
    with pytest.raises(RuntimeError, match="worker_jobs: 1 row"):
        upgrade("0003")
    assert current_revision(engine) == "0002"
    assert _rows("worker_jobs")  # nothing was dropped


def test_0003_downgrade_refuses_new_data_then_restores_0002(empty_schema):
    upgrade("0001")
    _seed_0001_rows()
    upgrade("0003")
    _exec("INSERT INTO attendance_daily (employee_id, work_date, office_minutes, wfh_minutes, status, corrected, "
          "created_at, updated_at) VALUES (1, '2026-03-09', 480, 0, 'PRESENT', false, '2026-03-09 00:00:00', "
          "'2026-03-09 00:00:00')")
    with pytest.raises(RuntimeError, match="attendance_daily: 1 row"):
        downgrade("0002")
    assert current_revision(engine) == "0003"
    _exec("DELETE FROM attendance_daily")
    downgrade("0002")
    tables = set(inspect(engine).get_table_names())
    assert LEGACY_0002 <= tables and not (NEW_0003 & tables)
    assert "gap_minutes" not in {c["name"] for c in inspect(engine).get_columns("attendance_policy")}
    upgrade("0003")
    assert current_revision(engine) == "0003"


def test_0003_unique_keys_and_setting_checks(empty_schema):
    from sqlalchemy.exc import IntegrityError
    upgrade("0001")
    _seed_0001_rows()
    _exec(POLICY_0001)
    upgrade("0003")
    wifi = ("INSERT INTO wifi_events (employee_id, ts_bucket, first_seen_at, last_seen_at, event_type, source, created_at) "
            "VALUES (1, '2026-03-09 04:00:00', '2026-03-09 04:00:10', '2026-03-09 04:02:00', 'SEEN', 'mock', "
            "'2026-03-09 04:02:00')")
    _exec(wifi)
    with pytest.raises(IntegrityError):  # one row per person per bucket: collectors can re-run safely
        _exec(wifi)
    presence = ("INSERT INTO presence_events (employee_id, ts_bucket, polled_at, availability, source, created_at) "
                "VALUES (1, '2026-03-09 04:00:00', '2026-03-09 04:01:00', 'Available', 'mock', '2026-03-09 04:01:00')")
    _exec(presence)
    with pytest.raises(IntegrityError):
        _exec(presence)
    daily = ("INSERT INTO attendance_daily (employee_id, work_date, office_minutes, wfh_minutes, status, corrected, "
             "created_at, updated_at) VALUES (1, '2026-03-09', 0, 0, 'ABSENT', false, '2026-03-09 00:00:00', "
             "'2026-03-09 00:00:00')")
    _exec(daily)
    with pytest.raises(IntegrityError):
        _exec(daily)
    with pytest.raises(IntegrityError):  # status values are constrained
        _exec(daily.replace("'2026-03-09',", "'2026-03-10',").replace("'ABSENT'", "'LEAVE'"))
    with pytest.raises(IntegrityError):  # half day can never exceed full day
        _exec("UPDATE attendance_policy SET half_day_minutes = 500")
    with pytest.raises(IntegrityError):
        _exec("UPDATE attendance_policy SET gap_minutes = 0")


def test_offline_postgres_0003_sql():
    sql = offline_sql(PG_URL)
    assert "DROP TABLE signals" in sql and "DROP TABLE ingest_api_keys" in sql
    assert "CREATE TABLE wifi_events" in sql and "CREATE TABLE attendance_daily" in sql
    assert "CONSTRAINT uq_wifi_events_employee_bucket UNIQUE (employee_id, ts_bucket)" in sql
    assert "ts_bucket TIMESTAMP WITH TIME ZONE NOT NULL" in sql
    assert "ck_attendance_policy_half_day_minutes_range CHECK (half_day_minutes >= 0 AND "            "half_day_minutes <= full_day_minutes)" in sql
    assert "DROP TABLE attendance_policy" not in sql  # PostgreSQL alters the settings table in place
