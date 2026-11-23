"""Generate timesheet-reader parity fixtures: synthetic CSV, Excel (.xlsx) and PDF files read by the Python backend's own
``read_workbook``, plus the cell-value parsers (parse_date, date_candidates, DateReader, parse_hours, parse_time) on
many inputs. The .NET tests read the same files and must produce identical cells. Synthetic data only; the seed is
fixed so the file is reproducible.

    backend\\.venv\\Scripts\\python.exe backend-dotnet\\tools\\make_upload_fixtures.py

Local mode (real files, never committed): record what Python reads from files on this machine, for the opt-in test
``UploadParityTests.Local_files_read_like_python`` (environment variable ATS_UPLOAD_PARITY_FILE):

    backend\\.venv\\Scripts\\python.exe backend-dotnet\\tools\\make_upload_fixtures.py --local OUT.json FILE [FILE ...]
"""
import base64
import csv
import gzip
import io
import json
import random
import sys
from datetime import date, datetime, time, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "backend"))
from app.core.errors import ValidationFailed  # noqa: E402
from app.services.timesheet_files import read_workbook  # noqa: E402
from app.services.wfo_upload import DateReader, date_candidates, parse_date, parse_hours, parse_time  # noqa: E402

OUT = ROOT / "backend-dotnet" / "tests" / "ATS.Tests" / "Fixtures" / "uploads.json.gz"
rnd = random.Random(20261123)
FIRST = ["Asha", "Ravi", "Meera", "Dev", "Nisha", "Arun", "Lata", "Sita", "Omar", "Lena", "Tara", "Ivan"]
LAST = ["Iyer", "Shah", "Menon", "Gupta", "Nair", "Das", "Bose", "Khan", "Pillai", "Quill"]


def canon(v):
    """A cell as typed text, the same on both sides (ints are floats in .NET)."""
    if v is None:
        return None
    if isinstance(v, bool):
        return f"b:{v}"
    if isinstance(v, (int, float)):
        return f"f:{float(v)!r}"
    if isinstance(v, datetime):
        return f"dt:{v.isoformat()}"
    if isinstance(v, time):
        return f"t:{v.isoformat()}"
    if isinstance(v, timedelta):
        return f"td:{v.total_seconds()!r}"
    if isinstance(v, str):
        return f"s:{v}"
    return f"o:{v}"


def read_case(name: str, content: bytes, sheet: str | None = None, embed: bool = True) -> dict:
    case = {"name": name, "sheet": sheet}
    if embed:
        case["content"] = base64.b64encode(content).decode()
    try:
        t = read_workbook(name, content, sheet)
        case["result"] = {"rows": [[canon(c) for c in r] for r in t.rows], "sheets": t.sheets, "sheet": t.sheet,
                          "file_type": t.file_type, "hours_from": t.hours_from}
    except ValidationFailed as e:
        case["error"] = str(e.message)
    return case


def person() -> str:
    return f"{rnd.choice(FIRST)} {rnd.choice(LAST)}"


def code() -> str:
    return rnd.choice([f"E{rnd.randint(1, 999):03d}", f"10{rnd.randint(0, 999):03d}", f"Intern - 1{rnd.randint(0, 9999):04d}",
                       f"T-{rnd.randint(1, 99)}", f"10{rnd.randint(0, 999):03d}-Ext"])


def date_text(d: date) -> str:
    return rnd.choice([d.strftime("%d/%m/%Y"), d.strftime("%d-%m-%Y"), d.isoformat(), f"{d.month}/{d.day}/{d.year}",
                       d.strftime("%d-%b-%Y"), d.strftime("%d %B %Y"), f"{d.day}/{d.month}/{d.year}"])


# ------------------------------------------------------------------ CSV

