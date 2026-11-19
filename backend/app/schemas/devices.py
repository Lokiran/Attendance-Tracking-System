from datetime import datetime

from pydantic import BaseModel, Field

from app.schemas.common import ORMModel


class OfficeSiteCreate(BaseModel):
    name: str = Field(min_length=2, max_length=100)
    code: str = Field(min_length=1, max_length=16, pattern=r"^[A-Za-z0-9_-]+$")
    network_name: str | None = Field(None, max_length=64)
    address: str | None = Field(None, max_length=500)


class OfficeSiteUpdate(BaseModel):
    name: str | None = Field(None, min_length=2, max_length=100)
    code: str | None = Field(None, min_length=1, max_length=16, pattern=r"^[A-Za-z0-9_-]+$")
    network_name: str | None = Field(None, max_length=64)
    address: str | None = Field(None, max_length=500)
    is_active: bool | None = None


class OfficeSiteOut(ORMModel):
    id: int
    name: str
    code: str
    network_name: str | None
    address: str | None
    is_active: bool


class DeviceOwner(ORMModel):
    id: int
    employee_code: str
    full_name: str


class DeviceCreate(BaseModel):
    serial_number: str | None = Field(None, max_length=64)
    mac_address: str | None = Field(None, max_length=32)
    asset_tag: str | None = Field(None, max_length=64)
    hostname: str | None = Field(None, max_length=64)
    model: str | None = Field(None, max_length=120)
    user_id: int | None = None
    notes: str | None = Field(None, max_length=1000)


class DeviceUpdate(DeviceCreate):
    is_active: bool | None = None


class DeviceOut(ORMModel):
    id: int
    serial_number: str | None
    mac_address: str | None
    random_mac: bool = False          # a private / random Wi-Fi address: it does not identify the laptop
    asset_tag: str | None
    hostname: str | None
    model: str | None
    user: DeviceOwner | None
    is_active: bool
    source: str
    notes: str | None
    updated_at: datetime


class RegisterSummary(BaseModel):
    devices: int
    with_mac: int
    without_mac: int
    unassigned: int
    random_mac: int
    people_tracked: int
    people_with_device: int       # tracked people with at least one active device that has a MAC address
    sites: int


class DeviceImportItem(BaseModel):
    row: int
    action: str                   # create | update | unchanged | error
    serial_number: str | None
    mac_address: str | None
    hostname: str | None
    owner: str | None
    owner_code: str | None
    owner_written: str | None
    changes: list[str]
    problems: list[str]


class DeviceImportResult(BaseModel):
    applied: bool
    sheet: str | None
    columns: dict[str, str]
    rows: int
    blank_rows: int
    created: int
    updated: int
    unchanged: int
    errors: int
    with_mac: int
    random_mac: int
    unknown_owners: list[dict]
    items: list[DeviceImportItem]
