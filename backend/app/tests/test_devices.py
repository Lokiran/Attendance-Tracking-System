"""Office sites and the device register: the shared Wi-Fi password means people are matched through devices."""

import io

import pytest
from sqlalchemy import func, select

from app.models import AuditLog, Device
from app.services.devices import is_random_mac, normalize_mac, normalize_serial
from app.tests.conftest import login

DEV = "/api/admin/devices"
XLSX = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"


# ------------------------------------------------------------------ identifiers

@pytest.mark.parametrize("raw, mac", [
    ("AA-BB-CC-DD-EE-01", "aa:bb:cc:dd:ee:01"), ("aabb.ccdd.ee01", "aa:bb:cc:dd:ee:01"),
    ("AABBCCDDEE01", "aa:bb:cc:dd:ee:01"), (" aa:bb:cc:dd:ee:01 ", "aa:bb:cc:dd:ee:01"), ("", None), (None, None)])
def test_mac_addresses_are_normalised(raw, mac):
    assert normalize_mac(raw) == mac


@pytest.mark.parametrize("raw", ["AA:BB:CC", "zz:bb:cc:dd:ee:ff", "00:00:00:00:00:00", "FF-FF-FF-FF-FF-FF"])
def test_bad_mac_addresses_are_refused(raw):
    with pytest.raises(ValueError):
        normalize_mac(raw)


def test_random_addresses_and_junk_serials():
    assert is_random_mac("da:a1:19:00:00:01") and is_random_mac("02:00:00:00:00:01")
    assert not is_random_mac("00:1b:63:00:00:01") and not is_random_mac(None)
    assert normalize_serial("  pf3a  9b2c ") == "PF3A 9B2C"
    assert normalize_serial("To be filled by O.E.M.") is None and normalize_serial("N/A") is None


# ------------------------------------------------------------------ offices

def test_offices_are_admin_only_unique_and_audited(client, org, db):
    login(client, org.rahul)
    assert client.get("/api/admin/sites").status_code == 403
    h = login(client, org.admin)
    r = client.post("/api/admin/sites", headers=h, json={"name": "Head Office", "code": "ho-1", "network_name": "Corp-HO"})
    assert r.status_code == 201, r.text
    site = r.json()
    assert site["code"] == "HO-1" and site["is_active"]
    assert client.post("/api/admin/sites", headers=h, json={"name": "head office", "code": "X"}).status_code == 409
    assert client.post("/api/admin/sites", headers=h, json={"name": "Branch", "code": "HO-1"}).status_code == 409
    r = client.patch(f"/api/admin/sites/{site['id']}", headers=h, json={"network_name": "Corp-HO-5G", "is_active": False})
    assert r.json()["network_name"] == "Corp-HO-5G" and not r.json()["is_active"]
    assert [s["name"] for s in client.get("/api/admin/sites").json()] == ["Head Office"]
    actions = set(db.scalars(select(AuditLog.action).where(AuditLog.entity_type == "office_site")))
    assert actions == {"OFFICE_SITE_CREATED", "OFFICE_SITE_UPDATED"}


# ------------------------------------------------------------------ devices

def test_devices_are_created_checked_and_listed(client, org, db):
    h = login(client, org.admin)
    r = client.post(DEV, headers=h, json={"serial_number": "pf3a9b2c", "user_id": org.rahul.id, "model": "ThinkPad"})
    assert r.status_code == 201, r.text
    laptop = r.json()
    assert laptop["serial_number"] == "PF3A9B2C" and laptop["mac_address"] is None
    assert laptop["user"]["full_name"] == "Rahul Reddy" and laptop["source"] == "MANUAL"
    # Duplicates, bad addresses and a device with nothing to identify it are refused.
    assert client.post(DEV, headers=h, json={"serial_number": "PF3A9B2C"}).status_code == 409
    assert client.post(DEV, headers=h, json={"mac_address": "nope"}).status_code == 422
    assert client.post(DEV, headers=h, json={"model": "No identifier"}).status_code == 422
    assert client.post(DEV, headers=h, json={"serial_number": "X1", "user_id": 99999}).status_code == 422
    # The MAC address arrives later; a private (random) address is flagged.
    r = client.patch(f"{DEV}/{laptop['id']}", headers=h, json={"mac_address": "DA-A1-19-00-00-01"})
    assert r.json()["mac_address"] == "da:a1:19:00:00:01" and r.json()["random_mac"]
    client.post(DEV, headers=h, json={"serial_number": "SN-2", "mac_address": "00:1b:63:00:00:02"})
    assert client.post(DEV, headers=h, json={"serial_number": "SN-3", "mac_address": "00-1B-63-00-00-02"}).status_code == 409
    # Search by serial, owner or MAC (with or without separators); filters.
    assert [d["serial_number"] for d in client.get(DEV, params={"search": "rahul"}).json()["items"]] == ["PF3A9B2C"]
    assert [d["serial_number"] for d in client.get(DEV, params={"search": "001b63"}).json()["items"]] == ["SN-2"]
    assert client.get(DEV, params={"assigned": False}).json()["total"] == 1
    # Retiring hides a device from the default list.
    client.patch(f"{DEV}/{laptop['id']}", headers=h, json={"is_active": False})
    assert client.get(DEV).json()["total"] == 1 and client.get(DEV, params={"active": False}).json()["total"] == 1
    assert db.scalar(select(func.count()).select_from(AuditLog).where(AuditLog.entity_type == "device")) == 4
    login(client, org.priya)
    assert client.get(DEV).status_code == 403