def csv_cases() -> list[dict]:
    cases = [
        read_case("t.tsv", b"Employee\tDate\nE1\t01/10/2026\n"),
        read_case("t.txt", b"Employee|Date\nE1|01/10/2026\n"),
        read_case("a.csv", "Employee;Date\nEMP001;2026-10-05\n".encode()),
        read_case("bom.csv", "﻿Employee,Date\r\nE1,2026-10-05\r\n".encode()),
        read_case("cp.csv", "Employee,Name,Date\nE1,Jos\xe9 \x81 \x80,2026-10-05\n".encode("latin-1")),
        read_case("q.csv", b'Employee,Name,Date\n"E1","Quill, Asha","05/10/2026"\nE2,"say ""hi""",06/10/2026\n'),
        read_case("ml.csv", b'Employee,Note,Date\nE1,"two\nlines",05/10/2026\n\nE2,x,06/10/2026'),
        read_case("tie.csv", b"a,b;c\n"),
        read_case("open.csv", b'Employee,Date\nE1,"05/10/2026'),
        read_case("odd.csv", b'Employee,Date\nE1,"05"x/10/2026\n,,\n,\n'),
        read_case("empty.csv", b""),
        read_case("a.docx", b"x"),
        read_case("a.ods", b"x"),
    ]
    for i in range(40):
        delim = rnd.choice([",", ";", "\t", "|"])
        rows = [["Employee", "Name", "Date", rnd.choice(["Mode", "Work Mode", "Location"]), "Hours"]]
        for _ in range(rnd.randint(0, 30)):
            d = date(2026, rnd.randint(6, 11), rnd.randint(1, 28))
            rows.append([code(), person(), date_text(d), rnd.choice(["WFO", "WFH", "Leave", "office", ""]),
                         rnd.choice(["8", "7.5", "7:30", "", "08:37:00"])])
            if rnd.random() < 0.1:
                rows.append([])
        buf = io.StringIO()
        csv.writer(buf, delimiter=delim, lineterminator=rnd.choice(["\n", "\r\n"]),
                   quoting=rnd.choice([csv.QUOTE_MINIMAL, csv.QUOTE_ALL])).writerows(rows)
        text = buf.getvalue()
        data = text.encode("utf-8-sig" if rnd.random() < 0.3 else "utf-8")
        cases.append(read_case(f"r{i}.{rnd.choice(['csv', 'tsv', 'txt'])}", data))
    return cases


# ------------------------------------------------------------------ Excel

FORMATS = ["General", "dd/mm/yyyy", "d-mmm-yy", "h:mm", "h:mm AM/PM", "[h]:mm:ss", "[mm]:ss", "0.00", "@",
           "yyyy-mm-dd hh:mm", '"Day "0', "[$-409]h:mm AM/PM", "[Red]0.0", "mm:ss", "#,##0"]


def xlsx_bytes(wb) -> bytes:
    buf = io.BytesIO()
    wb.save(buf)
    return buf.getvalue()


def xlsx_cases() -> list[dict]:
    from openpyxl import Workbook
    cases = []
    # Every value type and number format.
    wb = Workbook()
    ws = wb.active
    ws.title = "Types"
    ws.append(["Employee", "Date", "Value", "Format"])
    values = [datetime(2026, 10, 5), datetime(2026, 1, 10, 9, 30), time(9, 30), time(18, 15, 30), timedelta(hours=8, minutes=30),
              7.5, 8, 0, True, False, "text", "  spaced  ", None, 45000.5, 0.999999, 1.5, 0.3958333333, 61, 59.75, 2.0000001]
    for i, v in enumerate(values):
        for fmt in FORMATS:
            ws.append([f"E{i:03d}", datetime(2026, 10, 1 + i % 28), v, fmt])
            cell = ws.cell(row=ws.max_row, column=3)
            if not isinstance(v, (str, bool)) and v is not None:
                cell.number_format = fmt
    cases.append(read_case("types.xlsx", xlsx_bytes(wb)))

    # Month sheet chosen over a raw extract; times copied from the raw sheet by ID or by name.
    for i in range(12):
        wb = Workbook()
        raw = wb.active
        raw.title = rnd.choice(["Raw Data", "TS Raw Data", "Extract"])
        raw.append(["Code", "Employee", "Date", "Time In", "Time Out", "Total Hrs"])
        people = [(code(), person()) for _ in range(rnd.randint(1, 6))]
        month = wb.create_sheet(rnd.choice(["October 2026", "Sept 2026", "2026-10", "Summary"]))
        if rnd.random() < 0.3:
            month.append(["Attendance report"])
            month.append([])
        month.append(rnd.choice([["Employee ID", "Employee", "Date", None, "Row Labels", "Count of Date"],
                                 ["Emp. Code", "Emp. Name", "Date"], ["Code", "Name", "Date", "Mode"]]))
        for c, n in people:
            for _ in range(rnd.randint(1, 8)):
                d = datetime(2026, 10, rnd.randint(1, 31))
                listed = rnd.choice([d, d.strftime("%d/%m/%Y"), datetime(2026, d.day if d.day <= 12 else 10, 10 if d.day <= 12 else d.day)])
                month.append([c if rnd.random() < 0.8 else c + "-X", n, listed, rnd.choice([None, "WFO"]), "Label", 1])
                if rnd.random() < 0.6:
                    raw.append([c if rnd.random() < 0.7 else c.lower() + " ", n, d, rnd.choice([time(9, 30), "09:45", None]),
                                rnd.choice([time(18, 0), "17:30"]), rnd.choice([time(8, 30), "8.5", 7.25, timedelta(hours=8)])])
        if rnd.random() < 0.4:
            month.cell(row=month.max_row + 3, column=5, value="Grand Total")
        content = xlsx_bytes(wb)
        cases.append(read_case(f"month{i}.xlsx", content))
        cases.append(read_case(f"month{i}.xlsx", content, sheet=raw.title))
    cases.append(read_case("m.xlsx", content, sheet="Nope"))
    cases.append(read_case("bad.xlsx", b"PK\x03\x04 not really a zip"))
    cases.append(read_case("bad.xls", b"not an excel file"))
    # No usable sheet at all.
    wb = Workbook()
    wb.active.append(["Name", "Day"])
    wb.active.append(["A", "2026-10-05"])
    cases.append(read_case("none.xlsx", xlsx_bytes(wb)))
    return cases


