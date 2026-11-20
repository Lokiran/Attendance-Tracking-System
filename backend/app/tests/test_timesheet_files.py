"""Reading timesheet files: PDF tables, text formats and file checks (synthetic data only)."""

import pytest

from app.core.errors import ValidationFailed
from app.services.timesheet_files import read_workbook
from app.tests.conftest import login

URL = "/api/admin/attendance-uploads"


def make_pdf(pages: list[list[tuple[float, float, str]]]) -> bytes:
    """A minimal PDF: each page is a list of (x, y, text) drawn in Helvetica 10 pt (landscape A4-ish)."""
    objects: list[bytes] = []

    def add(body: bytes) -> int:
        objects.append(body)
        return len(objects)

    font = add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
    pages_id = len(objects) + 2 * len(pages) + 1
    kids = []
    for cells in pages:
        stream = b"".join(
            b"BT /F1 10 Tf %.1f %.1f Td (%s) Tj ET\n" % (x, y, t.replace("(", r"\(").replace(")", r"\)").encode())
            for x, y, t in cells)
        content = add(b"<< /Length %d >>\nstream\n%s\nendstream" % (len(stream), stream))
        kids.append(add(b"<< /Type /Page /Parent %d 0 R /MediaBox [0 0 792 612] /Contents %d 0 R "
                        b"/Resources << /Font << /F1 %d 0 R >> >> >>" % (pages_id, content, font)))
    assert add(b"<< /Type /Pages /Kids [%s] /Count %d >>" % (b" ".join(b"%d 0 R" % k for k in kids), len(kids))) \
        == pages_id
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


def sheet_pdf(rows: list[tuple[str, str, str]], per_page: int = 3) -> bytes:
    """A printed sheet: Employee ID / Employee / Date columns, a pivot table to the right, header repeated."""
    pages = []
    for start in range(0, len(rows), per_page):
        y = 540.0
        cells = [(57, y, "Employee ID"), (181, y, "Employee"), (357, y, "Date"), (502, y, "Row Labels"),
                 (632, y, "Count of Date")]
        for i, (code, name, d) in enumerate(rows[start:start + per_page]):
            y -= 15
            cells += [(72, y, code), (171, y, name), (343, y, d), (502, y, f"Label {start + i}"), (632, y, "1")]
        pages.append(cells)
    return make_pdf(pages)


ROWS = [("E005", "Rahul Reddy", "01-10-2026"), ("E005", "Rahul Reddy", "02/10/2026"),
        ("E006", "Sneha Rao", "10-01-2026"),   # month first: 1 October
        ("E006", "Sneha Rao", "15-10-2026"), ("E007", "Arjun Kumar", "16/10/2026")]


def test_pdf_table_is_rebuilt_from_text_positions():
    t = read_workbook("October 2026.pdf", sheet_pdf(ROWS))
    assert t.file_type == "pdf"
    assert t.rows[0] == ["Employee ID", "Employee", "Date", "Row Labels", "Count of Date"]
    body = [r[:3] for r in t.rows[1:]]
    assert body == [list(r) for r in ROWS]  # header repeated on page 2 is dropped, every row once


def test_pdf_timesheet_imports_like_a_sheet(client, frozen, org):
    from datetime import date, time
    frozen.set_local(date(2026, 11, 10), time(12, 0))
    h = login(client, org.admin)
    r = client.post(URL, headers=h, files={"file": ("October 2026.pdf", sheet_pdf(ROWS), "application/pdf")})
    assert r.status_code == 201, r.text
    s = r.json()["summary"]
    assert s["presence_list"] and s["error_rows"] == 0 and s["office"] == 5
    assert s["columns"] == {"employee": "Employee ID", "name": "Employee", "date": "Date"}
    assert any("swapped" in n for n in s["date_notes"])
    assert s["date_from"] == "2026-10-01" and s["date_to"] == "2026-10-16"
    assert r.json()["error_count"] == 0 and s["name_mismatches"] == []


def test_pdf_without_a_table_or_text_is_refused():
    with pytest.raises(ValidationFailed, match="Could not find an employee and a date column"):
        read_workbook("x.pdf", make_pdf([[(72, 500, "Quarterly newsletter")]]))
    with pytest.raises(ValidationFailed, match="no text"):
        read_workbook("x.pdf", make_pdf([[]]))
    with pytest.raises(ValidationFailed, match="could not be read"):
        read_workbook("x.pdf", b"%PDF-1.4 this is not really a pdf")


