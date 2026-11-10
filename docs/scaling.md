# Scaling: one admin, a few managers, many employees

The app is built for an organisation shaped like this: **one admin**, **a handful of managers**,
and **thousands of employees**. This page explains what grows with headcount, what was done
about it, and the measured results.

## What grows with headcount

| Work | Who triggers it | Cost |
|---|---|---|
| Check-in / check-out, "My dashboard" | every employee, many times a day | one person's data, **constant** (5–15 ms) |
| Team dashboard | a manager | their team × days in the range |
| Organisation dashboard, reports | the admin | everyone × days in the range |
| People pickers, user list, audit log | managers and the admin | **paged / searched**, constant |

Employee actions stay fast no matter how many employees there are. Only the manager and admin
dashboards grow, and only with the size of the scope and the date range.

## What the backend does

1. **No per-employee queries.** A dashboard request uses a fixed number of SQL statements whether
   the scope has 10 or 10,000 people. `test_dashboard_queries_do_not_grow_with_headcount` fails if
   that ever changes.
2. **Lean loading.** Sessions are read as plain column rows, not ORM objects. Large scopes use one
   date-range query instead of thousands of `IN (...)` parameters. Department and manager names are
   loaded with two small queries instead of being joined onto every row.
3. **A fast rules loop.** Schedule lookups and shift start times are worked out once per department
   and day, not once per person and day. Days without sessions take a fast path. Python's garbage
   collector is paused while records are built (they form no reference cycles). Otherwise its
   repeated full scans make the cost grow faster than the data.
4. **One computation per page** (`app/services/record_cache.py`). A dashboard page sends 7–9
   requests (cards, table, charts) for the same people and dates. The first request computes the
   day records and per-person summaries. The others wait for that result and reuse it instead of
   repeating the work.
   * **Never stale.** The cache key holds everything the numbers depend on: the people, the dates,
     schedules, holidays, the policy, and a fingerprint of the sessions in the range (count, newest
     `updated_at`, highest id). The fingerprint is read from the database on every request, so a
     write is visible on the very next request, from any server process. A per-process write counter
     also covers updates that keep the same timestamp.
   * **Bounded.** At most 1,000,000 cached day records (about 250 MB), least-recently-used first out,
     5-minute expiry.
   * For ranges that reach today, results also change slowly with the clock: a running session
     becomes "missing check-out" after the maximum session length. That is re-checked every 5 minutes.
5. **The browser never downloads everyone.**
   * People pickers (Reports, department head, schedule override) search the server: at most 20
     results per keystroke, via `GET /api/team/members?search=…&limit=20` and `GET /api/admin/users?search=…`.
   * Opening a person or a manager's team uses one lookup: `GET /api/team/members/{id}`.
   * Per-person charts show the **12 lowest** (attendance %, average hours), sorted by the server
     over the whole scope, and say "The 12 lowest of N".
   * The attendance-band chart uses counts computed over everyone by the server (`attendance_bands`
     in `/api/dashboard/overview`). It used to count only the first 200 rows.
   * The hierarchy shows 50 people per list with "Show more". Search finds anyone.

## Measured results

`scripts/scale_benchmark.py` on a development laptop (SQLite, one server process). The numbers are
the time until **all** of a dashboard page's API calls have answered, with calls sent in parallel
like a browser and nothing cached:

| Scope | Range | 5,000 employees before | 5,000 employees now | 10,000 employees now |
|---|---|---|---|---|
| Admin (whole organisation) | this month | ~5 s | **1.3 s** | 3.0 s |
| Admin (whole organisation) | last month | ~18.5 s | **2.1 s** | 4.9 s |
| Admin (whole organisation) | 90 days | ~48 s | **4.4 s** | 9.7 s |
| Manager (1 of 8 teams) | last month | ~1.7 s | **0.3 s** | 0.5 s |
| Employee "Today" panel | — | 20 ms | **8 ms** | 14 ms |

Revisiting the page or changing tabs within the same range takes a few hundred ms, because the
computation is reused.

Run it yourself (it builds a throw-away database in the temp folder, never your real one):

```powershell
backend\.venv\Scripts\python.exe scripts\scale_benchmark.py --employees 5000
```

## Guidance for production

* Use **PostgreSQL**. SQLite is for local development.
* One API process handles this organisation shape comfortably. If you run several, each keeps its own
  cache. Results stay correct, and the first page load per process computes once.
* Memory: budget about 250 MB per API process for the dashboard cache, plus the app.
* Long ranges for the whole organisation (a year × 10,000 people) take tens of seconds. If that
  becomes a regular need, the next step is pre-aggregated daily totals (roadmap F14).