# ------------------------------------------------------------------ PDF

def pdf_bytes(pages: list[list[tuple]], font: str = "Helvetica") -> bytes:
    """A minimal PDF: each page is a list of (x, y, text[, size]) drawn in a standard font (landscape A4-ish)."""
    objects: list[bytes] = []

    def add(body: bytes) -> int:
        objects.append(body)
        return len(objects)

    font_id = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /%s >>" % font.encode())
    pages_id = len(objects) + 2 * len(pages) + 1
    kids = []
    for cells in pages:
        parts = []
        for cell in cells:
            x, y, t = cell[:3]
            size = cell[3] if len(cell) > 3 else 10
            esc = t.replace("\\", "\\\\").replace("(", r"\(").replace(")", r"\)").encode("latin-1")
            parts.append(b"BT /F1 %d Tf %.2f %.2f Td (%s) Tj ET\n" % (size, x, y, esc))
        stream = b"".join(parts)
        content = add(b"<< /Length %d >>\nstream\n%s\nendstream" % (len(stream), stream))
        kids.append(add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 792 612] /Contents %d 0 R "
                        b"/Resources << /Font << /F1 %d 0 R >> >> >>" % (pages_id, content, font_id)))
    assert add(b"<< /Type /Pages /Kids [%s] /Count %d >>" % (b" ".join(b"%d 0 R" % k for k in kids), len(kids))) == pages_id
    catalog = add(b"<< /Type /Catalog /Pages %d 0 R >>" % pages_id)
    out = bytearray(b"%PDF-1.4\n")
    offsets = []
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n%s\nendobj\n" % (i, body)
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objects) + 1)
    out += b"".join(b"%010d 00000 n \n" % o for o in offsets)
    out += b"trailer\n<< /Size %d /Root %d 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, catalog, xref)
    return bytes(out)


HEADER_SETS = [
    ["Employee ID", "Employee", "Date", "Row Labels", "Count of Date"],
    ["Emp. ID", "Employee Name", "Date", "Time In", "Time Out", "Total Hrs"],
    ["Code", "Employee", "Date", "Time In", "Time Out", "Total Hrs"],
    ["Sr No.", "Code", "Employee", "Date", "Time In", "Time Out", "Total Hrs"],
    ["Code", "Name", "Date", "Mode", "Hours"],
]