def asset_list() -> bytes:
    """What the company has today: serial numbers and who has each laptop (no MAC addresses)."""
    from openpyxl import Workbook
    wb = Workbook()
    ws = wb.active
    ws.title = "Assets"
    ws.append(["IT asset register"])                           # a title above the header
    ws.append([])
    ws.append(["Asset Tag", "Serial No.", "Emp. ID", "Employee Name", "Model"])
    ws.append(["A-001", "pf3a9b2c", None, "Rahul Reddy", "ThinkPad E14"])       # matched by name
    ws.append(["A-002", "PF3A9B2D", None, "Sneha Rao", "ThinkPad E14"])
    ws.append(["A-003", "To be filled by O.E.M.", None, "Arjun Kumar", None])  # no usable serial
    ws.append(["A-004", "PF3A9B2E", "NOPE-9", None, "ThinkPad E14"])            # unknown employee: kept unassigned
    ws.append(["A-005", "pf3a9b2d", None, "Priya Sharma", None])               # serial repeated
    buf = io.BytesIO()
    wb.save(buf)
    return buf.getvalue()


def test_asset_list_then_intune_export_fill_the_register(client, org, db):
    h = login(client, org.admin)
    files = {"file": ("assets.xlsx", asset_list(), XLSX)}
    check = client.post(f"{DEV}/import", headers=h, files=files)
    assert check.status_code == 200, check.text
    s = check.json()
    assert not s["applied"] and s["sheet"] == "Assets"
    assert s["columns"] == {"asset_tag": "Asset Tag", "serial": "Serial No.", "employee": "Emp. ID",
                            "name": "Employee Name", "model": "Model"}
    assert (s["rows"], s["created"], s["errors"]) == (5, 3, 2)
    assert s["unknown_owners"] == [{"written": "NOPE-9", "rows": 1}]
    problems = {i["row"]: i["problems"] for i in s["items"]}
    assert problems[6] == ["no serial number or MAC address"] and "repeated (first on row 5)" in problems[8][0]
    assert db.scalar(select(func.count(Device.id))) == 0  # a check writes nothing

    done = client.post(f"{DEV}/import", headers=h, params={"apply": True}, files=files).json()
    assert done["applied"] and done["created"] == 3
    owners = {d.serial_number: d.user_id for d in db.scalars(select(Device))}
    assert owners == {"PF3A9B2C": org.rahul.id, "PF3A9B2D": org.sneha.id, "PF3A9B2E": None}

    # Later: the Intune export adds the Wi-Fi MAC addresses to the same laptops, by serial number.
    intune = ("Device name,Serial number,Wi-Fi MAC,Primary user UPN,Model\r\n"
              f"LT-RAHUL,PF3A9B2C,00:1B:63:AA:00:01,{org.rahul.email},ThinkPad E14\r\n"
              f"LT-SNEHA,PF3A9B2D,DAA119AA0002,{org.sneha.email},\r\n"           # private address: flagged
              f"LT-ARJUN,NEW-SN-1,00-1B-63-AA-00-01,{org.arjun.email},\r\n"     # same MAC as Rahul's laptop
              f"LT-NEW,NEW-SN-2,,{org.arjun.email.upper()},Latitude 5440\r\n").encode()
    s = client.post(f"{DEV}/import", headers=h, params={"apply": True},
                    files={"file": ("intune.csv", intune, "text/csv")}).json()
    assert (s["updated"], s["created"], s["errors"], s["random_mac"]) == (2, 1, 1, 1)
    items = {i["row"]: i for i in s["items"]}
    assert items[2]["changes"] == ["hostname", "mac_address"] and items[2]["owner"] == "Rahul Reddy"
    assert "private (random)" in items[3]["problems"][0]
    assert items[4]["problems"] == ["MAC address repeated (first on row 2)"]
    db.expire_all()
    rahul_laptop = db.scalar(select(Device).where(Device.serial_number == "PF3A9B2C"))
    assert rahul_laptop.mac_address == "00:1b:63:aa:00:01" and rahul_laptop.model == "ThinkPad E14"
    assert db.scalar(select(Device).where(Device.serial_number == "NEW-SN-2")).user_id == org.arjun.id

    # A MAC address already in the register cannot be given to another laptop by a later file.
    clash = ("Serial number,Wi-Fi MAC,Primary user UPN\r\n"
             f"OTHER-SN,00:1B:63:AA:00:01,{org.arjun.email}\r\n").encode()
    s = client.post(f"{DEV}/import", headers=h, files={"file": ("clash.csv", clash, "text/csv")}).json()
    assert s["errors"] == 1 and "already belongs to serial PF3A9B2C" in s["items"][0]["problems"][0]

    summary = client.get(f"{DEV}/summary").json()
    assert summary["devices"] == 4 and summary["with_mac"] == 2 and summary["unassigned"] == 1
    assert summary["random_mac"] == 1 and summary["people_with_device"] == 2
    assert db.scalar(select(func.count()).select_from(AuditLog).where(AuditLog.action == "DEVICES_IMPORTED")) == 2


def test_import_refuses_files_without_the_columns_it_needs(client, org):
    h = login(client, org.admin)
    r = client.post(f"{DEV}/import", headers=h, files={"file": ("x.csv", b"Name,Department\r\nRahul,AI\r\n", "text/csv")})
    assert r.status_code == 422 and "serial number or MAC address column" in r.json()["error"]["message"]
    r = client.post(f"{DEV}/import", headers=h, files={"file": ("x.pdf", b"%PDF-1.4", "application/pdf")})
    assert r.status_code == 422
    login(client, org.rahul)
    assert client.post(f"{DEV}/import", files={"file": ("x.csv", b"a", "text/csv")}).status_code == 403
