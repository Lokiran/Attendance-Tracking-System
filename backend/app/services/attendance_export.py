"""One employee's attendance as an Excel workbook or a branded PDF.

Both files are built from the same data as the dashboard (``compute_records`` + the WFO policy figures), with
the same access rule as the CSV export: yourself, your reporting line (managers) or anyone (HR admin).

Branding comes from settings, never from the code: ``REPORT_COMPANY_NAME``, ``REPORT_LOGO_FILE`` (an image
for the coloured header band, so a light / white logo) and ``REPORT_BRAND_COLOR`` / ``REPORT_ACCENT_COLOR``.
Without them the report uses the app's own navy and prints no logo.
"""

from __future__ import annotations

import calendar
import io
import logging
import re
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta
from pathlib import Path

from sqlalchemy.orm import Session

from app.core import clock
from app.core.config import BACKEND_DIR, get_settings
from app.core.timeutil import local_date, office_tz
from app.models import User
from app.models.enums import DayStatus
from app.services import wfo
from app.services.attendance_data import compute_records
from app.services.attendance_rules import DayRecord, Summary, summarize
from app.services.scope import get_viewable_employee
from app.services.wfo_rules import WFO_NEAR_DAYS, wfo_band

logger = logging.getLogger("ats.reports")

STATUS_LABEL = {
    DayStatus.PRESENT: "In office", DayStatus.WFH: "Work from home", DayStatus.ABSENT: "Absent",
    DayStatus.LEAVE: "Leave", DayStatus.HOLIDAY: "Holiday", DayStatus.WEEKEND: "Weekly off",
    DayStatus.NO_TIMESHEET: "No timesheet", DayStatus.NOT_SCHEDULED: "Not scheduled",
}
# The dashboard's status colours (always printed next to the label, never colour alone).
STATUS_COLOR = {
    DayStatus.PRESENT: "0CA30C", DayStatus.WFH: "0891B2", DayStatus.ABSENT: "D03B3B", DayStatus.LEAVE: "B45309",
    DayStatus.HOLIDAY: "6250D6", DayStatus.WEEKEND: "94A3B8", DayStatus.NO_TIMESHEET: "94A3B8",
    DayStatus.NOT_SCHEDULED: "94A3B8",
}
BAND_LABEL = {"MET": "On target", "NEAR": "Close", "BELOW": "Below target"}
BAND_COLOR = {"MET": "0CA30C", "NEAR": "EDA100", "BELOW": "D03B3B"}
BAND_SOFT = {"MET": "E3F4E3", "NEAR": "FCF1D6", "BELOW": "FBE4E4"}
_HEX = re.compile(r"^#?([0-9A-Fa-f]{6})$")
RISKY = ("=", "+", "-", "@", "\t", "\r")


# ------------------------------------------------------------------ data

@dataclass
class MonthRow:
    label: str                 # "Sep 2026"
    start: date
    end: date
    partial: bool              # the period covers only part of this month
    working_days: int
    office_days: int
    required_days: int
    band: str
    wfh_days: int
    leave_days: int
    absent_days: int
    no_timesheet_days: int
    attendance_percentage: float | None
    average_minutes: int | None
    covered: bool = True       # a timesheet covers this month for this person

    @property
    def status(self) -> str:
        return BAND_LABEL[self.band] if self.covered else "No timesheet"

    @property
    def color(self) -> str:
        return BAND_COLOR[self.band] if self.covered else "94A3B8"

    @property
    def soft(self) -> str:
        return BAND_SOFT[self.band] if self.covered else "F1F5F9"


@dataclass
class Brand:
    company: str
    color: str                 # hex without '#'
    accent: str
    logo: Path | None