def pdf_table_case(i: int) -> dict:
    size = rnd.choice([8, 9, 10, 11])
    font = rnd.choice(["Helvetica", "Helvetica", "Times-Roman", "Courier"])
    headers = rnd.choice(HEADER_SETS)
    widths = [rnd.randint(55, 140) for _ in headers]
    xs = [40.0]
    for w in widths[:-1]:
        xs.append(xs[-1] + w)
    glue_header = rnd.random() < 0.4
    rows = []
    for _ in range(rnd.randint(1, 40)):
        d = date(2026, 10, rnd.randint(1, 31))
        values = {"Employee ID": code(), "Emp. ID": code(), "Code": code(), "Employee": person(), "Employee Name": person(),
                  "Name": person(), "Date": date_text(d), "Row Labels": f"Label {rnd.randint(1, 99)}", "Count of Date": "1",
                  "Time In": rnd.choice(["09:40", "10:05", "", "9:5"]), "Time Out": rnd.choice(["17:39", "18:00", ""]),
                  "Total Hrs": rnd.choice(["7:59", "05:04:00", "8.5", ""]), "Sr No.": str(rnd.randint(1, 300)),
                  "Mode": rnd.choice(["WFO", "WFH", "Leave", ""]), "Hours": rnd.choice(["8", "7.5", ""])}
        rows.append([values[h] for h in headers])
    per_page = rnd.randint(3, 25)
    pages = []
    for start in range(0, len(rows), per_page):
        y = 560.0
        cells = []
        if rnd.random() < 0.3:
            cells.append((40, y + 25, "Attendance report October 2026", size + 2))
        line = []
        j = 0
        while j < len(headers):
            h = headers[j]
            if glue_header and h == "Sr No." and j + 1 < len(headers):
                line.append((xs[j], y, h + headers[j + 1], size))
                j += 2
                continue
            if glue_header and h == "Time In" and j + 2 < len(headers):
                line.append((xs[j], y, "Time InTime OutTotal Hrs", size))
                j += 3
                continue
            line.append((xs[j], y, h, size))
            j += 1
        cells += line
        for r in rows[start:start + per_page]:
            y -= rnd.choice([12, 14, 15, 18])
            k = 0
            while k < len(r):
                text = r[k]
                if not text:
                    k += 1
                    continue
                x = xs[k] + (rnd.choice([0, 0, 15]) if rnd.random() < 0.8 else widths[k] - len(text) * size * 0.5)
                if k + 1 < len(r) and r[k + 1] and rnd.random() < 0.08:
                    cells.append((x, y, text + r[k + 1], size))  # glued to the next cell
                    k += 2
                    continue
                cells.append((x, y, text, size))
                k += 1
        if rnd.random() < 0.3:
            cells.append((600, 30, f"Page {len(pages) + 1}", size))
        pages.append(cells)
    return read_case(f"pdf{i}.pdf", pdf_bytes(pages, font))


def pdf_cases() -> list[dict]:
    y = 540.0
    fixed = [
        [(57, y, "Emp. ID"), (181, y, "Employee Name"), (357, y, "Date"),
         (72, y - 15, "E005"), (171, y - 15, "Asha Quill"), (343, y - 15, "2026-10-01"),
         (72, y - 30, "E006"), (120, y - 30, "A Very Long Name That Fills The Whole Cell2026-10-02"),
         (72, y - 45, "E007"), (120, y - 45, "Another Long Name That Fills The Cell 215/10/2026")],
        [(53, y, "Code"), (110, y, "Employee"), (227, y, "Date"), (280, y, "Time InTime OutTotal Hrs"),
         (81, y - 15, "10001Asha Quill"), (230, y - 15, "10/1/2026"), (287, y - 15, "09:40"), (329, y - 15, "17:39"),
         (375, y - 15, "7:59"), (60, y - 30, "10002-ExtRavi Menon"), (230, y - 30, "10/2/2026"), (287, y - 30, "10:05"),
         (329, y - 30, "12:15"), (375, y - 30, "2:10"), (53, y - 45, "Guest 01"), (110, y - 45, "Guest 01"),
         (230, y - 45, "10/2/2026")],
        [(53, y, "Sr No.Code"), (150, y, "Employee"), (315, y, "Date"), (374, y, "Time InTime OutTotal Hrs"),
         (65, y - 15, "1"), (102, y - 15, "10042"), (165, y - 15, "Asha Quill"), (316, y - 15, "05/10/2026"),
         (380, y - 15, "11:22"), (424, y - 15, "16:26"), (464, y - 15, "05:04:00")],
    ]
    cases = [read_case(f"fixed{i}.pdf", pdf_bytes([p])) for i, p in enumerate(fixed)]
    cases.append(read_case("news.pdf", pdf_bytes([[(72, 500, "Quarterly newsletter")]])))
    cases.append(read_case("blank.pdf", pdf_bytes([[]])))
    cases.append(read_case("junk.pdf", b"%PDF-1.4 this is not really a pdf"))
    cases += [pdf_table_case(i) for i in range(60)]
    return cases


