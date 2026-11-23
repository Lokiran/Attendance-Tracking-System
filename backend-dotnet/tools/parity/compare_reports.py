"""Compare the Python and .NET exports (docs/dotnet-migration-plan.md §7.13, §11): CSV reports byte for byte, xlsx files
cell by cell (sheet names and values), the employee PDF by its extracted words and page count, and the status and
download headers of every response.

Both backends must use the same database copy and SECRET_KEY, with DEV_LOGIN_ENABLED=true (like compare.py; exports
only add audit rows).

    python backend-dotnet/tools/parity/compare_reports.py --python http://127.0.0.1:8010 --dotnet http://127.0.0.1:8011 \
        --admin admin@nexa.example
"""
import argparse
import io
import re
import sys
from collections import Counter
from datetime import date, datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from compare import call, login  # noqa: E402

HEADERS = ("Content-Type", "Content-Disposition", "Cache-Control")
VOLATILE = re.compile(r"^\d{1,2}:\d{2}$")  # the "Generated" time can differ by a minute between the two calls
GENERATED = re.compile(r"^(\d{2} \w{3} \d{4}), \d{2}:\d{2}$")
# The status bullet is ZapfDingbats code 0x6C (a filled circle) in both files; pdfminer extracts it as "n" from reportlab's
# font dictionary (no /Encoding) and as "l" from PdfPig's (/Encoding /FontSpecific). Same glyph on the page.
BULLETS = {"l", "n"}


def xlsx_cells(content: bytes) -> dict:
    from openpyxl import load_workbook
    wb = load_workbook(io.BytesIO(content), data_only=True)
    out = {}
    for ws in wb.worksheets:
        rows = []
        for row in ws.iter_rows(values_only=True):
            cells = []
            for v in row:
                if v == "":
                    v = None
                if isinstance(v, str) and GENERATED.match(v):
                    v = GENERATED.sub(r"\1", v)
                if isinstance(v, datetime) and v.hour == v.minute == v.second == 0:
                    v = v.date()
                if isinstance(v, float) and v.is_integer():
                    v = int(v)
                cells.append(v)
            while cells and cells[-1] is None:
                cells.pop()
            rows.append(cells)
        while rows and not rows[-1]:
            rows.pop()
        out[ws.title] = rows
    return out


def pdf_words(content: bytes) -> tuple[int, Counter]:
    from pdfminer.high_level import extract_pages
    from pdfminer.layout import LTTextContainer
    pages = list(extract_pages(io.BytesIO(content)))
    words: Counter = Counter()
    for page in pages:
        for element in page:
            if isinstance(element, LTTextContainer):
                for w in element.get_text().split():
                    if not VOLATILE.match(w) and w not in BULLETS:
                        words[w] += 1
    return len(pages), words


def compare_body(path: str, b1, b2) -> list[tuple]:
    if not isinstance(b1, bytes) or not isinstance(b2, bytes):
        from compare import diff
        return diff(b1, b2) if isinstance(b1, (dict, list)) and isinstance(b2, (dict, list)) else ([] if b1 == b2 else [("$body", b1, b2)])
    if ".csv" in path:
        if b1 == b2:
            return []
        l1, l2 = b1.decode("utf-8-sig").split("\r\n"), b2.decode("utf-8-sig").split("\r\n")
        for i, (x, y) in enumerate(zip(l1, l2)):
            if x != y:
                return [(f"line {i + 1}", x, y)]
        return [("$lines", len(l1), len(l2))]
    if ".xlsx" in path:
        c1, c2 = xlsx_cells(b1), xlsx_cells(b2)
        if list(c1) != list(c2):
            return [("$sheets", list(c1), list(c2))]
        for sheet in c1:
            for i, (x, y) in enumerate(zip(c1[sheet], c2[sheet])):
                if x != y:
                    return [(f"{sheet}!row {i + 1}", x, y)]
            if len(c1[sheet]) != len(c2[sheet]):
                return [(f"{sheet}!rows", len(c1[sheet]), len(c2[sheet]))]
        return []
    if ".pdf" in path:
        (n1, w1), (n2, w2) = pdf_words(b1), pdf_words(b2)
        problems = [] if n1 == n2 else [("$pages", n1, n2)]
        missing, extra = w1 - w2, w2 - w1
        if missing or extra:
            problems.append(("$words", dict(list(missing.items())[:12]), dict(list(extra.items())[:12])))
        return problems
    return [] if b1 == b2 else [("$body", b1[:60], b2[:60])]


