from datetime import datetime

from sqlalchemy import DDL, CheckConstraint, Enum, ForeignKey, Index, Integer, String, Text, event, text
from sqlalchemy.orm import Mapped, mapped_column

from app.db.base import Base, TimestampMixin
from app.db.types import UTCDateTime
from app.models.enums import SessionSource, SessionStatus, WorkLocation


class AttendanceSession(TimestampMixin, Base):
    """One check-in/check-out pair. The workday is the office-local date of ``check_in_at``."""

    __tablename__ = "attendance_sessions"
    __table_args__ = (
        CheckConstraint("check_out_at IS NULL OR check_out_at > check_in_at", name="checkout_after_checkin"),
        CheckConstraint("break_minutes >= 0", name="break_non_negative"),
        CheckConstraint("net_minutes IS NULL OR net_minutes >= 0", name="net_non_negative"),
        CheckConstraint("status <> 'OPEN' OR check_out_at IS NULL", name="open_has_no_checkout"),
        CheckConstraint(
            "status <> 'COMPLETED' OR (check_out_at IS NOT NULL AND net_minutes IS NOT NULL)",
            name="completed_has_totals",
        ),
        # At most one unfinished session per employee (also guards concurrent check-ins).
        Index(
            "uq_attendance_one_open_session",
            "employee_id",
            unique=True,
            postgresql_where=text("status = 'OPEN'"),
            sqlite_where=text("status = 'OPEN'"),
        ),
        Index("ix_attendance_employee_checkin", "employee_id", "check_in_at"),
        Index("ix_attendance_checkin", "check_in_at"),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    employee_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), nullable=False)
    check_in_at: Mapped[datetime] = mapped_column(UTCDateTime, nullable=False)
    check_out_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    break_minutes: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    net_minutes: Mapped[int | None] = mapped_column(Integer)
    status: Mapped[SessionStatus] = mapped_column(
        Enum(SessionStatus, native_enum=False, length=20, create_constraint=True, name="session_status"),
        nullable=False,
    )
    source: Mapped[SessionSource] = mapped_column(
        Enum(SessionSource, native_enum=False, length=16, create_constraint=True, name="session_source"),
        nullable=False, default=SessionSource.WEB,
    )
    # Office or WFH, picked by the employee at check-in (a WFO signal); NULL for sessions without a choice.
    work_location: Mapped[WorkLocation | None] = mapped_column(
        Enum(WorkLocation, native_enum=False, length=8, create_constraint=True, name="work_location"))
    notes: Mapped[str | None] = mapped_column(String(500))
    voided_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    void_reason: Mapped[str | None] = mapped_column(Text)


# PostgreSQL backstop against overlapping completed sessions (range [in, out) per employee).
# Open sessions use an empty range here; they are guarded by the partial unique index plus
# the service-level overlap check performed under a per-employee row lock.
NO_OVERLAP_SQL = (
    "ALTER TABLE attendance_sessions ADD CONSTRAINT ex_attendance_no_overlap "
    "EXCLUDE USING gist (employee_id WITH =, "
    "tstzrange(check_in_at, COALESCE(check_out_at, check_in_at), '[)') WITH &&) "
    "WHERE (status <> 'VOIDED')"
)

event.listen(
    AttendanceSession.__table__,
    "before_create",
    DDL("CREATE EXTENSION IF NOT EXISTS btree_gist").execute_if(dialect="postgresql"),
)
event.listen(AttendanceSession.__table__, "after_create", DDL(NO_OVERLAP_SQL).execute_if(dialect="postgresql"))