@dataclass
class Report:
    employee: User
    start: date
    end: date
    generated_at: datetime
    records: list[DayRecord]
    summary: Summary
    wfo: dict                  # wfo.range_summary for the whole period
    months: list[MonthRow] = field(default_factory=list)
    brand: Brand | None = None

    @property
    def office_days(self) -> int:
        return self.wfo["wfo_days"]

    @property
    def required_days(self) -> int:
        """Office days asked for in the period: months without a timesheet for this person ask for nothing."""
        if not self.months:
            return self.wfo["required_days"]
        return sum(m.required_days for m in self.months if m.covered)

    @property
    def band(self) -> str:
        return wfo_band(self.office_days, self.required_days)

    @property
    def target_note(self) -> str:
        covered = [m for m in self.months if m.covered]
        if len(covered) > 1:
            met = sum(1 for m in covered if m.band == "MET")
            if met == len(covered):
                return f"On target in all {met} months"
            return f"{BAND_LABEL[self.band]} · on target in {met} of {len(covered)} months"
        return BAND_LABEL[self.band]


def _hex(value: str, default: str) -> str:
    m = _HEX.match((value or "").strip())
    return m.group(1).upper() if m else default


def brand() -> Brand:
    s = get_settings()
    logo = None
    if s.report_logo_file.strip():
        path = Path(s.report_logo_file.strip())
        path = path if path.is_absolute() else BACKEND_DIR / path
        if path.is_file():
            logo = path
        else:
            logger.warning("REPORT_LOGO_FILE not found: %s (the report is generated without a logo)", path)
    return Brand(company=s.report_company_name.strip(), color=_hex(s.report_brand_color, "0F172A"),
                 accent=_hex(s.report_accent_color, "2563EB"), logo=logo)


def _months(start: date, end: date):
    d = start.replace(day=1)
    while d <= end:
        last = d.replace(day=calendar.monthrange(d.year, d.month)[1])
        yield d, last
        d = last + timedelta(days=1)


def gather(db: Session, viewer: User, employee_id: int, start: date, end: date) -> Report:
    employee = get_viewable_employee(db, viewer, employee_id)
    records = compute_records(db, [employee], start, end)[employee.id]
    report = Report(employee=employee, start=start, end=end, generated_at=clock.utcnow(), records=records,
                    summary=summarize(records, start, end), wfo=wfo.range_summary(db, employee, start, end),
                    brand=brand())
    report.months = month_rows(db, employee, records, start, end)
    return report


def month_rows(db: Session, employee: User, records: list[DayRecord], start: date, end: date) -> list[MonthRow]:
    """WFO and attendance figures for each calendar month of start..end (up to the current month)."""
    today = local_date(clock.utcnow(), office_tz())
    out: list[MonthRow] = []
    for first, last in _months(start, end):
        m_start, m_end = max(first, start), min(last, end)
        if m_start > today:
            break
        w = wfo.range_summary(db, employee, m_start, m_end)
        s = summarize([r for r in records if m_start <= r.date <= m_end], m_start, m_end)
        out.append(MonthRow(
            label=first.strftime("%b %Y"), start=m_start, end=m_end, partial=(m_start, m_end) != (first, last),
            working_days=w["working_days"], office_days=w["wfo_days"], required_days=w["required_days"],
            band=w["wfo_band"], wfh_days=w["wfh_days"], leave_days=w["leave_days"], absent_days=s.absent_days,
            no_timesheet_days=s.no_timesheet_days, attendance_percentage=s.attendance_percentage,
            average_minutes=s.average_net_minutes,
            covered=(s.present_days + s.absent_days + s.wfh_days + s.leave_days) > 0))
    return out


def month_dict(m: MonthRow) -> dict:
    return {"label": m.label, "start": m.start, "end": m.end, "partial": m.partial, "covered": m.covered,
            "working_days": m.working_days, "office_days": m.office_days, "required_days": m.required_days,
            "wfo_band": m.band, "wfh_days": m.wfh_days, "leave_days": m.leave_days, "absent_days": m.absent_days,
            "attendance_percentage": m.attendance_percentage, "average_net_minutes": m.average_minutes}


def _hm(minutes: int | None) -> str:
    if minutes is None:
        return "—"
    return f"{minutes // 60}h {minutes % 60:02d}m"


