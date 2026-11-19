"""Offices and the devices that identify people on the office Wi-Fi.

With one shared Wi-Fi password the network only sees devices, never people, so presence is matched through
this register: a device (normally a company laptop) belongs to one person, and its Wi-Fi MAC address is what
the Wi-Fi logs show. The company tracks assets by serial number, so the serial number is the device's key:
an asset list fills serial → person, and a later list (Intune export, a script on the laptops) fills serial →
MAC address. Either can arrive first.

Only presence and duration are derived from these devices: no traffic, no URLs, no browsing data.
"""

from sqlalchemy import Boolean, CheckConstraint, ForeignKey, Index, String, Text, text
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db.base import Base, TimestampMixin
from app.models.organization import User


class OfficeSite(TimestampMixin, Base):
    """An office location with its own Wi-Fi network (reports are per office)."""

    __tablename__ = "office_sites"
    __table_args__ = (
        Index("uq_office_sites_name_lower", text("lower(name)"), unique=True),
        Index("uq_office_sites_code_lower", text("lower(code)"), unique=True),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(100), nullable=False)
    code: Mapped[str] = mapped_column(String(16), nullable=False)        # short label: "BLR-1"
    # The Wi-Fi network name (SSID) the office's logs use; matches the site of each log line.
    network_name: Mapped[str | None] = mapped_column(String(64))
    address: Mapped[str | None] = mapped_column(Text)
    is_active: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)


class Device(TimestampMixin, Base):
    """A device in the register: who it belongs to and how the Wi-Fi logs recognise it."""

    __tablename__ = "devices"
    __table_args__ = (
        CheckConstraint("serial_number IS NOT NULL OR mac_address IS NOT NULL", name="has_identifier"),
        Index("uq_devices_serial_number", "serial_number", unique=True),   # stored upper-case
        Index("uq_devices_mac_address", "mac_address", unique=True),       # stored aa:bb:cc:dd:ee:ff
        Index("ix_devices_user_id", "user_id"),
        Index("ix_devices_hostname_lower", text("lower(hostname)")),
    )

    id: Mapped[int] = mapped_column(primary_key=True)
    serial_number: Mapped[str | None] = mapped_column(String(64))
    asset_tag: Mapped[str | None] = mapped_column(String(64))
    mac_address: Mapped[str | None] = mapped_column(String(17))
    hostname: Mapped[str | None] = mapped_column(String(64))           # computer name, as in DHCP / Intune
    model: Mapped[str | None] = mapped_column(String(120))
    user_id: Mapped[int | None] = mapped_column(ForeignKey("users.id", ondelete="SET NULL"))
    is_active: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    source: Mapped[str] = mapped_column(String(16), default="MANUAL", nullable=False)  # MANUAL, IMPORT, LEGACY
    notes: Mapped[str | None] = mapped_column(Text)

    user: Mapped[User | None] = relationship(foreign_keys=[user_id], lazy="joined")
