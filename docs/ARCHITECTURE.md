# Architecture, security and API

## Overview

```
Browser (React SPA, Vite)  ──/api──►  FastAPI (Uvicorn)  ──SQLAlchemy/psycopg──►  PostgreSQL
   TanStack Query cache                 routes → services → rules engine             Alembic migrations
```

* In development, Vite proxies `/api` to FastAPI, so the session cookie is first-party.
  The same layout works behind a reverse proxy in production.
* **Routes** (`app/api/routes`) handle HTTP concerns only.
* **Services** (`app/services`) hold business logic and transactions.
* **Rules** (`attendance_rules.py`) are pure functions.
* **Scope** (`scope.py`) is the single place where reporting-line authorization is decided.

```
backend/app/
  core/       config (env validation), security (Argon2id, tokens), errors, clock, timeutil
  db/         engine/session, declarative base, UTCDateTime type
  models/     users, departments, attendance_sessions, work_schedules, holidays, leave_records,
              attendance_corrections, audit_logs, user_sessions, login_attempts, attendance_policy
  schemas/    Pydantic request/response models
  services/   attendance_rules, attendance_data (bulk loading), attendance_service (check-in/out),
              dashboard, corrections, leaves, admin_users, configuration, org, reports, scope,
              auth_service, audit, policy
  api/        deps (auth, CSRF, roles, paging) + routes
  seed.py     repeatable demo data;  cli.py  seed / create-admin
frontend/src/
  lib/        api client (CSRF, errors), query hooks, formatting (office timezone), date helpers
  auth/       AuthProvider (session state), RequireAuth (UX-only route guard)
  components/ ui (design system), layout, charts (Recharts), attendance feature components
  pages/      employee, team, approvals, reports, admin/*
```

## Data model highlights

| Table | Key constraints |
|---|---|
| `users` | unique `employee_code`, unique `email` (stored lower-case), `CHECK manager_id <> id`, FK to manager and department |
| `departments` | unique `lower(name)`, head FK |
| `attendance_sessions` | `check_out_at > check_in_at`, non-negative break/net, completed ⇒ totals present, **partial unique index: one OPEN per employee**, **PostgreSQL `EXCLUDE USING gist` against overlapping sessions** (`btree_gist`) |
| `work_schedules` | single target (employee *or* department *or* org), minutes 1–1440, effective range check; overlapping assignments for the same target rejected by the service |
| `holidays` | unique `(holiday_date, COALESCE(department_id, 0))` |
| `leave_records` / `attendance_corrections` | status enums, reviewer, timestamps, `end >= start`, requested times ordered |
| `audit_logs` | actor, action, entity, before/after JSON, request id, IP |
| `user_sessions` | HMAC of the session token (never the token), CSRF token, idle + absolute expiry, revocation |
| `login_attempts` | throttling by email and by IP |

