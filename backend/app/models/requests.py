from datetime import date, datetime

from sqlalchemy import JSON, CheckConstraint, Date, Enum, ForeignKey, Index, Integer, String, Text
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, TimestampMixin
from app.db.types import UTCDateTime
from app.models.enums import CorrectionType, LeaveType, RequestStatus
from app.models.organization import User


def _status_col():
    return mapped_column(
        Enum(RequestStatus, native_enum=False, length=16, create_constraint=True, name="request_status"),
        nullable=False, default=RequestStatus.PENDING,
    )


class LeaveRecord(TimestampMixin, Base):
    """Legacy table from the retired leave feature. Nothing in the app reads or writes it any more;
    the mapping stays so the schema matches the migrations and existing rows are not dropped
    without an explicit, approved migration."""

    __tablename__ = "leave_records"
    __table_args__ = (
        CheckConstraint("end_date >= start_date", name="date_order"),
        Index("ix_leave_employee_dates", "employee_id", "start_date", "end_date"),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    employee_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), nullable=False)
    start_date: Mapped[date] = mapped_column(Date, nullable=False)
    end_date: Mapped[date] = mapped_column(Date, nullable=False)
    leave_type: Mapped[LeaveType] = mapped_column(
        Enum(LeaveType, native_enum=False, length=16, create_constraint=True, name="leave_type"), nullable=False
    )
    status: Mapped[RequestStatus] = _status_col()
    reason: Mapped[str | None] = mapped_column(Text)
    reviewed_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    reviewed_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    reviewer_comment: Mapped[str | None] = mapped_column(Text)

    employee: Mapped[User] = relationship(foreign_keys=[employee_id], lazy="joined")
    reviewed_by: Mapped[User | None] = relationship(foreign_keys=[reviewed_by_id], lazy="joined")


class AttendanceCorrection(TimestampMixin, Base):
    """Employee request to add or fix attendance. Approval never edits the original session:
    the original is VOIDED and a new CORRECTION-sourced session is created."""

    __tablename__ = "attendance_corrections"
    __table_args__ = (
        CheckConstraint("requested_check_out_at > requested_check_in_at", name="requested_order"),
        CheckConstraint("requested_break_minutes >= 0", name="requested_break_non_negative"),
        Index("ix_corrections_employee_date", "employee_id", "attendance_date"),
        Index("ix_corrections_status", "status"),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    employee_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), nullable=False)
    attendance_date: Mapped[date] = mapped_column(Date, nullable=False)
    request_type: Mapped[CorrectionType] = mapped_column(
        Enum(CorrectionType, native_enum=False, length=20, create_constraint=True, name="correction_type"),
        nullable=False,
    )
    original_session_id: Mapped[int | None] = mapped_column(
        ForeignKey("attendance_sessions.id", ondelete="SET NULL")
    )
    original_values: Mapped[dict | None] = mapped_column(JSON)
    requested_check_in_at: Mapped[datetime] = mapped_column(UTCDateTime, nullable=False)
    requested_check_out_at: Mapped[datetime] = mapped_column(UTCDateTime, nullable=False)
    requested_break_minutes: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    reason: Mapped[str] = mapped_column(String(1000), nullable=False)
    status: Mapped[RequestStatus] = _status_col()
    reviewed_by_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    reviewer_comment: Mapped[str | None] = mapped_column(Text)
    reviewed_at: Mapped[datetime | None] = mapped_column(UTCDateTime)
    resulting_session_id: Mapped[int | None] = mapped_column(
        ForeignKey("attendance_sessions.id", ondelete="SET NULL")
    )

    employee: Mapped[User] = relationship(foreign_keys=[employee_id], lazy="joined")
    reviewed_by: Mapped[User | None] = relationship(foreign_keys=[reviewed_by_id], lazy="joined")
