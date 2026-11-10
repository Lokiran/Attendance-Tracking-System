# Attendance rules

All figures on every dashboard, table and CSV export come from one implementation:
`backend/app/services/attendance_rules.py` (pure functions, unit-tested in
`backend/app/tests/test_attendance_rules.py`). Nothing is computed in the browser.

Attendance comes from the **timesheets the HR admin imports** (Excel, CSV or PDF; see
[wfo.md](wfo.md#uploads-phase-5) for reading files). There is no check-in, check-out or correction request
in the app. The tables of those retired features are kept in the database, unused.

## Timesheet days

* Each committed upload writes what it says about an employee-day into `attendance_daily` (work mode,
  hours, in / out times, badge rows). An Admin override of a single day is stored there too.
* The work mode of a day is decided with the WFO priority rules (`wfo_rules.classify_day`): override >
  the timesheet's work-mode column > badge rows > office Wi-Fi; hours without a work mode count as WFH.
  A file with only an employee and a date column is a presence list: every listed day is OFFICE.
* Office hours of a day: the timesheet's hours column, or the span of its in / out times (an out time
  earlier than the in time means the next day). A day listed without either has **no recorded hours**.
* Timestamps are stored in UTC (`timestamptz`) and shown in the office timezone (`OFFICE_TIMEZONE`, default
  `Asia/Kolkata`).

## Coverage

A committed timesheet **covers** the 1st of the month of its first date up to the end of the month of its
last date, but not beyond the day before it was committed (`attendance_uploads.period_start / period_end`),
**for the people listed in it** (its valid rows). A September sheet imported in November covers all of
September; a November sheet imported on the 10th covers 1–9 November. A person's coverage is the union of
the timesheets that list them, so a partial file (one team, a one-person fix) never makes anyone else
absent, and a person missing from every sheet of a month shows "No timesheet" for that month.

## Schedules

Resolution for a given day: **employee schedule > department schedule > organisation default**.
Within a tier, the assignment effective on that date wins. Schedules define start/end time
(end < start means an overnight shift), required minutes, and ISO working days
(1 = Mon … 7 = Sun). Days before an employee's joining date are `NOT_SCHEDULED`, even with data.

## Day status (single source of truth)

| Timesheet says | Status | Expected (in the denominator) |
|---|---|---|
| OFFICE | `PRESENT` (or `ABSENT` with `BELOW_MINIMUM` when recorded hours are under the policy minimum) | if a scheduled, non-holiday day |
| WFH | `WFH`: a working day, but not office attendance | if a scheduled, non-holiday day |
| LEAVE | `LEAVE` | no |
| HOLIDAY / WEEKOFF | `HOLIDAY` / `WEEKEND` | no |
| nothing, scheduled non-holiday day, covered for this person, before today | `ABSENT` (exception `ABSENT`) | yes |
| nothing, scheduled non-holiday day, not covered for this person (or today) | `NO_TIMESHEET` | no |
| nothing, holiday / weekly off | `HOLIDAY` / `WEEKEND` | no |

Future dates are never evaluated. Working on an off day or a holiday is an extra `PRESENT` day outside
the denominator.

## Metrics

* **Attendance %** = present expected days / expected days (office attendance; WFH days lower it).
  No expected days → no percentage (never a division by zero).
* **Shift hours** (`required_minutes`) = the shift's required minutes summed over expected days.
* **Office hours** (`net_minutes`) are summed only from days whose hours the timesheet recorded
  (`hours_days`). They are compared with the shift minutes of the **same** days
  (`hours_required_minutes`, `variance_minutes`), and the average is per such day. A presence list without
  hours therefore shows hours as "not recorded", never as hours short.
* Exceptions: `ABSENT`; `LATE_ARRIVAL` when the in time is later than shift start + grace;
  `SHORT_HOURS` when a past present day has recorded hours under the shift's required minutes;
  `BELOW_MINIMUM` (see above).

## Team snapshots

For one date, every person in scope falls in exactly one of: present, WFH, absent, leave, no timesheet,
off (holiday / weekend / not scheduled). The counts always add up to the headcount.

## Policy settings that apply

* `min_present_minutes`: recorded office minutes below this make an office day absent (0: off).
* `late_grace_minutes`: minutes after shift start before an in time counts as late.

The settings for check-in sessions and corrections (maximum session length, correction window, counting
missing check-outs) are kept in the database but no longer used.