def requests_for(role: str, ids: list[int], today: date) -> list[str]:
    ranges = [("2026-06-01", "2026-10-31"), ("2026-10-01", "2026-10-31"), ("2026-09-15", today.isoformat()), (today.isoformat(), today.isoformat())]
    out = []
    for eid in ids:
        for a, b in ranges:
            for ext in ("csv", "xlsx", "pdf"):
                out.append(f"/api/reports/employee-attendance.{ext}?employee_id={eid}&start={a}&end={b}")
    out += ["/api/reports/employee-attendance.csv", "/api/reports/employee-attendance.xlsx?employee_id=x&start=2026-13-01",
            "/api/reports/employee-attendance.pdf?employee_id=999999", f"/api/reports/employee-attendance.csv?employee_id={ids[0]}&start=2026-10-05&end=2026-10-01"]
    if role in ("MANAGER", "ADMIN"):
        for a, b in ranges:
            out += [f"/api/reports/team-attendance.csv?start={a}&end={b}", f"/api/reports/exceptions.csv?start={a}&end={b}"]
        out += [f"/api/reports/monthly-attendance.csv?year=2026&month={m}" for m in (6, 7, 8, 9, 10, 11)]
        out += ["/api/reports/monthly-attendance.csv", "/api/reports/monthly-attendance.csv?year=1999&month=13",
                "/api/reports/team-attendance.csv?manager_id=999999", "/api/reports/exceptions.csv?department_id=1&start=2026-10-01&end=2026-10-31"]
    else:
        out += ["/api/reports/team-attendance.csv", "/api/reports/monthly-attendance.csv?year=2026&month=10", "/api/reports/exceptions.csv"]
    if role == "ADMIN":
        for a, b in ranges:
            out.append(f"/api/reports/department-attendance.csv?start={a}&end={b}")
        out += [f"/api/org/wfo/export.xlsx?month=2026-{m:02d}" for m in (6, 7, 8, 9, 10, 11, 12)]
        out += ["/api/org/wfo/export.xlsx", "/api/org/wfo/export.xlsx?month=2026-13", "/api/org/wfo/export.xlsx?month=26-1",
                "/api/org/wfo/export.xlsx?month=2026-10&department_id=1", "/api/org/wfo/export.xlsx?month=2026-10&manager_id=999999"]
    else:
        out += ["/api/reports/department-attendance.csv", "/api/org/wfo/export.xlsx"]
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--python", required=True)
    ap.add_argument("--dotnet", required=True)
    ap.add_argument("--admin", required=True)
    args = ap.parse_args()
    admin_cookie, admin_me = login(args.python, args.admin)
    today = date.fromisoformat(call(args.python, "/api/attendance/current", admin_cookie)[1]["today"])
    members = call(args.python, "/api/team/members?include_inactive=true", admin_cookie)[1]
    managers = [m for m in members if m["role"] == "MANAGER" and m["is_active"]]
    employees = [m for m in members if m["role"] == "EMPLOYEE" and m["is_active"] and m["manager_id"]]
    personas = [("ADMIN", admin_cookie, admin_me)]
    for role, people in (("MANAGER", managers), ("EMPLOYEE", employees)):
        if people:
            cookie, me = login(args.python, people[0]["employee_code"])
            personas.append((role, cookie, me))
    same = different = 0
    for role, cookie, me in personas:
        my_id = me["user"]["id"]
        if role == "EMPLOYEE":
            ids = [my_id, next(m["id"] for m in members if m["id"] != my_id)]
        else:
            ids = [my_id, *[m["id"] for m in call(args.python, "/api/team/members", cookie)[1]][:2]]
        for path in requests_for(role, ids, today):
            s1, b1, h1 = call(args.python, path, cookie)
            s2, b2, h2 = call(args.dotnet, path, cookie)
            problems = [("$status", s1, s2)] if s1 != s2 else []
            if s1 == s2 == 200:
                problems += [(f"header {h}", h1.get(h), h2.get(h)) for h in HEADERS if h1.get(h) != h2.get(h)]
            problems += compare_body(path, b1, b2)
            if problems:
                different += 1
                print(f"[{role}] {path}")
                for p, x, y in problems[:5]:
                    print(f"    {p}: python={str(x)[:150]!r} dotnet={str(y)[:150]!r}")
            else:
                same += 1
    print(f"\n{same} identical, {different} different ({len(personas)} personas, today {today})")
    return 1 if different else 0


if __name__ == "__main__":
    sys.exit(main())
