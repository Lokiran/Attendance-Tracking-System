from sqlalchemy.orm import Session

from app.models import AttendancePolicy
from app.services.attendance_rules import PolicyData


def get_policy(db: Session) -> AttendancePolicy:
    policy = db.get(AttendancePolicy, 1)
    if policy is None:
        policy = AttendancePolicy(id=1)
        db.add(policy)
        db.flush()
    return policy


def policy_data(policy: AttendancePolicy) -> PolicyData:
    return PolicyData(min_present_minutes=policy.min_present_minutes, late_grace_minutes=policy.late_grace_minutes)
