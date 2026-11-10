"""Reading timesheet files into rows of cells: CSV / TSV / TXT, Excel (.xlsx, .xlsm, .xls) and PDF.

* A workbook may hold several sheets: the one named like a month ("September 2026") that has an employee
  and a date column is read, else the first sheet that has them (or the sheet the Admin picks).
* The header row can be any of the first ``HEADER_SEARCH_ROWS`` rows.
* A PDF is read from its text: words are grouped into rows by their height on the page and into columns
  by the header's positions, so a table exported or printed to PDF reads like the sheet it came from.
  Text outside the header's columns (a pivot table beside the data) is dropped, and the header repeated
  on later pages is skipped. A scanned PDF (an image, no text) cannot be read.

Cells are returned as Python values (text, numbers, dates, times); interpreting them is the job of
``app.services.wfo_upload``.
"""

from __future__ import annotations

import csv
import io
import re
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime

from app.core.errors import ValidationFailed
from app.core.wfo_mapping import get_mapping, normalize

MAX_UPLOAD_BYTES = 5 * 1024 * 1024
HEADER_SEARCH_ROWS = 20
ACCEPTED = (".csv", ".tsv", ".txt", ".xlsx", ".xlsm", ".xls", ".pdf")
ACCEPTED_TEXT = "CSV, TSV, TXT, Excel (.xlsx, .xls) or PDF"
_MONTH_NAME = re.compile(r"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\b|\b\d{4}-\d{2}\b",
                         re.IGNORECASE)


@dataclass
class Table:
    rows: list[list]
    sheets: list[dict] = field(default_factory=list)  # workbook sheets / PDF: {"name", "usable", "rows"}
    sheet: str | None = None                           # the sheet that was read
    file_type: str = "csv"                             # csv | excel | pdf


def cell_text(v) -> str:
    if v is None:
        return ""
    if isinstance(v, float) and v.is_integer():
        return str(int(v))
    return str(v).strip()


def map_header(header: list, mapping) -> dict[str, int]:
    """Column key -> index. With both an ID column ("Employee ID") and a plain "Employee" column, the plain
    one holds the names."""
    candidates: dict[str, list[int]] = defaultdict(list)
    for i, h in enumerate(header):
        key = mapping.column(cell_text(h))
        if key:
            candidates[key].append(i)
    columns = {k: v[0] for k, v in candidates.items()}
    ids = candidates.get("employee", [])
    if len(ids) > 1 and "name" not in columns:
        plain = [i for i in ids if normalize(cell_text(header[i])) == "employee"]
        specific = [i for i in ids if i not in plain]
        if plain and specific:
            columns["employee"], columns["name"] = specific[0], plain[0]
    return columns


def find_header(rows: list[list], mapping) -> tuple[int, dict[str, int]] | None:
    """(row index, columns) of the first of the top rows that has an employee and a date column."""
    for i, row in enumerate(rows[:HEADER_SEARCH_ROWS]):
        columns = map_header(row, mapping)
        if "employee" in columns and "date" in columns:
            return i, columns
    return None


# ------------------------------------------------------------------ formats

def _csv_rows(content: bytes) -> list[list]:
    try:
        text = content.decode("utf-8-sig")
    except UnicodeDecodeError:
        text = content.decode("cp1252", errors="replace")
    delimiter = max([",", ";", "\t", "|"], key=text[:4096].count)
    return [row for row in csv.reader(io.StringIO(text), delimiter=delimiter)]


def _xlsx_sheets(content: bytes) -> dict[str, list[list]]:
    from openpyxl import load_workbook
    try:
        wb = load_workbook(io.BytesIO(content), read_only=True, data_only=True)
    except Exception:  # noqa: BLE001 - any parse failure is a bad file for the user
        raise ValidationFailed("This Excel file could not be read") from None
    try:
        return {n: [list(r) for r in wb[n].iter_rows(values_only=True)] for n in wb.sheetnames}
    finally:
        wb.close()


