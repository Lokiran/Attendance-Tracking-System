"""Small in-process cache that lets the several requests of one dashboard page share one computation.

* Single flight: when identical requests arrive together (cards, table and charts load in parallel),
  one computes and the others wait for its result instead of repeating the work.
* Bounded: least-recently-used entries are evicted once the cached day records exceed
  ``MAX_DAY_RECORDS``, and entries expire after ``TTL_SECONDS``.
* Never stale: callers build the key from everything the result depends on (see
  ``attendance_data.compute_records``). ``write_version()`` additionally changes on every commit that
  touches attendance sessions in this process, which covers writes that keep the same timestamp.

Cached values are shared between requests and must be treated as read-only.
"""

from __future__ import annotations

import threading
import time
from collections import OrderedDict
from collections.abc import Callable, Hashable
from typing import Any

from sqlalchemy import event
from sqlalchemy.orm import Session, UOWTransaction

TTL_SECONDS = 300
# About 250 bytes per day record, so at most ~250 MB; enough for 10,000 people x 90 days.
MAX_DAY_RECORDS = 1_000_000
_WAIT_SECONDS = 120

_lock = threading.Lock()
_entries: OrderedDict[Hashable, tuple[float, int, Any]] = OrderedDict()  # key -> (expires, size, value)
_inflight: dict[Hashable, threading.Event] = {}
_total = 0
_version = 0


def write_version() -> int:
    return _version


def clear() -> None:
    global _total
    with _lock:
        _entries.clear()
        _total = 0


def _get(key: Hashable) -> Any | None:
    hit = _entries.get(key)
    if hit is None:
        return None
    if hit[0] < time.monotonic():
        _drop(key)
        return None
    _entries.move_to_end(key)
    return hit


def _drop(key: Hashable) -> None:
    global _total
    expires_size_value = _entries.pop(key, None)
    if expires_size_value is not None:
        _total -= expires_size_value[1]


def _store(key: Hashable, size: int, value: Any) -> None:
    global _total
    if size > MAX_DAY_RECORDS:
        return  # too big to keep; the caller still gets the value
    _drop(key)
    _entries[key] = (time.monotonic() + TTL_SECONDS, size, value)
    _total += size
    while _total > MAX_DAY_RECORDS and _entries:
        _drop(next(iter(_entries)))


def get_or_compute(key: Hashable, size: int, compute: Callable[[], Any]) -> Any:
    with _lock:
        hit = _get(key)
        if hit is not None:
            return hit[2]
        waiter = _inflight.get(key)
        if waiter is None:
            _inflight[key] = threading.Event()
    if waiter is not None:
        waiter.wait(_WAIT_SECONDS)
        with _lock:
            hit = _get(key)
        return hit[2] if hit is not None else compute()
    try:
        value = compute()
        with _lock:
            _store(key, size, value)
        return value
    finally:
        with _lock:
            _inflight.pop(key).set()


# ------------------------------------------------------------------ write tracking

_TOUCHED = "ats_attendance_touched"


def _is_tracked(obj: object) -> bool:
    from app.models import AttendanceSession  # local import: models import nothing from services

    return isinstance(obj, AttendanceSession)


@event.listens_for(Session, "after_flush")
def _after_flush(session: Session, _flush_context: UOWTransaction) -> None:
    if any(_is_tracked(o) for o in (*session.new, *session.dirty, *session.deleted)):
        session.info[_TOUCHED] = True


@event.listens_for(Session, "do_orm_execute")
def _bulk_write(state) -> None:  # bulk insert/update/delete statements bypass the flush
    if (state.is_insert or state.is_update or state.is_delete) and state.bind_mapper is not None \
            and state.bind_mapper.class_.__name__ == "AttendanceSession":
        state.session.info[_TOUCHED] = True


@event.listens_for(Session, "after_commit")
def _after_commit(session: Session) -> None:
    global _version
    if session.info.pop(_TOUCHED, False):
        with _lock:
            _version += 1


@event.listens_for(Session, "after_rollback")
def _after_rollback(session: Session) -> None:
    session.info.pop(_TOUCHED, None)