# ------------------------------------------------------------------ cell values

def typed(v):
    if v is None:
        return None
    if isinstance(v, bool):
        return {"b": v}
    if isinstance(v, (int, float)):
        return {"f": "nan" if v != v else float(v)}
    if isinstance(v, datetime):
        return {"dt": v.isoformat()}
    if isinstance(v, time):
        return {"t": v.isoformat()}
    if isinstance(v, timedelta):
        return {"td": v.total_seconds()}
    return {"s": v}


def outcome(fn, v):
    try:
        r = fn(v)
    except ValueError as e:
        return {"error": str(e)}
    if isinstance(r, (date, time)):
        return {"value": r.isoformat()}
    return {"value": r}


def value_cases() -> dict:
    texts = ["31/03/2026", "31-03-2026", "2026-03-31", "31-Mar-2026", "31 March 2026", "03/31/2026", "2026/3/31", "1 sept 2026",
             "01 Sep 2026", "31-Feb-2026", "9-1-2026", "09-01-2026", "29/02/2025", "29/02/2028", " 5/6/2026 ", "5.6.2026", "",
             "2026-13-01", "1/1/26", "15/09/2026", "30-09-2026", "Oct 5 2026", "05 OCT 2026", "5-oct-2026", "2026/10/05",
             "7.5", "7:30", "08:37:00", "30", "25:99", "9:5", " 9 PM", "12 AM", "12 PM", "6:15 PM", "6:15:30 pm", "24:00", "0:00",
             "1_0", "nan", "-1", "abc", "Eight", ".5", "5.", "1.5e1", "7.5 hrs", "7h", "8 h 30", "1:2", "12:60",
             "23:59:60", "09:30 AM", "13:00 PM", "0 AM", "11:59:59 PM", "٣:٣٠"]
    values = [None, 7.5, 8, 0, 25, -1, 0.25, 2.675, True, datetime(2026, 10, 5, 9, 30), datetime(2026, 1, 10),
              datetime(2026, 9, 18), time(9, 30), time(18, 15, 30, 500000), timedelta(hours=8, minutes=30, seconds=59),
              timedelta(hours=25), timedelta(minutes=-5), float("nan")]
    singles = []
    for v in [*texts, *values]:
        singles.append({"input": typed(v), "date": outcome(parse_date, v),
                        "candidates": [[d.isoformat(), s] for d, s in date_candidates(v)],
                        "hours": outcome(parse_hours, v), "time": outcome(parse_time, v)})
    readers = []
    seq_pool = ["15/09/2026", "30-09-2026", datetime(2026, 9, 18), "09-01-2026", datetime(2026, 1, 9), datetime(2026, 9, 5),
                "05/06/2026", "01/10/2026", "10-01-2026", "13/10/2026", datetime(2026, 2, 10), "2026-10-14", "x", None, 45000]
    readers.append(["15/09/2026", "30-09-2026", datetime(2026, 9, 18), "09-01-2026", datetime(2026, 1, 9), datetime(2026, 9, 5)])
    readers.append(["05/06/2026"])
    for _ in range(60):
        readers.append([rnd.choice(seq_pool) for _ in range(rnd.randint(1, 25))])
    reader_cases = []
    for seq in readers:
        r = DateReader.for_values(seq)
        reads = [r.read(v) for v in seq]
        reader_cases.append({"values": [typed(v) for v in seq], "months": sorted([list(m) for m in r.months]),
                             "reads": [d.isoformat() if d else None for d in reads], "month_first": r.month_first,
                             "ambiguous_day_first": r.ambiguous_day_first})
    return {"singles": singles, "readers": reader_cases}


# ------------------------------------------------------------------ files for the API tests (ports of the pytest cases)