def _xls_sheets(content: bytes) -> dict[str, list[list]]:
    """Excel 97-2003 (.xls)."""
    import xlrd
    try:
        book = xlrd.open_workbook(file_contents=content, on_demand=True)
    except Exception:  # noqa: BLE001
        raise ValidationFailed("This Excel (.xls) file could not be read") from None
    out: dict[str, list[list]] = {}
    try:
        for sh in book.sheets():
            rows = []
            for r in range(sh.nrows):
                row = []
                for c in range(sh.ncols):
                    cell = sh.cell(r, c)
                    if cell.ctype == xlrd.XL_CELL_DATE:
                        try:
                            value = xlrd.xldate_as_datetime(cell.value, book.datemode)
                        except Exception:  # noqa: BLE001 - an impossible date stays a number
                            value = cell.value
                        # A time-only cell (no date part) is a time of day.
                        row.append(value.time() if isinstance(value, datetime) and cell.value < 1 else value)
                    elif cell.ctype in (xlrd.XL_CELL_EMPTY, xlrd.XL_CELL_BLANK):
                        row.append(None)
                    else:
                        row.append(cell.value)
                rows.append(row)
            out[sh.name] = rows
    finally:
        book.release_resources()
    return out


@dataclass
class _Word:
    x0: float
    x1: float
    y: float
    text: str

    @property
    def center(self) -> float:
        return (self.x0 + self.x1) / 2


def _page_words(page) -> list[_Word]:
    """Words of one PDF page: a text line is split where the gap between characters is wider than a space,
    so cells that sit close together on one line still come out separately."""
    from pdfminer.layout import LTChar, LTTextContainer, LTTextLine
    words: list[_Word] = []

    def lines(el):
        if isinstance(el, LTTextLine):
            yield el
        elif isinstance(el, LTTextContainer) or hasattr(el, "__iter__"):
            for child in el:
                yield from lines(child)

    for line in lines(page):
        chars = [c for c in line if isinstance(c, LTChar)]
        current: list = []
        for c in chars:
            if current and (c.x0 - current[-1].x1) > max(0.8 * c.size, 4):
                words.append(_cell_from(current))
                current = []
            if c.get_text().strip() or current:
                current.append(c)
        if current:
            words.append(_cell_from(current))
    return [w for w in words if w.text]


def _cell_from(chars) -> _Word:
    return _Word(chars[0].x0, chars[-1].x1, (chars[0].y0 + chars[0].y1) / 2,
                 "".join(c.get_text() for c in chars).strip())


def _rows_by_height(words: list[_Word]) -> list[list[_Word]]:
    rows: list[list[_Word]] = []
    for w in sorted(words, key=lambda w: (-w.y, w.x0)):
        if rows and abs(rows[-1][0].y - w.y) <= 3:
            rows[-1].append(w)
        else:
            rows.append([w])
    return [sorted(r, key=lambda w: w.x0) for r in rows]


def _bounds(header: list[_Word]) -> list[tuple[float, float]]:
    """Each header column owns the space halfway to its neighbours; the outer columns as far again outwards."""
    centers = [w.center for w in header]
    out = []
    for i, c in enumerate(centers):
        left = (centers[i - 1] + c) / 2 if i > 0 else None
        right = (c + centers[i + 1]) / 2 if i + 1 < len(centers) else None
        half = (c - left) if left is not None else (right - c) if right is not None else 200
        out.append((left if left is not None else c - max(half, header[i].center - header[i].x0 + 10),
                    right if right is not None else c + max(half, header[i].x1 - header[i].center + 10)))
    return out


