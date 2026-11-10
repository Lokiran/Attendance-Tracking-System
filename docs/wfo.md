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

`attendance_sessions.work_location` stores the Office / WFH choice an employee makes at check-in.

Reads always recompute from the evidence, so they are never stale. The stored result is refreshed in the
same transaction as every write: check-in, check-out, approved correction, upload commit and override. Use
`python -m app.cli wfo-refresh --from YYYY-MM-DD --to YYYY-MM-DD` after changing schedules or holidays.

## Classification (Phase 1)

Highest priority first:

0. **Admin override** (`MANUAL_OVERRIDE`): always wins. All of the day's worked time counts in that mode.
1. **Uploaded work-mode column** (`UPLOAD_COLUMN`). Accepted text is in `backend/config/wfo_mapping.json`:
   WFO, Office, On-site → OFFICE; WFH, Remote → WFH; also leave / holiday / week-off words.
2. **Badge / swipe / office-access rows** of an upload (`BADGE`) → OFFICE.
3. **Office Wi-Fi** (`WIFI`) → OFFICE.
4. **Office / WFH chosen at check-in** (`CHECK_IN`), lower priority than the systems above.
5. Worked hours with no office signal → **WFH** (`INFERRED`).
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
| POST | `/api/attendance/check-in` `{"work_location": "OFFICE" \| "WFH"}` | everyone | Check in with a location (optional) |

The existing `GET /api/attendance/employees/{id}/summary` now also returns a `wfo` block with `wfo_days`,
`wfh_days`, `wfo_percentage`, `weekly_wfo_breakdown`, `compliance` and `shortfall_days`.

## Uploads (Phase 5)

`POST /api/admin/attendance-uploads` (multipart `file`, admin) returns a **preview**; nothing changes yet.
Then `POST …/{id}/commit {"overwrite_overrides": false}` or `POST …/{id}/discard`.

* CSV (UTF-8; `,` `;` or tab) or Excel `.xlsx` (first sheet); at most 5 MB and 20,000 rows.
* Required columns: employee (code or email) and date. Then at least one of: work mode, hours, in / out time,
  source. Header aliases are configured in `config/wfo_mapping.json`.
* Dates are day-first: `31/03/2026`, `31-03-2026`, `2026-03-31`, `31-Mar-2026`, or Excel dates.
* Hours: `7.5` or `7:30`, or derived from in / out times. An out time earlier than the in time means the next day.
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
* An imported OFFICE day **without hours** is OFFICE but not a WFO day, unless another source supplies office
  hours. Previews count these as "below the minimum".
* Check-ins made **without a location** (older data, correction-created sessions) count as worked hours with
  no office signal (WFH). A correction keeps the location of the session it fixes.
* Grouping in the admin view is by **direct** manager, so each person appears once.
* The 403-not-404 rule applies to the new WFO endpoints only, as the WFO spec asked. Older endpoints keep
  their 404 for out-of-scope ids.
