import pytest
from sqlalchemy import select

from app.core.context import RequestContext
from app.models import AuditLog, User
from app.services.identities import apply_plan, normalize_graph_user_id, normalize_wifi_identity, plan_from_csv

GID1 = "11111111-2222-3333-4444-555555555555"
GID2 = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
CTX = RequestContext(request_id="test")


@pytest.mark.parametrize("raw, expected", [
    ("AA-BB-CC-DD-EE-FF", "aa:bb:cc:dd:ee:ff"),
    ("aabb.ccdd.eeff", "aa:bb:cc:dd:ee:ff"),
    ("AABBCCDDEEFF", "aa:bb:cc:dd:ee:ff"),
    ("NEXA\\Rahul.Reddy", "nexa\\rahul.reddy"),
    ("  rahul.reddy@nexa.example ", "rahul.reddy@nexa.example"),
    ("", None),
])
def test_normalize_wifi_identity(raw, expected):
    assert normalize_wifi_identity(raw) == expected


def test_normalize_graph_user_id():
    assert normalize_graph_user_id(GID1.upper()) == GID1
    assert normalize_graph_user_id("") is None
    with pytest.raises(ValueError, match="GUID"):
        normalize_graph_user_id("not-a-guid")


def test_dry_run_then_apply_with_audit(db, org):
    csv_text = (f"employee_code,graph_user_id,wifi_identity\n"
                f"{org.rahul.employee_code},{GID1},AA-BB-CC-DD-EE-FF\n"
                f"{org.sneha.employee_code},{GID2},\n")
    plan = plan_from_csv(db, csv_text)
    assert plan.errors == [] and len(plan.changes) == 2
    db.expire_all()
    assert db.get(User, org.rahul.id).graph_user_id is None  # planning writes nothing
    assert apply_plan(db, plan, None, CTX) == 2
    db.expire_all()
    rahul = db.get(User, org.rahul.id)
    assert rahul.graph_user_id == GID1 and rahul.wifi_identity == "aa:bb:cc:dd:ee:ff"
    assert db.get(User, org.sneha.id).wifi_identity is None
    logs = list(db.scalars(select(AuditLog).where(AuditLog.action == "USER_IDENTITY_LINKED")))
    assert len(logs) == 2
    assert plan_from_csv(db, csv_text).unchanged == 2  # idempotent


def test_lookup_by_email_and_errors_block_everything(db, org):
    csv_text = (f"email,graph_user_id\n"
                f"{org.rahul.email},{GID1}\n"
                f"ghost@test.example,{GID2}\n"
                f"{org.sneha.email},bad-guid\n"
                f"{org.arjun.email},{GID1}\n")
    plan = plan_from_csv(db, csv_text)
    assert any("no user" in e for e in plan.errors)
    assert any("GUID" in e for e in plan.errors)
    assert any("also used on line 2" in e for e in plan.errors)
    with pytest.raises(ValueError):
        apply_plan(db, plan, None, CTX)


def test_value_already_owned_by_someone_else(db, org):
    org.sneha.graph_user_id = GID1
    db.merge(org.sneha)
    db.commit()
    plan = plan_from_csv(db, f"employee_code,graph_user_id\n{org.rahul.employee_code},{GID1}\n")
    assert any("already belongs to" in e for e in plan.errors)
    # ...but moving it is fine when the same file reassigns the current owner
    plan = plan_from_csv(db, f"employee_code,graph_user_id\n{org.rahul.employee_code},{GID1}\n"
                             f"{org.sneha.employee_code},{GID2}\n")
    assert plan.errors == []
    apply_plan(db, plan, None, CTX)
    db.expire_all()
    assert db.get(User, org.rahul.id).graph_user_id == GID1 and db.get(User, org.sneha.id).graph_user_id == GID2


def test_missing_columns():
    from unittest.mock import MagicMock
    assert plan_from_csv(MagicMock(), "name,foo\nx,y\n").errors
