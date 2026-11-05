"""Organisation hierarchy: Organisation -> Department -> top-of-department people -> reporting tree."""

from collections import defaultdict

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.models import Department, User


def _node(u: User, children: dict[int, list[User]], seen: set[int]) -> dict:
    seen.add(u.id)
    kids = [c for c in children.get(u.id, []) if c.department_id == u.department_id and c.id not in seen]
    return {
        "id": u.id, "employee_code": u.employee_code, "full_name": u.full_name, "role": u.role,
        "job_title": u.job_title, "is_active": u.is_active,
        "direct_reports": len(children.get(u.id, [])),
        "children": [_node(c, children, seen) for c in sorted(kids, key=lambda x: x.full_name)],
    }


def hierarchy(db: Session, include_inactive: bool = False) -> dict:
    stmt = select(User)
    if not include_inactive:
        stmt = stmt.where(User.is_active.is_(True))
    users = list(db.scalars(stmt.order_by(User.full_name)).unique())
    by_id = {u.id: u for u in users}
    children: dict[int, list[User]] = defaultdict(list)
    for u in users:
        if u.manager_id in by_id:
            children[u.manager_id].append(u)

    departments = []
    for dept in [*db.scalars(select(Department).order_by(Department.name)), None]:
        dept_id = dept.id if dept else None
        members = [u for u in users if u.department_id == dept_id]
        if dept is None and not members:
            continue
        # Roots: members whose manager is absent or sits in another department (cross-department reporting).
        roots = [u for u in members if u.manager_id not in by_id or by_id[u.manager_id].department_id != dept_id]
        seen: set[int] = set()
        departments.append({
            "id": dept_id, "name": dept.name if dept else "Unassigned",
            "description": dept.description if dept else None,
            "head": {"id": dept.head.id, "full_name": dept.head.full_name} if dept and dept.head else None,
            "member_count": len(members),
            "nodes": [_node(r, children, seen) for r in roots],
        })
    return {"total_people": len(users), "departments": departments}