def _pdf_table(content: bytes) -> list[list]:
    try:
        from pdfminer.high_level import extract_pages
        from pdfminer.pdfparser import PDFSyntaxError
    except ImportError:  # pragma: no cover - dependency listed in requirements.txt
        raise ValidationFailed("PDF reading is not installed on the server (pdfminer.six)") from None
    mapping = get_mapping()
    out: list[list] = []
    header_texts: list[str] | None = None
    bounds: list[tuple[float, float]] = []
    any_text = False
    try:
        pages = list(extract_pages(io.BytesIO(content)))
    except (PDFSyntaxError, Exception):  # noqa: BLE001 - broken or encrypted file
        raise ValidationFailed("This PDF could not be read (it may be damaged or password-protected)") from None
    for page in pages:
        rows = _rows_by_height(_page_words(page))
        any_text = any_text or bool(rows)
        for row in rows:
            texts = [w.text for w in row]
            if header_texts is None:
                cols = map_header(texts, mapping)
                if "employee" in cols and "date" in cols:
                    header_texts, bounds = texts, _bounds(row)
                    out.append(list(texts))
                continue
            cells: list = [None] * len(header_texts)
            for w in row:
                for i, (lo, hi) in enumerate(bounds):
                    if lo <= w.center < hi:
                        cells[i] = w.text if cells[i] is None else f"{cells[i]} {w.text}"
                        break
            if cells == header_texts:
                continue  # the header again on a new page
            if any(c is not None for c in cells):
                out.append(cells)
    if not any_text:
        raise ValidationFailed("This PDF has no text to read (it looks like a scanned image). Upload the "
                               "Excel or CSV file it came from instead.")
    if header_texts is None:
        first = next((r for r in _rows_by_height(_page_words(pages[0]))), []) if pages else []
        raise ValidationFailed("Could not find an employee and a date column in this PDF. Found: "
                               f"{', '.join(w.text for w in first[:8]) or 'nothing'}")
    return out


# ------------------------------------------------------------------ entry point

def _choose(data: dict[str, list[list]], sheet: str | None, file_type: str) -> Table:
    mapping = get_mapping()
    info = []
    for n, rows in data.items():
        found = find_header(rows, mapping)
        info.append({"name": n, "usable": found is not None,
                     "rows": sum(1 for r in rows[found[0] + 1:] if any(cell_text(c) for c in r)) if found else 0})
    names = list(data)
    if not names:
        raise ValidationFailed("This workbook has no sheets")
    if sheet is not None:
        if sheet not in data:
            raise ValidationFailed(f"There is no sheet {sheet!r}. Sheets: {', '.join(names)}")
        chosen = sheet
    else:
        # A monthly workbook keeps raw extracts next to the month's sheet ("September 2026"): prefer that one.
        usable = [i["name"] for i in info if i["usable"]]
        chosen = next((n for n in usable if _MONTH_NAME.search(n)), usable[0] if usable else names[0])
    return Table(data[chosen], info, chosen, file_type)


def read_workbook(filename: str, content: bytes, sheet: str | None = None) -> Table:
    """Rows of the file (of the chosen sheet for a workbook). Raises ValidationFailed for unsupported or
    broken files."""
    name = filename.lower()
    if len(content) > MAX_UPLOAD_BYTES:
        raise ValidationFailed(f"The file is larger than {MAX_UPLOAD_BYTES // (1024 * 1024)} MB")
    if not content:
        raise ValidationFailed("The file is empty")
    if name.endswith((".csv", ".tsv", ".txt")):
        return Table(_csv_rows(content))
    if name.endswith((".xlsx", ".xlsm")):
        return _choose(_xlsx_sheets(content), sheet, "excel")
    if name.endswith(".xls"):
        return _choose(_xls_sheets(content), sheet, "excel")
    if name.endswith(".pdf"):
        return Table(_pdf_table(content), file_type="pdf")
    if name.endswith((".ods", ".numbers")):
        raise ValidationFailed("Save this spreadsheet as Excel (.xlsx) or CSV and upload that")
    raise ValidationFailed(f"Upload a {ACCEPTED_TEXT} file")


def read_table(filename: str, content: bytes) -> list[list]:
    return read_workbook(filename, content).rows
