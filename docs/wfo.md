# WFO (work from office) tracking

Every employee-day gets a **work mode**: `OFFICE` (WFO), `WFH`, `LEAVE`, `HOLIDAY` or `WEEKOFF`. Monthly
policy figures (WFO days, WFO %, weekly progress, compliance, shortfall) are computed from those days.

* Rules: `backend/app/services/wfo_rules.py` (pure functions, unit-tested)
* Data: `backend/app/services/wfo.py`
* Uploads: `backend/app/services/wfo_upload.py`
* Excel report: `backend/app/services/wfo_export.py`

## Where the data lives

`attendance_daily` (one row per employee and date, unique) is the attendance table:

| Column | Meaning |
|---|---|
| `work_mode`, `wfo_source`, `conflicts` | The result: the mode, the signal that decided it, and the disagreements |
| `upload_work_mode`, `upload_minutes` | Evidence from an imported file's work-mode and hours columns |
| `badge_minutes` | Not NULL: badge / swipe rows exist for the day |
| `wifi_minutes` | Office Wi-Fi minutes; automatic attendance Phase 2 fills this |
| `override_work_mode`, `override_note`, `overridden_by_id`, `overridden_at` | Admin override; the audit log keeps old and new values |
| `office_minutes`, `wfh_minutes`, `status` | Minutes by mode, and Present / Half-day / Absent / Leave / Holiday / Weekend |

Check-in no longer exists (attendance comes from imported timesheets); `attendance_sessions` and its
`work_location` column are kept, unused, and are not a source any more.

Reads always recompute from the evidence, so they are never stale. The stored result is refreshed in the
same transaction as every write: upload commit and override. Use
`python -m app.cli wfo-refresh --from YYYY-MM-DD --to YYYY-MM-DD` after changing schedules or holidays.

## Classification (Phase 1)

Highest priority first:

0. **Admin override** (`MANUAL_OVERRIDE`): always wins. All of the day's worked time counts in that mode.
1. **Uploaded work-mode column** (`UPLOAD_COLUMN`). Accepted text is in `backend/config/wfo_mapping.json`:
   WFO, Office, On-site → OFFICE; WFH, Remote → WFH; also leave / holiday / week-off words.
2. **Badge / swipe / office-access rows** of an upload (`BADGE`) → OFFICE.
3. **Office Wi-Fi** (`WIFI`) → OFFICE.
4. (Retired) Office / WFH chosen at check-in (`CHECK_IN`): the value may still appear on old stored days.
5. Hours in a timesheet row without a work mode → **WFH** (`INFERRED`).
6. No work: HOLIDAY or WEEKOFF from the calendar (`CALENDAR`), otherwise no mode (absent).

**Conflicts:** an office signal together with a WFH (or leave / holiday / week-off) statement makes the day
OFFICE, and each disagreement is written to `conflicts`. Upload previews and the calendar show them.

Office minutes are the **longest** office evidence, never the sum: several sources describe the same time.

## Policy (Phase 2)

Settings live in the settings row (`attendance_policy`) and are editable at `PUT /api/admin/wfo/policy`:

| Setting | Default |
|---|---|
| `min_wfo_days_per_week` | 3 |
| `min_wfo_days_per_month` | 12 |
| `min_office_minutes_for_wfo_day` | 240 (4 h) |

* **WFO day:** work mode OFFICE **and** at least the minimum office time.
* **Working days** (the WFO % denominator): scheduled working days that are not holidays and not
  LEAVE / HOLIDAY / WEEKOFF, plus days actually worked on an off day. LEAVE, HOLIDAY and WEEKOFF are excluded.
* `wfo_percentage = wfo_days / working_days`.
* **Requirements are prorated**, rounding half up. Monthly: `12 × scheduled working days ÷ scheduled
  non-holiday days of the month`. Weekly: `3 × scheduled working days that week ÷ scheduled days of the full
  week`. So leave, holidays and the current partial month lower the requirement. Working on a holiday or a
  weekend raises the percentage but never the requirement.
* **Weeks are judged on all seven days** (Monday to Sunday), also the days in the neighbouring month. A week
  that straddles two months gets the same verdict in both.
* **COMPLIANT** when the monthly requirement and every completed week are met.
  `shortfall_days = max(monthly shortfall, sum of weekly shortfalls)`.
* **Only completed days count.** Today is shown, and counted from tomorrow. The current week is
  `IN_PROGRESS` and weeks not yet started are `UPCOMING`; neither can make anyone non-compliant.

## API (Phase 3)

Role scope is enforced in the backend queries. Out-of-scope employees get **403**.

| Method | Path | Who | What |
|---|---|---|---|
| GET | `/api/me/wfo?month=YYYY-MM` | everyone | My month: summary, weekly breakdown, days |
| GET | `/api/team/wfo?month=&employee_id=` | manager, admin | Me plus my whole reporting line (admin: everyone) |
| GET | `/api/team/wfo/employees/{id}?month=` | manager, admin | One team member's month |
| GET | `/api/org/wfo?month=&manager_id=&department_id=` | admin | Everyone, grouped by direct manager |
| GET | `/api/org/wfo/export.xlsx?month=&manager_id=&department_id=` | admin | Excel: Summary, Daily, Policy sheets |
| PUT / DELETE | `/api/admin/attendance/{employee_id}/{date}/work-mode` | admin | Set or remove an override (audited) |
| GET / PUT | `/api/admin/wfo/policy` | admin | WFO policy (audited) |