def api_files() -> None:
    """The workbooks and the PDF the Python upload tests build in code (test_wfo_upload.py, test_timesheet_files.py)."""
    from openpyxl import Workbook
    folder = OUT.parent / "uploads"
    folder.mkdir(exist_ok=True)

    def save(wb, name):
        wb.properties.created = wb.properties.modified = datetime(2026, 11, 1)
        buf = io.BytesIO()
        wb.save(buf)
        (folder / name).write_bytes(buf.getvalue())

    wb = Workbook()
    raw = wb.active
    raw.title = "Raw Data"
    raw.append(["Code", "Employee", "Date", "Time In", "Time Out", "Total Hrs"])
    raw.append(["EMP001", "Person EMP001", "05/10/2026", "09:30", "18:00", "08:30:00"])
    month = wb.create_sheet("October 2026")
    month.append(["Employee ID", "Employee", "Date", None, "Row Labels", "Count of Date"])
    rows = [("EMP001", "Person EMP001", datetime(2026, 1, 10)), ("EMP001", "Person EMP001", datetime(2026, 2, 10)),
            ("EMP001", "Person EMP001", "14/10/2026"), ("EMP001", "Person EMP001", "14-10-2026"),
            ("EMP001", "Person EMP001", datetime(2026, 10, 15)), ("EMP002", "Somebody Else", "20/10/2026"),
            ("Intern - 10001", "New Intern", "21/10/2026"), ("Intern  -  10001", "New Intern", datetime(2026, 10, 22)),
            ("Intern - 10001", "N. Intern", "23/10/2026")]
    for i, (c, n, d) in enumerate(rows):
        month.append([c, n, d, None, f"Label {i}", 1])
    for i in range(5):
        month.append([None, None, None, None, f"More {i}", 2])
    month.append([None, None, None, None, "Grand Total", 19])
    save(wb, "monthly_workbook.xlsx")

    wb = Workbook()
    raw = wb.active
    raw.title = "TS Raw Data"
    raw.append(["Code", "Employee", "Date", "Time In", "Time Out", "Total Hrs"])
    raw.append(["EMP001-X", "Person EMP001", datetime(2026, 10, 1), time(9, 30), time(18, 0), time(8, 30)])
    raw.append(["EMP003", "Person EMP003", datetime(2026, 10, 9), time(10, 0), time(12, 0), time(2, 0)])
    month = wb.create_sheet("October 2026")
    month.append(["Emp. Code", "Emp. Name", "Date"])
    month.append(["EMP001", "Person EMP001", datetime(2026, 10, 1)])
    month.append(["EMP001", "Person EMP001", datetime(2026, 10, 2)])
    month.append(["Intern-9", "Person EMP002", datetime(2026, 10, 2)])
    save(wb, "two_sheet_workbook.xlsx")

    rows = [("E005", "Rahul Reddy", "01-10-2026"), ("E005", "Rahul Reddy", "02/10/2026"), ("E006", "Sneha Rao", "10-01-2026"),
            ("E006", "Sneha Rao", "15-10-2026"), ("E007", "Arjun Kumar", "16/10/2026")]
    pages = []
    for start in range(0, len(rows), 3):
        y = 540.0
        cells = [(57, y, "Employee ID"), (181, y, "Employee"), (357, y, "Date"), (502, y, "Row Labels"), (632, y, "Count of Date")]
        for i, (c, n, d) in enumerate(rows[start:start + 3]):
            y -= 15
            cells += [(72, y, c), (171, y, n), (343, y, d), (502, y, f"Label {start + i}"), (632, y, "1")]
        pages.append(cells)
    (folder / "october_sheet.pdf").write_bytes(pdf_bytes(pages))


def main() -> None:
    if len(sys.argv) > 2 and sys.argv[1] == "--local":
        out, files = Path(sys.argv[2]), sys.argv[3:]
        cases = []
        for f in files:
            p = Path(f)
            case = read_case(p.name, p.read_bytes(), embed=False)
            case["path"] = str(p.resolve())
            cases.append(case)
        out.write_text(json.dumps({"files": cases}), encoding="utf-8")
        print(f"{len(cases)} files -> {out}")
        return
    api_files()
    data = {"files": csv_cases() + xlsx_cases() + pdf_cases(), "values": value_cases()}
    OUT.write_bytes(gzip.compress(json.dumps(data, sort_keys=True).encode(), mtime=0))
    print(f"{len(data['files'])} files, {len(data['values']['singles'])} values, {len(data['values']['readers'])} readers -> "
          f"{OUT} ({OUT.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    main()
