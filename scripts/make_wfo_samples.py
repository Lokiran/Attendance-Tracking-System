"""Regenerate the WFO sample files in samples/ (deterministic). Employee codes match the demo seed.

    backend\\.venv\\Scripts\\python.exe scripts\\make_wfo_samples.py
"""

import csv
import random
from datetime import date, timedelta
from pathlib import Path

from openpyxl import Workbook

OUT = Path(__file__).resolve().parents[1] / "samples"
EMPLOYEES = ["EMP001", "EMP002", "EMP003", "EMP004", "EMP005", "EMP006", "EMP007", "EMP009", "EMP010"]
START, END = date(2026, 10, 5), date(2026, 10, 30)  # four full weeks, Monday to Friday
OFFICE_WORDS = ["WFO", "Office", "On-site", "WFO", "Office"]
WFH_WORDS = ["WFH", "Remote", "WFH", "Work from home"]


def weekdays():
    d = START
    while d <= END:
        if d.weekday() < 5:
            yield d
        d += timedelta(days=1)


def attendance_rows(rng: random.Random):
    """Mostly 3 office days a week; EMP005 and EMP010 fall short; one leave day; some short office days."""
    rows = []
    for code in EMPLOYEES:
        office_days = {0, 1, 2} if code not in ("EMP005", "EMP010") else {1}
        for d in weekdays():
            if code == "EMP006" and d == date(2026, 10, 14):
                rows.append([code, d.strftime("%d/%m/%Y"), "Leave", "", "HRMS"])
                continue
            if d.weekday() in office_days:
                hours = 3.5 if (code == "EMP004" and d.weekday() == 0) else round(rng.uniform(8.0, 9.3), 2)
                rows.append([code, d.strftime("%d/%m/%Y"), rng.choice(OFFICE_WORDS), hours, "HRMS"])
            else:
                rows.append([code, d.strftime("%d/%m/%Y"), rng.choice(WFH_WORDS), round(rng.uniform(7.5, 8.8), 2), "HRMS"])
    # Badge rows for days the attendance system marked WFH: a conflict, resolved as OFFICE.
    rows.append(["EMP003", "2026-10-08", "", "", "Badge", "09:41", "18:22"])
    rows.append(["EMP003", "2026-10-15", "", "", "Badge", "10:02", "17:55"])
    return rows


def main() -> None:
    OUT.mkdir(exist_ok=True)
    rng = random.Random(42)
    with (OUT / "wfo_attendance_sample.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["Employee Code", "Date", "Work Mode", "Hours", "Source", "In Time", "Out Time"])
        for r in attendance_rows(rng):
            w.writerow(r + [""] * (7 - len(r)))

    wb = Workbook()
    ws = wb.active
    ws.title = "Swipes"
    ws.append(["Emp Code", "Date", "Swipe In", "Swipe Out", "Source"])
    for code in ("EMP001", "EMP002", "EMP005", "EMP010"):
        for d in weekdays():
            # EMP005 / EMP010 badged in Monday to Thursday: office days the HR file called WFH (conflicts).
            if d.weekday() in (0, 1, 2) or (code in ("EMP005", "EMP010") and d.weekday() == 3):
                t_in = f"09:{rng.randint(5, 55):02d}"
                t_out = f"{rng.randint(17, 19)}:{rng.randint(0, 59):02d}"
                ws.append([code, d, t_in, t_out, "Access card"])
    for row in ws.iter_rows(min_row=2, min_col=2, max_col=2):
        row[0].number_format = "dd-mm-yyyy"
    wb.save(OUT / "wfo_badge_sample.xlsx")
    print(f"wrote {OUT / 'wfo_attendance_sample.csv'} and {OUT / 'wfo_badge_sample.xlsx'}")


if __name__ == "__main__":
    main()
