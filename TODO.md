# Automatic attendance: TODO

Goal: employees no longer check in or out. Office hours come from Wi-Fi sessions, WFH hours from
Microsoft Teams presence. Work goes one phase at a time; each phase ends with tests, an update of this
file, a summary and the exact commands, then waits for "continue".

Legend: `[x]` done and verified · `[ ]` open · `[~]` blocked (reason given)

## Decisions (2026-11-10)

- **Reuse, don't duplicate.** The spec's tables map onto the existing system:

  | Spec | Here |
  |---|---|
  | `employees(id, name, email, manager_id, team_id, device_mac, aad_object_id)` | `users`: `full_name`, `email`, `manager_id`, `department_id` (= team), `wifi_identity` (= device MAC or Wi-Fi username), `graph_user_id` (= Entra object ID) |
  | `settings` | `attendance_policy` (one row) plus the new rule columns from migration 0003 |
  | `holidays_leaves` | `holidays`. **Leave stays removed** (decision of 2026-11-05, confirmed 2026-11-10): no Leave status |
  | `wifi_events`, `presence_events`, `attendance_daily`, `corrections` | new tables (migration 0003); `corrections` is `attendance_daily_corrections` |
  | roles `employee`, `manager`, `hr_admin` | `EMPLOYEE`, `MANAGER`, `ADMIN` (shown as "HR admin") |

- **Sign-in:** Microsoft Entra ID (OIDC, authorization code + PKCE) **plus** the existing email/password login,
  switchable with `PASSWORD_LOGIN_ENABLED`, until the Entra app registration is live. A dev-only login bypass
  (`DEV_LOGIN_ENABLED`) is refused in production. Entra proves who someone is; their role is managed in the app.
- **Old groundwork replaced:** migration 0003 drops the empty `signals`, `auto_attendance_days`, `worker_jobs` and
  `ingest_api_keys` tables from 0002. It refuses to run if any of them has rows.
- New dependency: `PyJWT[crypto]` (ID-token signature validation).

## Phase 1: Foundation ✅ (2026-11-10)

- [x] Migration 0003: drop the empty 0002 tables (guarded), create `wifi_events`, `presence_events`,
      `attendance_daily`, `attendance_daily_corrections` with unique keys (`employee_id, ts_bucket` and
      `employee_id, work_date`)
- [x] Settings: rule columns on `attendance_policy` with database CHECKs (gap minutes, full-day and half-day
      minutes, Wi-Fi / presence poll minutes, no-data alert minutes, raw retention days, WFH presence states)
- [x] Role-protected settings endpoint (`GET`/`PUT /api/admin/auto-attendance/settings`, HR admin only, audited)
- [x] Entra ID sign-in: `/api/auth/entra/login` and `/callback`, state + nonce + PKCE in a signed short-lived
      cookie, ID token checked (signature via tenant keys, issuer, audience, expiry, nonce, tenant), account
      found by Entra object ID or email (then linked), inactive / unknown / mismatched accounts refused
- [x] Dev login bypass (`POST /api/auth/dev-login`), off by default, impossible in production
- [x] `PASSWORD_LOGIN_ENABLED` switch; `GET /api/auth/providers` tells the login page what to show
- [x] Login page: "Sign in with Microsoft", dev sign-in (clearly marked), password form when enabled
- [x] Seed: 10 sample employees, each with a device MAC and an Entra object ID; default settings; sample holidays
- [x] `.env.example` / `.env.sqlite.example` with every new variable (placeholders only)
- [x] Tests: 0003 up / down / up (data preserved, guard refuses non-empty legacy tables), unique keys, settings
      access per role and validation, Entra flow (success and every rejection), dev login, password switch,
      health endpoint, seed
- [x] PostgreSQL: 0003 verified in CI

## Phase 2: Wi-Fi signal

- [ ] `WifiSource` adapter interface: `fetch_events(since)`
- [ ] `MockWifiSource` (realistic days: arrival, lunch, roaming between APs, laptop sleep, overnight)
- [ ] Stubs: RADIUS accounting receiver, Cisco, Aruba, Meraki
- [ ] `collector_wifi` job (every 3 min): map username / MAC to employee, insert `wifi_events` (unique per bucket)
- [ ] `sessionize` job: merge events into sessions with `gap_minutes` from settings
- [ ] `daily_rollup` job: `first_in`, `last_out`, `office_minutes` into `attendance_daily` (upsert)
- [ ] CLI: `python -m ats.jobs run <collector|sessionize|rollup> --date YYYY-MM-DD`
- [ ] Every job idempotent (safe to re-run)
- [ ] Tests: 3 simulated days; gaps, roaming, lunch sleep, overnight connections

## Phase 3: API and dashboards

- [ ] `GET /me/attendance?month=`, `GET /team/attendance?date=`, `GET /admin/attendance?from=&to=`, `GET /admin/export` (xlsx)
- [ ] Employee monthly calendar + today's live status
- [ ] Manager team-today view
- [ ] HR report table with filters and Excel export
- [ ] Status colours: Present, Half-day, Absent, WFH, Holiday (no Leave, see decisions)
- [ ] Access-control tests per role

## Phase 4: Teams presence (WFH)

- [ ] `collector_presence` job via Microsoft Graph `getPresencesByUserId` (batches of up to 650, `Presence.Read.All`), every 5 min
- [ ] Rollup: no Wi-Fi that day → configured Teams states count as `wfh_minutes`; Wi-Fi always wins
- [ ] Mock Graph client for local development and tests
- [ ] Test: a WFH day in the mock data is classified correctly

## Phase 5: Polish and reliability

- [ ] Corrections: employee requests, manager / HR approve or reject, audit log (who, when, old, new)
- [ ] Holidays and weekends skipped before marking Absent
- [ ] Status rules from settings: ≥ full day = Present, ≥ half day = Half-day, else Absent
- [ ] Missing-data protection: collector silent for 15 min → affected days "No data", alert (email or Teams webhook)
- [ ] Scheduler: OpenShift CronJob YAML set + local crontab (collectors, sessionize, nightly rollup 23:55)
- [ ] Retention job: purge raw events older than 90 days, keep daily summaries
- [ ] Monthly report e-mailed to HR
- [ ] README: setup, environment variables, running each job, swapping the Wi-Fi adapter

## Definition of done (whole project)

- [ ] A full simulated month runs end to end with no manual input
- [ ] Tests pass, this file fully checked, README accurate

## Verification log

| Date | Phase | Backend tests | Frontend tests | Type-check | Build |
|---|---|---|---|---|---|
| 2026-11-10 | Baseline (before Phase 1) | 170 passed, 26 skipped (PostgreSQL-only; green in CI) | 25 passed | clean | ok |
| 2026-11-10 | Phase 1 | 208 passed, 27 skipped (PostgreSQL-only; run in CI) | 29 passed | clean | ok |
