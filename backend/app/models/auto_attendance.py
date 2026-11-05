"""Automatic attendance: raw signals, computed per-day results, worker health and ingest keys."""

from datetime import date, datetime

from sqlalchemy import JSON, Boolean, Date, Enum, ForeignKey, Index, Integer, String, Text, UniqueConstraint
from sqlalchemy.orm import Mapped, mapped_column

from app.core import clock
from app.db.base import Base, TimestampMixin
from app.db.types import UTCDateTime
from app.models.enums import AutoSource, RequestStatus, SignalSource, WorkLocation
from app.models.system import BigId


class Signal(Base):
    """One presence observation: a Wi-Fi CONNECT/DISCONNECT or a Teams availability poll."""

    __tablename__ = "signals"
    __table_args__ = (
        Index("ix_signals_employee_seen", "employee_id", "seen_at"),
        Index("ix_signals_seen_at", "seen_at"),  # retention purge
        # Identical events (same person, source, event and instant) are stored once.
        UniqueConstraint("employee_id", "source", "event", "seen_at", name="uq_signals_dedupe"),
    )

    id: Mapped[int] = mapped_column(BigId, primary_key=True)
    employee_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), nullable=False)
    source: Mapped[SignalSource] = mapped_column(
        Enum(SignalSource, native_enum=False, length=8, create_constraint=True, name="signal_source"), nullable=False
    )
    # WIFI: CONNECT | DISCONNECT. TEAMS: Graph availability (Available, Busy, Away, Offline, ...).
    event: Mapped[str] = mapped_column(String(32), nullable=False)
    # WIFI: access point / SSID / "OFFICE_IP" (low confidence). TEAMS: Graph activity.
    detail: Mapped[str | None] = mapped_column(String(255))
    seen_at: Mapped[datetime] = mapped_column(UTCDateTime, nullable=False)
    received_at: Mapped[datetime] = mapped_column(UTCDateTime, default=lambda: clock.utcnow(), nullable=False)


class AutoAttendanceDay(TimestampMixin, Base):
    """The engine's result for one person and office-local work date.

    Shadow rows never affect official attendance. Official rows own at most one AUTO_* session
    (``session_id``); a manual session for the same day supersedes it.
    """

    __tablename__ = "auto_attendance_days"
    __table_args__ = (
        UniqueConstraint("employee_id", "work_date", name="uq_auto_attendance_days_employee_day"),
        Index("ix_auto_attendance_days_review", "needs_review", "work_date"),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    employee_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), nullable=False)
    work_date: Mapped[date] = mapped_column(Date, nullable=False)
    check_in: Mapped[datetime | None] = mapped_column(UTCDateTime)
    check_out: Mapped[datetime | None] = mapped_column(UTCDateTime)
    location: Mapped[WorkLocation | None] = mapped_column(
        Enum(WorkLocation, native_enum=False, length=8, create_constraint=True, name="work_location")
    )
    source: Mapped[AutoSource | None] = mapped_column(
        Enum(AutoSource, native_enum=False, length=16, create_constraint=True, name="auto_source")
    )
    active_minutes: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    signal_count: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    needs_review: Mapped[bool] = mapped_column(Boolean, default=False, nullable=False)
    review_reasons: Mapped[list[str]] = mapped_column(JSON, default=list, nullable=False)
    is_shadow: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    superseded: Mapped[bool] = mapped_column(Boolean, default=False, nullable=False)
    superseded_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    session_id: Mapped[int | None] = mapped_column(ForeignKey("attendance_sessions.id", ondelete="SET NULL"))
    review_status: Mapped[RequestStatus | None] = mapped_column(
        Enum(RequestStatus, native_enum=False, length=16, create_constraint=True, name="review_status")
    )
    reviewed_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    reviewed_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    review_comment: Mapped[str | None] = mapped_column(Text)
    computed_at: Mapped[datetime | None] = mapped_column(UTCDateTime)


class WorkerJob(Base):
    """Health of a background job, shown to admins (one row per job name)."""

    __tablename__ = "worker_jobs"

    name: Mapped[str] = mapped_column(String(64), primary_key=True)
    last_run_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    last_success_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    last_error: Mapped[str | None] = mapped_column(Text)
    last_duration_ms: Mapped[int | None] = mapped_column(Integer)
    consecutive_failures: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    run_count: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    details: Mapped[dict | None] = mapped_column(JSON)
    updated_at: Mapped[datetime] = mapped_column(UTCDateTime, default=lambda: clock.utcnow(),
                                                 onupdate=lambda: clock.utcnow(), nullable=False)


class IngestApiKey(Base):
    """API key for the Wi-Fi ingest endpoint. Only a SHA-256 hash is stored; at most two are active."""

    __tablename__ = "ingest_api_keys"

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(64), nullable=False)
    key_hash: Mapped[str] = mapped_column(String(64), unique=True, nullable=False)
    key_prefix: Mapped[str] = mapped_column(String(12), nullable=False)  # for identification only
    created_at: Mapped[datetime] = mapped_column(UTCDateTime, default=lambda: clock.utcnow(), nullable=False)
    created_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    last_used_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    revoked_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
