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