The existing `GET /api/attendance/employees/{id}/summary` now also returns a `wfo` block with `wfo_days`,
`wfh_days`, `wfo_percentage`, `weekly_wfo_breakdown`, `compliance` and `shortfall_days`.

## Uploads (Phase 5)

`POST /api/admin/attendance-uploads` (multipart `file`, admin; optional query `sheet` and
`presence_mode=OFFICE|WFH`) returns a **preview**; nothing changes yet. Then
`POST …/{id}/commit {"overwrite_overrides": false}` or `POST …/{id}/discard`.

* CSV / TSV / TXT (UTF-8; `,` `;` tab or `|`), Excel `.xlsx` / `.xls`, or **PDF**; at most 5 MB and 20,000
  rows. A PDF is read from its text: words are grouped into rows by height and into columns by the header's
  positions, so a sheet exported or printed to PDF reads like the sheet itself (text outside the header's
  columns, such as a pivot table, is dropped; a header repeated on later pages is skipped). Scanned PDFs
  (images) are refused with a message to upload the Excel file. `.ods` must be saved as Excel or CSV.
* Committing sets the timesheet's **period** (`period_start` / `period_end`, shown after the import): from the
  1st of the month of its first date to the end of the month of its last date, not beyond the day before the
  commit. It covers that period for the people listed in it: their scheduled days in the period that are not
  in any timesheet count as absent; people not in the file are not affected.
* **Which sheet**: the `sheet` asked for; otherwise the first sheet named like a month ("September 2026",
  "Sep-26", "2026-09") that has an employee and a date column; otherwise the first sheet that has them. The
  preview lists every sheet (usable or not, with row counts) and the page can read another one.
* The header row can be any of the first 20 rows. Rows with neither an employee nor a date are skipped, so a
  pivot table or notes beside the data do no harm.
* Required columns: employee (code or email) and date. Header aliases are configured in
  `config/wfo_mapping.json`. When there is both an ID column ("Employee ID", "Code") and a plain "Employee"
  column, the plain one is the **name**: it is only compared with the app (differences are listed, rows still
  match by ID).
* **Presence list**: with no work mode and no source column, every listed day is an OFFICE day (or WFH when
  `presence_mode=WFH`). Hours / in / out columns are still read when present.
* Dates: text is day-first (`31/03/2026`, `31-03-2026`, `2026-03-31`, `31-Mar-2026`). A date that could be read
  both ways takes the reading inside the months the rest of the file covers: `09-01-2026` in a September file
  is 1 September. The same applies to **Excel dates stored with day and month swapped** (Excel on a US-format
  PC turns a typed `01/09/2026` into 9 January). The preview says how many dates were read this way.
* Employee IDs match case-insensitively, and runs of spaces count as one (`Intern - 10001`).
* **Unknown employee IDs** are listed (with the file's name, row count and first date).
  `POST …/{id}/create-employees {"employee_refs": null}` adds them as employees: the ID and the most frequent
  name from the file, a placeholder email `<id>@imported.example.com`, an unusable random password (sign-in
  needs a password reset or SSO), no manager / department, joined on the 1st of the month of their first date
  in the file. Each creation is audited (`USER_CREATED` with `via_upload`). The upload's rows are re-checked
  without uploading the file again. Email addresses and IDs that do not fit the employee ID rule cannot be added
  this way.
* Hours: `7.5`, `7:30` or `07:30:00`, or derived from in / out times. An out time earlier than the in time means
  the next day.
* Rows whose source is a badge value (Badge, Swipe, Access card …) are badge evidence.
* Future dates are accepted only for LEAVE / HOLIDAY / WEEKOFF. Dates before an employee joined are refused.
* **Preview shows** OFFICE / WFH counts, conflicts, rows with problems (with row numbers), new days, and days
  protected by an override.
* **Commit upserts** on `(employee_id, date)`. A file replaces only the evidence it contains (work mode / hours,
  badge). Days with an Admin override are **skipped** unless `overwrite_overrides` is chosen; the override is
  then cleared and audited. The commit applies the stored preview rows, so it imports exactly what was previewed.

Sample files: `samples/` (see `samples/README.md`).

## Assumptions

* **Leave** exists only from imported files or Admin overrides. Leave requests stay removed (decision of
  2026-11-05).
* **Manager scope** is the whole reporting line plus the manager, consistent with the rest of the app.
* An OFFICE day **without hours from any source** (a presence list, a work-mode-only row, a badge row without
  times) counts as a WFO day and as present: the file says the person was in the office, and there is nothing
  to measure. When hours are known, the minimum office hours apply.
* Employee IDs may contain inner spaces and `.` (`Intern - 10001`, `Guest 2`), as attendance files use them.
* Attendance (Present / Absent / WFH / No timesheet, hours, attendance %) is built from the same timesheet
  days; the rules are in [ATTENDANCE_RULES.md](ATTENDANCE_RULES.md), including which dates a committed
  timesheet covers.
* Grouping in the admin view is by **direct** manager, so each person appears once.
* The 403-not-404 rule applies to the new WFO endpoints only, as the WFO spec asked. Older endpoints keep
  their 404 for out-of-scope ids.