def _clock(value: datetime | None) -> str:
    return value.astimezone(office_tz()).strftime("%H:%M") if value else ""


def _pct(value: float | None) -> str:
    return "—" if value is None else f"{value:.1f}%"


def _period(start: date, end: date) -> str:
    return f"{start.strftime('%d %b %Y')} – {end.strftime('%d %b %Y')}"


def filename(report: Report, ext: str) -> str:
    code = re.sub(r"[^A-Za-z0-9._-]+", "_", report.employee.employee_code).strip("_") or "employee"
    return f"attendance_{code}_{report.start}_{report.end}.{ext}"


def _facts(r: Report) -> list[tuple[str, str]]:
    e = r.employee
    return [
        ("Employee", e.full_name), ("Employee ID", e.employee_code),
        ("Department", e.department.name if e.department else "—"),
        ("Reports to", e.manager.full_name if e.manager else "—"),
        ("Period", _period(r.start, r.end)),
        ("Generated", r.generated_at.astimezone(office_tz()).strftime("%d %b %Y, %H:%M")),
    ]


def _band_rule(required: int) -> str:
    return (f"On target: {required} or more office days · Close: up to {WFO_NEAR_DAYS} short · "
            f"Below target: more than {WFO_NEAR_DAYS} short")


# ------------------------------------------------------------------ Excel

def _safe(value):
    """Text a spreadsheet could run as a formula gets a leading apostrophe (CWE-1236)."""
    if isinstance(value, str) and value.startswith(RISKY):
        return "'" + value
    return value