Circular reporting lines are rejected by walking the proposed manager's chain. Reporting
subtrees are computed with a recursive CTE (`UNION`, so they're cycle-safe).

## Authentication and session security

* **Passwords**: Argon2id (argon2-cffi); rehashed on login if parameters change. Minimum 10
  characters with letters and digits. The hash is never serialized.
* **Sessions**: a random 256-bit token sits in an `HttpOnly` cookie `ats_session`
  (`SameSite=Lax`; `Secure` is forced on in production). The database stores only
  `HMAC-SHA256(SECRET_KEY, token)`.
  * Idle timeout: `SESSION_IDLE_MINUTES` (120). Absolute lifetime: `SESSION_ABSOLUTE_HOURS` (12).
  * Logout, password change, password reset and deactivation revoke sessions server-side.
* **CSRF**: double-submit token. A per-session token is returned by `/auth/login` and
  `/auth/me` and mirrored in the readable `ats_csrf` cookie. Every non-GET authenticated
  request must send it in `X-CSRF-Token`, and it is compared in constant time.
  * Login itself takes JSON only; with the restricted CORS list, cross-site forms can't post it.
* **Brute force**: 5 failures per email or 30 per IP within 15 minutes → HTTP 429 with
  `Retry-After`. Failure messages are generic, and a dummy hash is verified for unknown emails
  to equalize timing.
* **CORS**: explicit origin list (`CORS_ORIGINS`) with credentials; wildcards are refused in
  production.
* **Headers**: `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, and
  `Cache-Control: no-store` on API responses. Every response carries an `X-Request-ID`, which
  is also stored in audit rows.
* **Fail-safe configuration**: with `APP_ENV=production` the app refuses to start if
  `SECRET_KEY` is short or a placeholder, if `COOKIE_SECURE=false`, or if CORS contains `*`.
  The demo seed refuses to run in production.
* **Errors**: one JSON envelope (`{"error": {"code", "message", "details"}, "request_id"}`).
  Stack traces are logged server-side only; database outages return 503.
* **Logging**: passwords, cookies and tokens are never logged. Failed logins log a truncated
  hash of the email, not the address.

## Authorization

| Capability | Employee | Manager | Admin |
|---|---|---|---|
| Own attendance, check-in/out, requests | ✔ | ✔ | ✔ |
| Read another person's attendance | ✘ | reporting **subtree** only | everyone |
| Team dashboards / CSV | ✘ | own subtree, or a sub-manager's subtree | any manager / department / organisation |
| Approve corrections & leave | ✘ | subtree (if policy allows), never own | everyone except self |
| Users, departments, schedules, holidays, policy, audit log, org hierarchy | ✘ | ✘ | ✔ |

* Enforced in the backend on every endpoint via dependencies (`require_roles`) and
  `scope.py`. The frontend route guards only shape the UX.
* Records outside a viewer's scope return **404**, the same as a missing record, so ids can't
  be probed.
* Being a department head gives no extra data access; reporting lines decide access.
* Admin safeguards: you can't change your own role or deactivate yourself, and the last active
  admin can't be demoted or deactivated. A manager with active direct reports can't be
  demoted to EMPLOYEE or deactivated until those reports are reassigned.

## Live data

* Manager and admin dashboards poll every `VITE_DASHBOARD_REFRESH_MS` (30 s default; minimum
  5 s; `0` disables). TanStack Query pauses polling while the tab is hidden.
* The "Updated hh:mm" label shows the last successful fetch.
* Check-in/out, approvals and admin changes invalidate the affected query families
  immediately.

## CSV exports

Exports use the same scope functions as the dashboards, and each export is audited
(`REPORT_EXPORTED`). Cells starting with `= + - @ \t \r` are prefixed with `'` to prevent
spreadsheet formula injection. Files are UTF-8 with a BOM so Excel opens them correctly.

## API endpoints

Interactive documentation: `http://127.0.0.1:8000/api/docs` (Swagger) and `/api/redoc`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/health` | Liveness |
| GET | `/api/health/db` | Database connectivity (503 when down) |
| POST | `/api/auth/login` | Sign in with email and password |
| POST | `/api/auth/logout` | Revoke the current session |
| GET | `/api/auth/me` | Current user profile and CSRF token |
| POST | `/api/auth/change-password` | Change password (signs out other sessions) |
| GET | `/api/attendance/current` | Today's status and active session |
| POST | `/api/attendance/check-in` | Start a session at the current server time |
| POST | `/api/attendance/check-out` | Close the active session (optional `break_minutes`) |
| GET | `/api/attendance/employees/{id}/summary` | Period summary |
| GET | `/api/attendance/employees/{id}/trends` | Day/week/month buckets |
| GET | `/api/attendance/employees/{id}/days` | Daily records (filters, pagination) |
| GET | `/api/attendance/employees/{id}/days/{day}` | One day with its sessions (incl. voided) |
| GET/POST | `/api/corrections` | List mine / reviewable; submit a request |
| POST | `/api/corrections/{id}/approve` `…/reject` `…/cancel` | Workflow actions |
| GET/POST | `/api/leaves` | List mine / reviewable; request leave |
| POST | `/api/leaves/record` | Reviewer records approved leave |
| POST | `/api/leaves/{id}/approve` `…/reject` `…/cancel` | Workflow actions |
| GET | `/api/dashboard/overview` | Snapshot cards + period statistics for a scope |
| GET | `/api/dashboard/employees` | Per-employee table (search, status, sort, paging) |
| GET | `/api/dashboard/trends` | Team trends by day/week/month |
| GET | `/api/dashboard/exceptions` | Exception list and counts |
| GET | `/api/dashboard/departments` | Department comparison (admin) |
| GET | `/api/dashboard/managers` | Manager comparison (admin) |
| GET | `/api/org/hierarchy` | Organisation → department → reporting tree (admin) |
| GET | `/api/team/members` | People in my scope |
| GET/POST | `/api/admin/users` | List/search, create |
| GET/PATCH | `/api/admin/users/{id}` | Read, update (role, manager, department, active…) |
| POST | `/api/admin/users/{id}/reset-password` | Reset password |
| GET | `/api/managers` | Assignable managers |
| GET | `/api/departments` | Departments with member counts |
| POST/PATCH/DELETE | `/api/admin/departments[/{id}]` | Manage departments |
| GET/POST/PATCH/DELETE | `/api/admin/schedules[/{id}]` | Manage work schedules |
| GET | `/api/holidays` | Holidays (scoped to my department unless admin) |
| POST/DELETE | `/api/admin/holidays[/{id}]` | Manage holidays |
| GET / PUT | `/api/policy` / `/api/admin/policy` | Read / update the attendance policy |
| GET | `/api/admin/audit-logs` | Searchable audit log |
| GET | `/api/reports/employee-attendance.csv` | Daily records for one employee |
| GET | `/api/reports/team-attendance.csv` | Per-employee summary |
| GET | `/api/reports/monthly-attendance.csv` | Monthly per-employee summary |
| GET | `/api/reports/department-attendance.csv` | Department comparison (admin) |
| GET | `/api/reports/exceptions.csv` | Exceptions |

## Limits of manual check-in

The system records attendance **events** that people submit from their own browser. It does
not prove physical presence: there is no badge, geofence or network-location check. Treat the
data as self-reported, and use the audit trail and manager review for oversight.
