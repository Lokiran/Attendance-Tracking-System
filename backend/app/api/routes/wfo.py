"""WFO (work from office) endpoints. Role scope is enforced in the backend query; out-of-scope employees
get 403 (as the WFO API specifies)."""

from datetime import date

from fastapi import APIRouter, File, Query, Response, UploadFile

from app.api.deps import DB, AdminUser, Ctx, CurrentUser, ManagerOrAdmin
from app.core.errors import NotFound
from app.models import User, WorkMode
from app.schemas.wfo import (
    CommitRequest,
    CreateEmployeesRequest,
    EmployeeWfoOut,
    OrgWfoOut,
    OverrideOut,
    OverrideRequest,
    TeamWfoOut,
    UploadOut,
    WfoPolicyOut,
    WfoPolicyUpdate,
)
from app.services import audit, wfo, wfo_export, wfo_upload
from app.services.policy import get_policy

router = APIRouter(tags=["wfo"])
Month = Query(None, pattern=r"^\d{4}-\d{2}$", description="YYYY-MM (default: this month)")


@router.get("/me/wfo", response_model=EmployeeWfoOut, summary="My WFO days, weekly progress and compliance")
def my_wfo(user: CurrentUser, db: DB, month: str | None = Month):
    return wfo.employee_month(db, user, month)


@router.get("/team/wfo", response_model=TeamWfoOut, summary="WFO for my team (manager: whole reporting line + me)")
def team_wfo(user: ManagerOrAdmin, db: DB, month: str | None = Month, employee_id: int | None = None):
    return wfo.team_month(db, user, month, employee_id)


@router.get("/team/wfo/employees/{employee_id}", response_model=EmployeeWfoOut,
            summary="One team member's WFO month (403 outside my team)")
def team_member_wfo(employee_id: int, user: ManagerOrAdmin, db: DB, month: str | None = Month):
    return wfo.employee_month(db, wfo.scoped_employee(db, user, employee_id), month)


@router.get("/org/wfo", response_model=OrgWfoOut, summary="Organisation WFO grouped by manager (admin)")
def org_wfo(admin: AdminUser, db: DB, month: str | None = Month, manager_id: int | None = None,
            department_id: int | None = None):
    return wfo.org_month(db, admin, month, manager_id, department_id)


@router.get("/org/wfo/export.xlsx", summary="Download the WFO report as Excel (admin)")
def org_wfo_export(admin: AdminUser, db: DB, ctx: Ctx, month: str | None = Month, manager_id: int | None = None,
                   department_id: int | None = None):
    filename, content = wfo_export.build(db, admin, month, manager_id, department_id)
    audit.record(db, actor_id=admin.id, action="REPORT_EXPORTED", entity_type="report", entity_id="wfo",
                 after={"month": month, "manager_id": manager_id, "department_id": department_id}, ctx=ctx)
    db.commit()
    return Response(content, media_type="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    headers={"Content-Disposition": f'attachment; filename="{filename}"', "Cache-Control": "no-store"})


def _override_out(row) -> dict:
    return {"employee_id": row.employee_id, "work_date": row.work_date, "work_mode": row.work_mode,
            "wfo_source": row.wfo_source, "overridden": row.override_work_mode is not None,
            "override_note": row.override_note, "overridden_at": row.overridden_at}


def _employee(db, employee_id: int) -> User:
    employee = db.get(User, employee_id)
    if employee is None:
        raise NotFound("Employee not found")
    return employee


@router.put("/admin/attendance/{employee_id}/{work_date}/work-mode", response_model=OverrideOut,
            summary="Override a day's work mode (admin, audited)")
def override_work_mode(employee_id: int, work_date: date, body: OverrideRequest, admin: AdminUser, db: DB, ctx: Ctx):
    row = wfo.set_override(db, admin, _employee(db, employee_id), work_date, body.work_mode, body.note, ctx)
    return _override_out(row)


@router.delete("/admin/attendance/{employee_id}/{work_date}/work-mode", response_model=OverrideOut,
               summary="Remove a day's override (admin, audited)")
def clear_work_mode_override(employee_id: int, work_date: date, admin: AdminUser, db: DB, ctx: Ctx):
    return _override_out(wfo.clear_override(db, admin, _employee(db, employee_id), work_date, ctx))


@router.get("/admin/wfo/policy", response_model=WfoPolicyOut, summary="WFO policy (admin)")
def read_wfo_policy(_: AdminUser, db: DB):
    return wfo.policy_dict(wfo.policy_of(get_policy(db)))


@router.put("/admin/wfo/policy", response_model=WfoPolicyOut, summary="Change the WFO policy (admin, audited)")
def update_wfo_policy(body: WfoPolicyUpdate, admin: AdminUser, db: DB, ctx: Ctx):
    return wfo.policy_dict(wfo.update_policy(db, admin, body.model_dump(exclude_unset=True), ctx))


# ------------------------------------------------------------------ attendance / badge uploads (admin)

def _upload_out(db, upload) -> UploadOut:
    out = UploadOut.model_validate(upload)
    if upload.uploaded_by_id:
        u = db.get(User, upload.uploaded_by_id)
        out.uploaded_by_name = u.full_name if u else None
    return out


@router.post("/admin/attendance-uploads", response_model=UploadOut, status_code=201,
             summary="Upload an attendance / badge file (CSV or Excel) and get a preview; nothing is applied yet")
def upload_attendance(admin: AdminUser, db: DB, ctx: Ctx, file: UploadFile = File(...),
                      sheet: str | None = Query(None, max_length=100, description="Excel sheet (default: the first "
                                                "with an employee and a date column)"),
                      presence_mode: WorkMode = Query(WorkMode.OFFICE, description="For a presence list (no work "
                                                      "mode column): OFFICE or WFH")):
    # A plain def runs in the thread pool, so parsing and database work never block the event loop.
    content = file.file.read(wfo_upload.MAX_UPLOAD_BYTES + 1)  # one byte over the limit is enough to refuse
    return _upload_out(db, wfo_upload.preview(db, admin, file.filename or "upload", content, ctx, sheet=sheet,
                                              presence_mode=presence_mode))


@router.get("/admin/attendance-uploads", response_model=list[UploadOut], summary="Recent uploads (admin)")
def list_uploads(_: AdminUser, db: DB):
    return [_upload_out(db, u) for u in wfo_upload.recent(db)]


@router.get("/admin/attendance-uploads/{upload_id}", response_model=UploadOut)
def get_upload(upload_id: int, _: AdminUser, db: DB):
    return _upload_out(db, wfo_upload.get(db, upload_id))


@router.post("/admin/attendance-uploads/{upload_id}/commit", response_model=UploadOut,
             summary="Apply a previewed upload (upserts per employee and date)")
def commit_upload(upload_id: int, body: CommitRequest, admin: AdminUser, db: DB, ctx: Ctx):
    return _upload_out(db, wfo_upload.commit(db, admin, upload_id, body.overwrite_overrides, ctx))


@router.post("/admin/attendance-uploads/{upload_id}/discard", response_model=UploadOut)
def discard_upload(upload_id: int, admin: AdminUser, db: DB, ctx: Ctx):
    return _upload_out(db, wfo_upload.discard(db, admin, upload_id, ctx))


@router.post("/admin/attendance-uploads/{upload_id}/create-employees", response_model=UploadOut,
             summary="Add the upload's unknown employee IDs as employees, then re-check its rows")
def create_upload_employees(upload_id: int, body: CreateEmployeesRequest, admin: AdminUser, db: DB, ctx: Ctx):
    return _upload_out(db, wfo_upload.create_employees(db, admin, upload_id, body.employee_refs, ctx))