def to_xlsx(r: Report) -> bytes:
    from openpyxl import Workbook
    from openpyxl.styles import Alignment, Font, PatternFill
    from openpyxl.utils import get_column_letter

    b = r.brand or brand()
    head_fill = PatternFill("solid", fgColor=b.color)
    head_font = Font(bold=True, color="FFFFFF")
    muted = Font(color="64748B")

    def header(ws, row: int, titles: list[str]):
        for i, t in enumerate(titles, start=1):
            c = ws.cell(row=row, column=i, value=t)
            c.font, c.fill = head_font, head_fill
            c.alignment = Alignment(vertical="center", wrap_text=True)

    def widths(ws, values: list[int]):
        for i, w in enumerate(values, start=1):
            ws.column_dimensions[get_column_letter(i)].width = w

    wb = Workbook()
    ws = wb.active
    ws.title = "Summary"
    ws["A1"] = (b.company + " · " if b.company else "") + "Attendance report"
    ws["A1"].font = Font(bold=True, size=14, color=b.color)
    row = 3
    for label, value in _facts(r):
        ws.cell(row=row, column=1, value=label).font = muted
        ws.cell(row=row, column=2, value=_safe(value)).font = Font(bold=True)
        row += 1
    row += 1
    w, s = r.wfo, r.summary
    header(ws, row, ["Measure", "Value", "Note"])
    figures = [
        ("Office (WFO) days", r.office_days, f"of {r.required_days} required · {r.target_note}"),
        ("Attendance rate", _pct(s.attendance_percentage), f"{s.present_days} of {s.counted_days} working days in the office"),
        ("Average office hours", _hm(s.average_net_minutes), f"on {s.hours_days} day(s) with recorded hours" if s.hours_days else "not in the timesheets for this period"),
        ("Required hours", _hm(s.required_minutes), f"{s.expected_days} scheduled day(s)"),
        ("Absent days", s.absent_days, ""), ("Work from home days", s.wfh_days, ""), ("Leave days", s.leave_days, ""),
        ("Days without a timesheet", s.no_timesheet_days, "not counted as absent"),
    ]
    for label, value, note in figures:
        row += 1
        ws.cell(row=row, column=1, value=label)
        ws.cell(row=row, column=2, value=value).font = Font(bold=True)
        ws.cell(row=row, column=3, value=note).font = muted
    ws.cell(row=row - len(figures) + 1, column=2).fill = PatternFill("solid", fgColor=BAND_SOFT[r.band])
    row += 2
    ws.cell(row=row, column=1, value=_band_rule(r.required_days)).font = muted
    widths(ws, [28, 30, 58])

    mo = wb.create_sheet("By month")
    header(mo, 1, ["Month", "Working days", "Office days", "Required", "WFO status", "WFH", "Leave", "Absent",
                   "No timesheet", "Attendance %", "Average office hours"])
    for i, m in enumerate(r.months, start=2):
        values = [m.label + (" (part)" if m.partial else ""), m.working_days, m.office_days, m.required_days,
                  m.status, m.wfh_days, m.leave_days, m.absent_days, m.no_timesheet_days,
                  None if m.attendance_percentage is None else round(m.attendance_percentage, 1),
                  None if m.average_minutes is None else round(m.average_minutes / 60, 2)]
        for j, v in enumerate(values, start=1):
            mo.cell(row=i, column=j, value=v)
        mo.cell(row=i, column=5).fill = PatternFill("solid", fgColor=m.soft)
    mo.freeze_panes = "A2"
    widths(mo, [16, 14, 12, 10, 14, 8, 8, 8, 14, 14, 22])

    da = wb.create_sheet("Daily")
    header(da, 1, ["Date", "Day", "Status", "In", "Out", "Office hours", "Required hours", "Notes"])
    for i, rec in enumerate(r.records, start=2):
        notes = "; ".join(x for x in [rec.holiday_name or "", *[e.replace("_", " ").capitalize() for e in rec.exceptions
                                                                 if e != "ABSENT"]] if x)
        values = [rec.date, rec.date.strftime("%a"), STATUS_LABEL[rec.status], _clock(rec.first_in), _clock(rec.last_out),
                  round(rec.net_minutes / 60, 2) if rec.hours_known else None,
                  round(rec.required_minutes / 60, 2) if rec.required_minutes else None, _safe(notes)]
        for j, v in enumerate(values, start=1):
            da.cell(row=i, column=j, value=v)
        da.cell(row=i, column=1).number_format = "DD MMM YYYY"
        da.cell(row=i, column=3).font = Font(color=STATUS_COLOR[rec.status], bold=rec.status in (DayStatus.PRESENT, DayStatus.ABSENT))
    da.freeze_panes = "A2"
    da.auto_filter.ref = f"A1:H{max(1, len(r.records) + 1)}"
    widths(da, [14, 7, 18, 8, 8, 13, 15, 40])

    buf = io.BytesIO()
    wb.save(buf)
    return buf.getvalue()


# ------------------------------------------------------------------ PDF

