from datetime import date

from sqlalchemy import Boolean, CheckConstraint, Date, Enum, ForeignKey, Index, String, Text, text
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, TimestampMixin
from app.models.enums import Role


class Department(TimestampMixin, Base):
    __tablename__ = "departments"
    # Names are unique organisation-wide, ignoring case ("HR" and "hr" are the same department).
    __table_args__ = (Index("uq_departments_name_lower", text("lower(name)"), unique=True),)

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(100), nullable=False)
    description: Mapped[str | None] = mapped_column(Text)
    department_head_id: Mapped[int | None] = mapped_column(
        ForeignKey("users.id", ondelete="SET NULL", use_alter=True, name="fk_departments_head_users")
    )

    head: Mapped["User | None"] = relationship(foreign_keys=[department_head_id])


class User(TimestampMixin, Base):
    __tablename__ = "users"
    __table_args__ = (
        CheckConstraint("manager_id IS NULL OR manager_id <> id", name="not_own_manager"),
        Index("ix_users_manager_id", "manager_id"),
        Index("ix_users_department_id", "department_id"),
        # Unique indexes (not column UNIQUE) so migration 0002 can add them to an existing table.
        Index("uq_users_graph_user_id", "graph_user_id", unique=True),
        Index("uq_users_wifi_identity", "wifi_identity", unique=True),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    employee_code: Mapped[str] = mapped_column(String(32), unique=True, nullable=False)
    full_name: Mapped[str] = mapped_column(String(120), nullable=False)
    email: Mapped[str] = mapped_column(String(255), unique=True, nullable=False)  # stored lower-case
    password_hash: Mapped[str] = mapped_column(String(255), nullable=False)
    role: Mapped[Role] = mapped_column(
        Enum(Role, native_enum=False, length=16, create_constraint=True, name="role"), nullable=False
    )
    job_title: Mapped[str | None] = mapped_column(String(120))
    department_id: Mapped[int | None] = mapped_column(ForeignKey("departments.id", ondelete="SET NULL"))
    manager_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    is_active: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    # False for service/administrative accounts that should not appear in attendance statistics.
    attendance_tracked: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    date_joined: Mapped[date] = mapped_column(Date, nullable=False)
    # Automatic attendance identities (optional). Entra object id for Teams presence; 802.1X
    # username or MAC address (normalised lower-case) for office Wi-Fi.
    graph_user_id: Mapped[str | None] = mapped_column(String(64))
    wifi_identity: Mapped[str | None] = mapped_column(String(255))

    department: Mapped[Department | None] = relationship(foreign_keys=[department_id], lazy="joined")
    manager: Mapped["User | None"] = relationship(remote_side=[id], foreign_keys=[manager_id], lazy="joined",
                                                  join_depth=1)
