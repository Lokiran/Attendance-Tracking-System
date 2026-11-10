"""Automatic attendance endpoints. Phase 1: the settings (HR admin only)."""

from fastapi import APIRouter

from app.api.deps import DB, AdminUser, Ctx
from app.schemas.auto_attendance import AutoAttendanceSettingsOut, AutoAttendanceSettingsUpdate
from app.services import auto_settings
from app.services.policy import get_policy

router = APIRouter(prefix="/admin/auto-attendance", tags=["automatic attendance"])


@router.get("/settings", response_model=AutoAttendanceSettingsOut,
            summary="Rules used by the automatic attendance jobs (HR admin)")
def read_settings(_: AdminUser, db: DB):
    return get_policy(db)


@router.put("/settings", response_model=AutoAttendanceSettingsOut,
            summary="Change automatic attendance rules (HR admin, audited)")
def update_settings(body: AutoAttendanceSettingsUpdate, admin: AdminUser, db: DB, ctx: Ctx):
    return auto_settings.update(db, admin, body.model_dump(exclude_unset=True), ctx)
