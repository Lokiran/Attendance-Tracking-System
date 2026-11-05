# Attendance rules

All figures on every dashboard, table and CSV export come from one implementation:
`backend/app/services/attendance_rules.py` (pure functions, unit-tested in
`backend/app/tests/test_attendance_rules.py`). Nothing is computed in the browser.

## Time and workdays

* Timestamps are stored in UTC (`timestamptz`) and shown in the office timezone
  (`OFFICE_TIMEZONE`, default `Asia/Kolkata`).
* Check-in and check-out times are taken from the **server clock**. Browser-supplied times are
  never used for check-in/out.
* A session belongs to the office-local **date of its check-in**. An overnight session
  (e.g. 21:00 → 06:00) counts fully toward the day it started.
* Day boundaries are local midnights computed with `zoneinfo`, so DST transitions and month
  ends are handled correctly (no UTC off-by-one).

## Sessions

| Status | Meaning | Counts toward hours? |
|---|---|---|
| `OPEN` | Checked in, not yet out. *Active* while younger than `max_session_minutes`. | No (until checked out) |
| `COMPLETED` | Checked out; `net_minutes` is final | **Yes** |
| `MISSING_CHECKOUT` | Never checked out (or exceeded max length) | No — *unresolved* |
| `VOIDED` | Superseded by an approved correction; kept for history | No |

* `net_minutes = floor(minutes between check-in and check-out) − break_minutes`. The duration
  must be positive and the break can't exceed it. All arithmetic is in integer minutes.
* Only one `OPEN` session per employee (partial unique index + row lock). Sessions can't
  overlap: an `OPEN` session occupies `[check-in, ∞)` until it is checked out, so corrections
  can't be placed inside a running session. Check-out re-checks for overlap. On PostgreSQL the
  exclusion constraint `ex_attendance_no_overlap` is the final guard, and any violation is
  reported as HTTP 409 with the whole transaction rolled back.
* If someone forgets to check out, the next check-in marks the old session `MISSING_CHECKOUT`.
  The original row is preserved, and the employee can request a correction.
* A check-out attempted after `max_session_minutes` (default 16 h) is refused, and the session
  is flagged `MISSING_CHECKOUT`.

## Schedules

Resolution for a given day: **employee schedule > department schedule > organisation default**.
Within a tier, the assignment effective on that date wins. Schedules define start/end time
(end < start means an overnight shift), required net minutes, and ISO working days
(1 = Mon … 7 = Sun). Days before an employee's joining date are `NOT_SCHEDULED`.

## Day status (single source of truth)

Evaluated for each date up to today (future dates are never evaluated), first match wins:

1. Before the joining date → `NOT_SCHEDULED`
2. **Present**: a completed session (for past days with net ≥ `min_present_minutes`), or an
   active session → `PRESENT`. If `count_incomplete_as_present` is on, a day with only
   unresolved sessions also counts.
3. Unresolved session(s) and not present → `INCOMPLETE`
4. Scheduled working day, not a holiday, not on approved leave:
   today → `NOT_YET_CHECKED_IN`; past → `ABSENT`
5. Holiday on a working day → `HOLIDAY`
6. Approved leave on a working day → `ON_LEAVE`
7. Non-working day of the schedule → `WEEKEND`; no schedule → `NOT_SCHEDULED`

Only **approved** leave counts. Pending or rejected leave has no effect, so the day is absent
unless the person attended.

## Expected days and metrics

A day is **expected** when it is a scheduled working day, not a holiday, and either:
* not on approved leave, or
* on approved leave but the person attended anyway (they are then counted as present).

Today is expected only once the person is present. `NOT_YET_CHECKED_IN` is never a penalty.

| Metric | Definition |
|---|---|
| Days present | Days with status `PRESENT` (includes extra days worked on weekends/holidays, reported separately as *extra days*) |
| Days absent | Days with status `ABSENT` |
| Leave days | Days with status `ON_LEAVE` |
| Net office hours | Sum of `net_minutes` of `COMPLETED` sessions in the period |
| Average per attended day | Net minutes ÷ number of `PRESENT` days with net minutes > 0 |
| Attendance % | Present expected days ÷ expected days × 100, shown as "—" when there are no expected days (never divides by zero) |
| Required hours | Sum of the schedule's required minutes over expected days |
| Variance | Net minutes − required minutes (presence time, not "productive" time) |

Exceptions flagged per day: `MISSING_CHECKOUT`, `LATE_ARRIVAL` (first check-in later than
schedule start + `late_grace_minutes`), `SHORT_HOURS` (finished present day below required),
`ABSENT`, and `BELOW_MINIMUM`.

## Team snapshots

Manager and admin "today" cards put each person in **exactly one** category from their day
status: present / absent / not checked in / on leave / incomplete / off. The categories
always add up to the headcount. Accounts with `attendance_tracked = false` (e.g. the system
administrator) are excluded from statistics.

## Corrections

Employees can't edit sessions. They submit a request to fix an existing session (including a
missing check-out) or to add a missing one. Requests are allowed within the policy's correction
window; they can't be for future times, can't overlap other sessions, and allow only one
pending request per session. When a reviewer approves one, inside a single transaction:

1. the original session becomes `VOIDED` with a reason (never deleted),
2. a new `COMPLETED` session with source `CORRECTION` is created, and
3. the reviewer, time, comment and before/after values are written to the audit log.

Reviewers are admins, or managers whose subtree includes the employee (when the
`manager_can_approve` policy is on). Nobody can review their own request.

## Known rule limitation

A session that **starts** after midnight belongs to the new day. This applies even when it
continues an overnight shift after a break, such as 21:00–01:00 and then 01:30–06:00. Most
night-shift workers check in once per shift, so this rarely matters. A future rule could
attach such continuations to the shift's start day.
