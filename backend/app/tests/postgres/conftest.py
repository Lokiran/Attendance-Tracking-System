"""Helpers for PostgreSQL integration tests (every test in this package is marked ``postgres``)."""

from contextlib import contextmanager
from datetime import datetime

import pytest
from sqlalchemy import text

from app.db.session import engine
from app.tests.migration_utils import reset_schema, upgrade

INSERT_SESSION = text(
    "INSERT INTO attendance_sessions (employee_id, check_in_at, check_out_at, net_minutes, status, source, "
    "break_minutes, created_at, updated_at) "
    "VALUES (:employee_id, :check_in_at, :check_out_at, :net_minutes, :status, 'ADMIN', 0, :ts, :ts)"
)


def session_row(employee_id: int, check_in_at: datetime, check_out_at: datetime | None = None,
                status: str = "COMPLETED") -> dict:
    net = int((check_out_at - check_in_at).total_seconds() // 60) if check_out_at else None
    return {"employee_id": employee_id, "check_in_at": check_in_at, "check_out_at": check_out_at,
            "net_minutes": net, "status": status, "ts": check_in_at}


@contextmanager
def transaction(timeout: str = "10s"):
    """A raw connection inside an explicit transaction, with lock/statement timeouts so a broken
    locking assumption fails the test instead of hanging it."""
    with engine.connect() as conn:
        tx = conn.begin()
        conn.execute(text(f"SET LOCAL lock_timeout = '{timeout}'"))
        conn.execute(text(f"SET LOCAL statement_timeout = '{timeout}'"))
        try:
            yield conn, tx
        finally:
            if tx.is_active:
                tx.rollback()


@pytest.fixture
def migrated():
    """Schema built by Alembic (not metadata.create_all), so constraints come from the migration."""
    reset_schema(engine)
    upgrade()
    yield
    reset_schema(engine)