def test_text_formats_and_unsupported_files():
    assert read_workbook("t.tsv", b"Employee\tDate\nE1\t01/10/2026\n").rows[1] == ["E1", "01/10/2026"]
    assert read_workbook("t.txt", b"Employee|Date\nE1|01/10/2026\n").rows[1] == ["E1", "01/10/2026"]
    with pytest.raises(ValidationFailed, match="could not be read"):
        read_workbook("t.xls", b"not an excel file")
    with pytest.raises(ValidationFailed, match="Save this spreadsheet as Excel"):
        read_workbook("t.ods", b"x")
    with pytest.raises(ValidationFailed, match="PDF"):
        read_workbook("t.docx", b"x")


def test_pdf_date_glued_to_a_long_name_is_split_off():
    """A name that fills its cell is printed right up against the date: one run of text in the PDF."""
    y = 540.0
    page = [(57, y, "Emp. ID"), (181, y, "Employee Name"), (357, y, "Date"),
            (72, y - 15, "E005"), (171, y - 15, "Rahul Reddy"), (343, y - 15, "2026-10-01"),
            (72, y - 30, "E006"), (120, y - 30, "A Very Long Name That Fills The Whole Cell2026-10-02"),
            (72, y - 45, "E007"), (120, y - 45, "Another Long Name That Fills The Cell 215/10/2026")]
    t = read_workbook("October 2026.pdf", make_pdf([page]))
    assert t.rows[1] == ["E005", "Rahul Reddy", "2026-10-01"]
    assert t.rows[2] == ["E006", "A Very Long Name That Fills The Whole Cell", "2026-10-02"]
    assert t.rows[3][2].endswith("/10/2026") and t.rows[3][1].startswith("Another Long Name")


def test_borderless_pdf_with_glued_header_and_ids_is_split():
    """A sheet printed without grid lines: the time headers run together ("Time InTime OutTotal Hrs") and a
    right-aligned ID touches the name beside it ("10001Rahul Reddy")."""
    y = 540.0
    page = [(53, y, "Code"), (110, y, "Employee"), (227, y, "Date"), (280, y, "Time InTime OutTotal Hrs"),
            (81, y - 15, "10001Rahul Reddy"), (230, y - 15, "10/1/2026"), (287, y - 15, "09:40"),
            (329, y - 15, "17:39"), (375, y - 15, "7:59"),
            (60, y - 30, "20002-ExtSneha Rao"), (230, y - 30, "10/2/2026"), (287, y - 30, "10:05"),
            (329, y - 30, "12:15"), (375, y - 30, "2:10"),
            (53, y - 45, "Guest 01"), (110, y - 45, "Guest 01"), (230, y - 45, "10/2/2026")]
    t = read_workbook("October 2026.pdf", make_pdf([page]))
    assert t.rows[0] == ["Code", "Employee", "Date", "Time In", "Time Out", "Total Hrs"]
    assert t.rows[1] == ["10001", "Rahul Reddy", "10/1/2026", "09:40", "17:39", "7:59"]
    assert t.rows[2] == ["20002-Ext", "Sneha Rao", "10/2/2026", "10:05", "12:15", "2:10"]
    assert t.rows[3][:3] == ["Guest 01", "Guest 01", "10/2/2026"]  # already separate: unchanged


def test_serial_number_label_glued_to_the_id_label_is_split():
    """"Sr No.Code": the serial-number header runs into the ID header; the ID column must still be found
    (otherwise the name column is taken as the employee reference)."""
    y = 540.0
    page = [(53, y, "Sr No.Code"), (150, y, "Employee"), (315, y, "Date"), (374, y, "Time InTime OutTotal Hrs"),
            (65, y - 15, "1"), (102, y - 15, "16942"), (165, y - 15, "Rahul Reddy"), (316, y - 15, "05/10/2026"),
            (380, y - 15, "11:22"), (424, y - 15, "16:26"), (464, y - 15, "05:04:00")]
    t = read_workbook("October 2026.pdf", make_pdf([page]))
    assert t.rows[0][-6:] == ["Code", "Employee", "Date", "Time In", "Time Out", "Total Hrs"]
    assert t.rows[1][-6:] == ["16942", "Rahul Reddy", "05/10/2026", "11:22", "16:26", "05:04:00"]