def to_pdf(r: Report) -> bytes:
    from reportlab.graphics.shapes import Drawing, Line, Rect, String
    from reportlab.lib import colors
    from reportlab.lib.enums import TA_RIGHT
    from reportlab.lib.pagesizes import A4
    from reportlab.lib.styles import ParagraphStyle
    from reportlab.lib.units import mm
    from reportlab.lib.utils import ImageReader
    from reportlab.pdfgen import canvas as pdfcanvas
    from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle
    from xml.sax.saxutils import escape

    b = r.brand or brand()
    brand_c, accent_c = colors.HexColor("#" + b.color), colors.HexColor("#" + b.accent)
    ink, muted, line, soft = (colors.HexColor(x) for x in ("#0F172A", "#64748B", "#E2E8F0", "#F8FAFC"))
    page_w, page_h = A4
    margin, band_h = 16 * mm, 24 * mm
    title = "Attendance report"
    footer_left = " · ".join(x for x in [b.company, title, r.employee.full_name] if x)
    generated = r.generated_at.astimezone(office_tz()).strftime("%d %b %Y, %H:%M")

    class Numbered(pdfcanvas.Canvas):
        """Draws the header band and the footer on every page once the page count is known."""

        def __init__(self, *args, **kwargs):
            super().__init__(*args, **kwargs)
            self._pages: list[dict] = []

        def showPage(self):
            self._pages.append(dict(self.__dict__))
            self._startPage()

        def save(self):
            total = len(self._pages)
            for i, state in enumerate(self._pages, start=1):
                self.__dict__.update(state)
                self._frame(i, total)
                super().showPage()
            super().save()

        def _frame(self, page: int, total: int):
            self.saveState()
            self.setFillColor(brand_c)
            self.rect(0, page_h - band_h, page_w, band_h, stroke=0, fill=1)
            self.setFillColor(accent_c)
            self.rect(0, page_h - band_h - 1.2 * mm, page_w, 1.2 * mm, stroke=0, fill=1)
            x = margin
            if b.logo is not None:
                try:
                    img = ImageReader(str(b.logo))
                    iw, ih = img.getSize()
                    h = 8 * mm
                    w = min(h * iw / ih, 60 * mm)
                    self.drawImage(img, x, page_h - band_h / 2 - h / 2, width=w, height=h, mask="auto",
                                   preserveAspectRatio=True, anchor="w")
                except Exception:  # noqa: BLE001 - a broken logo must not break the report
                    logger.warning("the report logo could not be drawn: %s", b.logo)
                    self._company(x)
            else:
                self._company(x)
            self.setFillColor(colors.white)
            self.setFont("Helvetica-Bold", 13)
            self.drawRightString(page_w - margin, page_h - band_h / 2 + 1, title)
            self.setFont("Helvetica", 8.5)
            self.drawRightString(page_w - margin, page_h - band_h / 2 - 10, _period(r.start, r.end))
            self.setStrokeColor(line)
            self.line(margin, 13 * mm, page_w - margin, 13 * mm)
            self.setFillColor(muted)
            self.setFont("Helvetica", 7.5)
            self.drawString(margin, 9 * mm, footer_left[:110])
            self.drawRightString(page_w - margin, 9 * mm, f"Generated {generated} · Page {page} of {total}")
            self.restoreState()

        def _company(self, x: float):
            if b.company:
                self.setFillColor(colors.white)
                self.setFont("Helvetica-Bold", 16)
                self.drawString(x, page_h - band_h / 2 - 5, b.company)

    def style(name: str, **kw) -> ParagraphStyle:
        return ParagraphStyle(name, fontName=kw.pop("fontName", "Helvetica"), fontSize=kw.pop("fontSize", 9),
                              leading=kw.pop("leading", 12), textColor=kw.pop("textColor", ink), **kw)

    h2 = style("h2", fontName="Helvetica-Bold", fontSize=11, leading=14, textColor=brand_c, spaceBefore=10, spaceAfter=5)
    small = style("small", fontSize=7.5, leading=10, textColor=muted)
    label_s = style("label", fontSize=7.5, leading=10, textColor=muted)
    value_s = style("value", fontName="Helvetica-Bold", fontSize=10, leading=13)
    cell = style("cell", fontSize=8, leading=10)
    cell_r = style("cell_r", fontSize=8, leading=10, alignment=TA_RIGHT)

    def p(text, st=cell) -> Paragraph:
        return Paragraph(escape(str(text)), st)

    def dot(hex_color: str, text: str, bold: bool = False) -> Paragraph:
        body = f"<b>{escape(text)}</b>" if bold else escape(text)
        # ZapfDingbats "l" is a filled circle (the standard PDF fonts have no bullet of that size).
        return Paragraph(f'<font name="ZapfDingbats" size="6" color="#{hex_color}">l</font>&nbsp;&nbsp;{body}', cell)

    width = page_w - 2 * margin
    story = []

    # Who and when
    facts = _facts(r)
    rows = [[[p(k, label_s), p(v, value_s)] for k, v in facts[i:i + 3]] for i in (0, 3)]
    info = Table(rows, colWidths=[width / 3] * 3)
    info.setStyle(TableStyle([("VALIGN", (0, 0), (-1, -1), "TOP"), ("LEFTPADDING", (0, 0), (-1, -1), 0),
                              ("BOTTOMPADDING", (0, 0), (-1, -1), 6)]))
    story += [info, Spacer(1, 4)]

    # Key figures
    w, s = r.wfo, r.summary
    band_hex = BAND_COLOR[r.band]
    big = style("big", fontName="Helvetica-Bold", fontSize=18, leading=21)

    words = style("words", fontName="Helvetica-Bold", fontSize=12, leading=21)

    def tile(caption: str, value: str, note: str, color: str | None = None):
        v = f'<font color="#{color}">{escape(value)}</font>' if color else escape(value)
        return [p(caption, label_s), Paragraph(v, big if value[:1].isdigit() else words), p(note, small)]

    tiles = Table([[
        tile("Office (WFO) days", f"{r.office_days} of {r.required_days}", r.target_note, band_hex),
        tile("Attendance rate", _pct(s.attendance_percentage), f"{s.present_days} of {s.counted_days} working days"),
        tile("Average office hours", _hm(s.average_net_minutes) if s.hours_days else "Not recorded",
             f"on {s.hours_days} day(s) with hours" if s.hours_days else "not in the timesheets"),
        tile("Absent days", str(s.absent_days), f"WFH {s.wfh_days} · Leave {s.leave_days}"),
    ]], colWidths=[width / 4] * 4)
    tiles.setStyle(TableStyle([
        ("BOX", (0, 0), (-1, -1), 0.6, line), ("INNERGRID", (0, 0), (-1, -1), 0.6, line),
        ("BACKGROUND", (0, 0), (-1, -1), soft), ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LINEABOVE", (0, 0), (0, 0), 2, colors.HexColor("#" + band_hex)),
        ("LINEABOVE", (1, 0), (-1, 0), 2, accent_c),
        ("TOPPADDING", (0, 0), (-1, -1), 7), ("BOTTOMPADDING", (0, 0), (-1, -1), 7),
        ("LEFTPADDING", (0, 0), (-1, -1), 8),
    ]))
    story += [tiles, Spacer(1, 3), p(_band_rule(r.required_days), small)]

    # Office days by month: bars in the status colour, a tick at the requirement
    if r.months:
        story.append(Paragraph("Office days by month", h2))
        chart_h, base, top_pad = 46 * mm, 12 * mm, 6 * mm
        d = Drawing(width, chart_h)
        peak = max([m.office_days for m in r.months] + [m.required_days for m in r.months] + [1])
        scale = (chart_h - base - top_pad) / peak
        slot = min(width / len(r.months), 42 * mm)
        bar_w = min(slot * 0.5, 16 * mm)
        left = (width - slot * len(r.months)) / 2
        d.add(Line(0, base, width, base, strokeColor=line, strokeWidth=0.8))
        for i, m in enumerate(r.months):
            cx = left + slot * i + slot / 2
            h = m.office_days * scale
            d.add(String(cx, base - 10, m.label, textAnchor="middle", fontName="Helvetica", fontSize=8, fillColor=ink))
            if not m.covered:
                d.add(String(cx, base + 4, "No timesheet", textAnchor="middle", fontName="Helvetica", fontSize=7,
                             fillColor=muted))
                continue
            if h > 0:
                d.add(Rect(cx - bar_w / 2, base, bar_w, h, fillColor=colors.HexColor("#" + m.color),
                           strokeColor=None, rx=1.5, ry=1.5))
            req_y = base + m.required_days * scale
            d.add(Line(cx - bar_w / 2 - 5, req_y, cx + bar_w / 2 + 5, req_y, strokeColor=ink, strokeWidth=1,
                       strokeDashArray=[3, 2]))
            # The number sits above the bar, or above the requirement line when that is just over the bar.
            label_y = req_y + 3 if 0 <= req_y - (base + h) < 12 else base + h + 3
            d.add(String(cx, label_y, str(m.office_days), textAnchor="middle", fontName="Helvetica-Bold",
                         fontSize=9, fillColor=ink))
            d.add(String(cx, base - 19, f"{m.status} · need {m.required_days}", textAnchor="middle",
                         fontName="Helvetica", fontSize=6.5, fillColor=muted))
        story += [d, Spacer(1, 6), p("Bar: office days in the month (green on target, amber close, red below). "
                                     "Dashed line: days required.", small)]

        story.append(Paragraph("Summary by month", h2))
        head = ["Month", "Working days", "Office days", "Required", "WFO status", "WFH", "Leave", "Absent",
                "Attendance", "Avg hours"]
        data = [[p(x, style("th", fontName="Helvetica-Bold", fontSize=7.5, leading=9, textColor=colors.white)) for x in head]]
        for m in r.months:
            data.append([p(m.label + (" (part)" if m.partial else "")), p(m.working_days, cell_r), p(m.office_days, cell_r),
                         p(m.required_days, cell_r), dot(m.color, m.status), p(m.wfh_days, cell_r),
                         p(m.leave_days, cell_r), p(m.absent_days, cell_r), p(_pct(m.attendance_percentage), cell_r),
                         p(_hm(m.average_minutes), cell_r)])
        shares = [0.13, 0.1, 0.09, 0.09, 0.15, 0.07, 0.07, 0.08, 0.11, 0.11]
        mt = Table(data, colWidths=[width * x for x in shares], repeatRows=1)
        mt.setStyle(TableStyle([
            ("BACKGROUND", (0, 0), (-1, 0), brand_c), ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, soft]),
            ("LINEBELOW", (0, 0), (-1, -1), 0.4, line), ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
            ("TOPPADDING", (0, 0), (-1, -1), 4), ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
        ]))
        story.append(mt)

    # Every day
    story.append(Paragraph("Daily attendance", h2))
    head = ["Date", "Day", "Status", "In", "Out", "Office hours", "Notes"]
    data = [[p(x, style("th2", fontName="Helvetica-Bold", fontSize=7.5, leading=9, textColor=colors.white)) for x in head]]
    quiet = (DayStatus.WEEKEND, DayStatus.NOT_SCHEDULED, DayStatus.NO_TIMESHEET)
    styles = [
        ("BACKGROUND", (0, 0), (-1, 0), brand_c), ("LINEBELOW", (0, 0), (-1, -1), 0.3, line),
        ("VALIGN", (0, 0), (-1, -1), "MIDDLE"), ("TOPPADDING", (0, 0), (-1, -1), 2.5),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 2.5),
    ]
    for i, rec in enumerate(r.records, start=1):
        notes = "; ".join(x for x in [rec.holiday_name or "", *[e.replace("_", " ").capitalize() for e in rec.exceptions
                                                                 if e != "ABSENT"]] if x)
        data.append([p(rec.date.strftime("%d %b %Y")), p(rec.date.strftime("%a")),
                     dot(STATUS_COLOR[rec.status], STATUS_LABEL[rec.status], bold=rec.status == DayStatus.ABSENT),
                     p(_clock(rec.first_in)), p(_clock(rec.last_out)),
                     p(_hm(rec.net_minutes) if rec.hours_known else "", cell_r), p(notes, small)])
        if rec.status in quiet:
            styles.append(("BACKGROUND", (0, i), (-1, i), soft))
    if len(data) == 1:
        data.append([p("No days in this period.", small)] + [""] * 6)
    shares = [0.16, 0.08, 0.24, 0.09, 0.09, 0.13, 0.21]
    dt = Table(data, colWidths=[width * x for x in shares], repeatRows=1)
    dt.setStyle(TableStyle(styles))
    story.append(dt)

    buf = io.BytesIO()
    doc = SimpleDocTemplate(buf, pagesize=A4, leftMargin=margin, rightMargin=margin, topMargin=band_h + 9 * mm,
                            bottomMargin=18 * mm, title=f"{title} – {r.employee.full_name}",
                            author=b.company or "Attendance", subject=_period(r.start, r.end))
    doc.build(story, canvasmaker=Numbered)
    return buf.getvalue()
