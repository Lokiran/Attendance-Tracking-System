"""Concurrency on PostgreSQL: row locks, the partial unique index, and parallel API requests.

SQLite serialises all writers, so none of this is meaningful there.
"""

import threading
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import timedelta

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import func, select, text
from sqlalchemy.exc import IntegrityError

from app.main import app
from app.models import AttendanceSession, SessionStatus, User
from app.services import attendance_service
from app.tests.conftest import login

from .conftest import INSERT_SESSION, session_row, transaction


def _signed_in(n: int, user: User):
    clients = [TestClient(app) for _ in range(n)]
    return clients, [login(c, user) for c in clients]


def _fire_together(clients, headers, path: str, json=None) -> list[int]:
    barrier = threading.Barrier(len(clients), timeout=15)

    def call(i: int) -> int:
        barrier.wait()
        return clients[i].post(path, headers=headers[i], json=json).status_code

    with ThreadPoolExecutor(max_workers=len(clients)) as pool:
        return list(pool.map(call, range(len(clients))))


def _count(db, status: SessionStatus) -> int:
    db.expire_all()
    return db.scalar(select(func.count()).select_from(AttendanceSession).where(AttendanceSession.status == status))


def test_parallel_check_ins_create_exactly_one_open_session(frozen, org, db):
    clients, headers = _signed_in(8, org.rahul)
    codes = _fire_together(clients, headers, "/api/attendance/check-in")
    assert sorted(codes) == [201] + [409] * 7, codes
    assert _count(db, SessionStatus.OPEN) == 1


def test_parallel_check_outs_complete_the_session_once(frozen, org, db):
    clients, headers = _signed_in(4, org.rahul)
    assert clients[0].post("/api/attendance/check-in", headers=headers[0]).status_code == 201
    frozen.advance(minutes=30)
    codes = _fire_together(clients, headers, "/api/attendance/check-out", json={"break_minutes": 0})
    assert sorted(codes) == [200, 409, 409, 409], codes
    assert _count(db, SessionStatus.COMPLETED) == 1 and _count(db, SessionStatus.OPEN) == 0


def test_check_in_waits_for_the_employee_row_lock(frozen, org, db):
    """check_in takes SELECT ... FOR UPDATE on the employee row and re-reads after waiting."""
    client = TestClient(app)
    headers = login(client, org.rahul)  # before locking: login inserts rows referencing users
    with transaction() as (locker, tx):
        locker.execute(text("SELECT id FROM users WHERE id = :id FOR UPDATE"), {"id": org.rahul.id})
        with ThreadPoolExecutor(max_workers=1) as pool:
            pending = pool.submit(lambda: client.post("/api/attendance/check-in", headers=headers).status_code)
            time.sleep(1.0)
            assert not pending.done(), "check-in must block while another transaction holds the row lock"
            # The lock holder checks in first; the waiter must see it once the lock is released.
            locker.execute(INSERT_SESSION, session_row(org.rahul.id, frozen.now - timedelta(minutes=1), status="OPEN"))
            tx.commit()
            assert pending.result(timeout=20) == 409
    assert _count(db, SessionStatus.OPEN) == 1


def test_partial_unique_index_stops_duplicates_when_app_lock_is_bypassed(frozen, org, db, monkeypatch):
    """Defence in depth: with the row lock removed and both requests forced past the
    "already checked in?" read, PostgreSQL's unique index must still allow only one."""
    clients, headers = _signed_in(2, org.rahul)
    barrier = threading.Barrier(2, timeout=15)
    original = attendance_service._open_session

    def racing_open_session(session, employee_id):
        found = original(session, employee_id)
        barrier.wait()  # both requests have now seen "no open session"
        return found

    monkeypatch.setattr(attendance_service, "_lock_employee", lambda session, eid: session.get(User, eid))
    monkeypatch.setattr(attendance_service, "_open_session", racing_open_session)
    with ThreadPoolExecutor(max_workers=2) as pool:
        codes = list(pool.map(lambda i: clients[i].post("/api/attendance/check-in", headers=headers[i]).status_code,
                              range(2)))
    assert sorted(codes) == [201, 409], codes  # IntegrityError is translated, never a 500
    assert _count(db, SessionStatus.OPEN) == 1


def test_second_uncommitted_open_insert_waits_then_fails(frozen, org):
    """Raw database behaviour: a concurrent OPEN insert blocks on the first and then violates the index."""
    first = session_row(org.rahul.id, frozen.now, status="OPEN")
    second = session_row(org.rahul.id, frozen.now + timedelta(minutes=1), status="OPEN")
    with transaction() as (a, tx_a), transaction() as (b, _tx_b):
        a.execute(INSERT_SESSION, first)

        def insert_second():
            try:
                b.execute(INSERT_SESSION, second)
                return "inserted"
            except IntegrityError as exc:
                return exc.orig.sqlstate

        with ThreadPoolExecutor(max_workers=1) as pool:
            pending = pool.submit(insert_second)
            time.sleep(0.5)
            assert not pending.done(), "the second insert should wait for the first transaction"
            tx_a.commit()
            assert pending.result(timeout=20) == "23505"


@pytest.mark.parametrize("n", [2, 6])
def test_parallel_check_ins_for_different_employees_do_not_block_each_other(frozen, org, db, n):
    people = [org.rahul, org.sneha, org.arjun, org.priya, org.anil, org.vikram][:n]
    clients = [TestClient(app) for _ in people]
    headers = [login(c, p) for c, p in zip(clients, people)]
    codes = _fire_together(clients, headers, "/api/attendance/check-in")
    assert codes == [201] * n
    assert _count(db, SessionStatus.OPEN) == n
