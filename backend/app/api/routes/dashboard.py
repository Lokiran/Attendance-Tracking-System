"""Team (manager) and organisation (admin) analytics. Scope is always enforced server-side."""

from datetime import date
from typing import Literal

from fastapi import APIRouter, Query

from app.api.deps import DB, AdminUser, ManagerOrAdmin, Paging, Range
from app.core.timeutil import today_local
from app.models import DayStatus
from app.schemas.dashboard import (
    DepartmentStats,
    EmployeeRowsPage,
    ExceptionsOut,
    HierarchyOut,
    ManagerStats,
    OverviewOut,
    TeamMember,
    TrendsOut,
)
from app.services import dashboard, org
from app.services.dashboard import ScopeArgs
from app.services.scope import resolve_scope

router = APIRouter(tags=["dashboards"])

SortKey = Literal["name", "code", "department", "check_in", "net_minutes", "attendance_percentage", "status",
                  "exceptions"]


def _scope(manager_id: int | None, department_id: int | None) -> ScopeArgs:
    return ScopeArgs(manager_id=manager_id, department_id=department_id)


@router.get("/dashboard/overview", response_model=OverviewOut,
            summary="Headline cards: status snapshot for a date + period statistics")
def overview(user: ManagerOrAdmin, db: DB, rng: Range, on_date: date | None = None,
             manager_id: int | None = None, department_id: int | None = None):
    return dashboard.overview(db, user, _scope(manager_id, department_id), rng.start, rng.end,
                              on_date or today_local())


@router.get("/dashboard/employees", response_model=EmployeeRowsPage, summary="Per-employee attendance table")
def employees(user: ManagerOrAdmin, db: DB, rng: Range, paging: Paging, on_date: date | None = None,
              manager_id: int | None = None, department_id: int | None = None,
              search: str | None = Query(None, max_length=100), status: DayStatus | None = None,
              sort: SortKey = "name", order: Literal["asc", "desc"] = "asc", include_inactive: bool = False):
    return dashboard.employee_rows(
        db, user, _scope(manager_id, department_id), rng.start, rng.end, on_date or today_local(),
        search=search, status=status, sort=sort, order=order, page=paging.page, page_size=paging.page_size,
        include_inactive=include_inactive,
    )


@router.get("/dashboard/trends", response_model=TrendsOut)
def trends(user: ManagerOrAdmin, db: DB, rng: Range, granularity: Literal["day", "week", "month"] = "day",
           manager_id: int | None = None, department_id: int | None = None):
    return dashboard.trends(db, user, _scope(manager_id, department_id), rng.start, rng.end, granularity)


@router.get("/dashboard/exceptions", response_model=ExceptionsOut)
def exceptions(user: ManagerOrAdmin, db: DB, rng: Range, paging: Paging, manager_id: int | None = None,
               department_id: int | None = None, exception_type: str | None = Query(None, max_length=40)):
    return dashboard.exceptions(db, user, _scope(manager_id, department_id), rng.start, rng.end,
                                exception_type=exception_type, page=paging.page, page_size=paging.page_size)


@router.get("/dashboard/departments", response_model=list[DepartmentStats])
def departments(user: AdminUser, db: DB, rng: Range, on_date: date | None = None):
    return dashboard.department_comparison(db, user, rng.start, rng.end, on_date or today_local())


@router.get("/dashboard/managers", response_model=list[ManagerStats])
def managers(user: AdminUser, db: DB, rng: Range, on_date: date | None = None, department_id: int | None = None):
    return dashboard.manager_comparison(db, user, rng.start, rng.end, on_date or today_local(), department_id)


@router.get("/org/hierarchy", response_model=HierarchyOut)
def hierarchy(user: AdminUser, db: DB, include_inactive: bool = False):
    return org.hierarchy(db, include_inactive)


@router.get("/team/members", response_model=list[TeamMember], summary="People in my reporting scope")
def team_members(user: ManagerOrAdmin, db: DB, manager_id: int | None = None, department_id: int | None = None,
                 include_inactive: bool = False):
    people = resolve_scope(db, user, manager_id=manager_id, department_id=department_id,
                           include_inactive=include_inactive, tracked_only=False)
    return [
        TeamMember(id=p.id, employee_code=p.employee_code, full_name=p.full_name, role=p.role,
                   job_title=p.job_title, department_name=p.department.name if p.department else None,
                   manager_id=p.manager_id, manager_name=p.manager.full_name if p.manager else None,
                   is_active=p.is_active)
        for p in people
    ]
