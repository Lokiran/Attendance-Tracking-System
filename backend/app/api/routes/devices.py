"""Office sites and the device register (HR admin). Wi-Fi presence is matched through these devices."""

from fastapi import APIRouter, File, Query, UploadFile
from sqlalchemy import select

from app.api.deps import DB, AdminUser, Ctx, Paging
from app.models import OfficeSite
from app.schemas.common import Page
from app.schemas.devices import (
    DeviceCreate,
    DeviceImportResult,
    DeviceOut,
    DeviceUpdate,
    OfficeSiteCreate,
    OfficeSiteOut,
    OfficeSiteUpdate,
    RegisterSummary,
)
from app.services import devices

router = APIRouter(tags=["devices"])


def _out(d) -> DeviceOut:
    out = DeviceOut.model_validate(d)
    out.random_mac = devices.is_random_mac(d.mac_address)
    return out


# ------------------------------------------------------------------ offices

@router.get("/admin/sites", response_model=list[OfficeSiteOut], summary="Offices (HR admin)")
def list_sites(_: AdminUser, db: DB):
    return list(db.scalars(select(OfficeSite).order_by(OfficeSite.is_active.desc(), OfficeSite.name)))


@router.post("/admin/sites", response_model=OfficeSiteOut, status_code=201)
def create_site(body: OfficeSiteCreate, admin: AdminUser, db: DB, ctx: Ctx):
    return devices.create_site(db, admin, body.model_dump(), ctx)


@router.patch("/admin/sites/{site_id}", response_model=OfficeSiteOut)
def update_site(site_id: int, body: OfficeSiteUpdate, admin: AdminUser, db: DB, ctx: Ctx):
    return devices.update_site(db, admin, site_id, body.model_dump(exclude_unset=True), ctx)


# ------------------------------------------------------------------ devices

@router.get("/admin/devices", response_model=Page[DeviceOut], summary="Device register (HR admin)")
def list_devices(_: AdminUser, db: DB, paging: Paging, search: str | None = Query(None, max_length=100),
                 assigned: bool | None = None, has_mac: bool | None = None, active: bool | None = True):
    rows, total = devices.list_devices(db, search=search, assigned=assigned, has_mac=has_mac, active=active,
                                       offset=paging.offset, limit=paging.page_size)
    return Page(items=[_out(d) for d in rows], total=total, page=paging.page, page_size=paging.page_size)


@router.get("/admin/devices/summary", response_model=RegisterSummary,
            summary="How ready the register is for Wi-Fi matching")
def register_summary(_: AdminUser, db: DB):
    return devices.register_summary(db)


@router.post("/admin/devices", response_model=DeviceOut, status_code=201)
def create_device(body: DeviceCreate, admin: AdminUser, db: DB, ctx: Ctx):
    return _out(devices.create_device(db, admin, body.model_dump(), ctx))


@router.patch("/admin/devices/{device_id}", response_model=DeviceOut)
def update_device(device_id: int, body: DeviceUpdate, admin: AdminUser, db: DB, ctx: Ctx):
    return _out(devices.update_device(db, admin, device_id, body.model_dump(exclude_unset=True), ctx))


@router.post("/admin/devices/import", response_model=DeviceImportResult,
             summary="Import an asset list or Intune export (CSV / Excel): checked first, written with apply=true")
def import_devices(admin: AdminUser, db: DB, ctx: Ctx, file: UploadFile = File(...),
                   apply: bool = Query(False, description="false: check only (nothing is written)")):
    content = file.file.read(devices.MAX_UPLOAD_BYTES + 1)
    return devices.import_devices(db, admin, file.filename or "devices", content, apply, ctx)
