# Sample WFO files

Import them at **Admin → Import attendance**. Employee codes match the demo seed (`python -m app.cli seed`).
Regenerate them with `backend\.venv\Scripts\python.exe scripts\make_wfo_samples.py`.

| File | What it shows |
|---|---|
| `wfo_attendance_sample.csv` | An HR export for 5–30 October 2026: 9 people, mixed `WFO` / `Office` / `On-site` / `WFH` / `Remote` / `Work from home` values, hours, one `Leave` day (EMP006, 14 Oct), 3.5 h office Mondays below the 4 h minimum (EMP004), two people who fall short of 3 office days a week (EMP005, EMP010), and two badge rows that conflict with a WFH entry (EMP003, 8 and 15 Oct) |
| `wfo_badge_sample.xlsx` | A badge / swipe export (sheet "Swipes": Emp Code, Date, Swipe In, Swipe Out, Source = Access card) for October. EMP005 and EMP010 badged in Monday to Thursday, so after importing both files their WFH days become office days, each listed as a conflict |

Both files are also used by the backend tests (`backend/app/tests/test_wfo_upload.py`).
