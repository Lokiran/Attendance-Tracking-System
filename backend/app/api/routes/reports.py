"""Exports (CSV, and Excel / PDF for one employee). Same authorisation as the dashboards; every export is
audited."""

import logging

from fastapi import APIRouter, Query
from fastapi.responses import Response

from app.api.deps import DB, AdminUser, CurrentUser, Ctx, ManagerOrAdmin, Range
from app.core.errors import AppError
from app.services import attendance_export, audit, reports

router = APIRouter(prefix="/reports", tags=["reports"])
logger = logging.getLogger("ats.reports")


def _csv(db, user, ctx, report: str, params: dict, build) -> Response:
    try:
        filename, body = build()
    except AppError:
        raise
    except Exception:
        logger.exception("report generation failed: %s", report)
        raise AppError("The report could not be generated. Please try again.", code="report_failed",
                       status_code=500) from None
    audit.record(db, actor_id=user.id, action="REPORT_EXPORTED", entity_type="report", entity_id=report,
                 after=params, ctx=ctx)
    db.commit()
    return Response(content=body, media_type="text/csv; charset=utf-8",
                    headers={"Content-Disposition": f'attachment; filename="{filename}"',
                             "Cache-Control": "no-store"})


@router.get("/employee-attendance.csv", summary="Daily attendance for one employee")
def employee_attendance(employee_id: int, rng: Range, user: CurrentUser, db: DB, ctx: Ctx):
    params = {"employee_id": employee_id, "start": rng.start, "end": rng.end}
    return _csv(db, user, ctx, "employee-attendance", params,
                lambda: reports.employee_attendance(db, user, employee_id, rng.start, rng.end))


XLSX = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"


def _file(db, user, ctx, report: str, params: dict, build, media_type: str) -> Response:
    try:
        filename, body = build()
    except AppError:
        raise
    except Exception:
        logger.exception("report generation failed: %s", report)
        raise AppError("The report could not be generated. Please try again.", code="report_failed",
                       status_code=500) from None
    audit.record(db, actor_id=user.id, action="REPORT_EXPORTED", entity_type="report", entity_id=report,
                 after=params, ctx=ctx)
    db.commit()
    return Response(content=body, media_type=media_type,
                    headers={"Content-Disposition": f'attachment; filename="{filename}"',
                             "Cache-Control": "no-store"})


@router.get("/employee-attendance.xlsx", summary="One employee's attendance as an Excel workbook")
def employee_attendance_xlsx(employee_id: int, rng: Range, user: CurrentUser, db: DB, ctx: Ctx):
    def build():
        report = attendance_export.gather(db, user, employee_id, rng.start, rng.end)
        return attendance_export.filename(report, "xlsx"), attendance_export.to_xlsx(report)
    params = {"employee_id": employee_id, "start": rng.start, "end": rng.end, "format": "xlsx"}
    return _file(db, user, ctx, "employee-attendance", params, build, XLSX)


@router.get("/employee-attendance.pdf", summary="One employee's attendance as a branded PDF")
def employee_attendance_pdf(employee_id: int, rng: Range, user: CurrentUser, db: DB, ctx: Ctx):
    def build():
        report = attendance_export.gather(db, user, employee_id, rng.start, rng.end)
        return attendance_export.filename(report, "pdf"), attendance_export.to_pdf(report)
    params = {"employee_id": employee_id, "start": rng.start, "end": rng.end, "format": "pdf"}
    return _file(db, user, ctx, "employee-attendance", params, build, "application/pdf")


@router.get("/team-attendance.csv", summary="Per-employee summary for my scope")
def team_attendance(rng: Range, user: ManagerOrAdmin, db: DB, ctx: Ctx, manager_id: int | None = None,
                    department_id: int | None = None):
    params = {"start": rng.start, "end": rng.end, "manager_id": manager_id, "department_id": department_id}
    return _csv(db, user, ctx, "team-attendance", params,
                lambda: reports.team_attendance(db, user, rng.start, rng.end, manager_id, department_id))


@router.get("/monthly-attendance.csv", summary="Monthly per-employee summary for my scope")
def monthly_attendance(user: ManagerOrAdmin, db: DB, ctx: Ctx, year: int = Query(ge=2000, le=2100),
                       month: int = Query(ge=1, le=12), manager_id: int | None = None,
                       department_id: int | None = None):
    params = {"year": year, "month": month, "manager_id": manager_id, "department_id": department_id}
    return _csv(db, user, ctx, "monthly-attendance", params,
                lambda: reports.monthly_attendance(db, user, year, month, manager_id, department_id))


@router.get("/department-attendance.csv", summary="Department comparison (admin)")
def department_attendance(rng: Range, user: AdminUser, db: DB, ctx: Ctx):
    return _csv(db, user, ctx, "department-attendance", {"start": rng.start, "end": rng.end},
                lambda: reports.department_attendance(db, user, rng.start, rng.end))


@router.get("/exceptions.csv", summary="Attendance exceptions for my scope")
def exceptions(rng: Range, user: ManagerOrAdmin, db: DB, ctx: Ctx, manager_id: int | None = None,
               department_id: int | None = None):
    params = {"start": rng.start, "end": rng.end, "manager_id": manager_id, "department_id": department_id}
    return _csv(db, user, ctx, "exceptions", params,
                lambda: reports.exceptions_report(db, user, rng.start, rng.end, manager_id, department_id))
