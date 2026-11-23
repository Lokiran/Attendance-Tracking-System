# .NET backend migration plan (Step 0: analysis)

Status: **approved 2026-11-20** (all recommended decisions). Branch: `feature/dotnet-backend`.

Approved decisions (details in §0):

* **D1:** .NET 10 LTS / C# 14 / EF Core 10. The SDK is pinned in `backend-dotnet/global.json`.
* **D2:** server-side session cookie plus double-submit CSRF, exactly as today. No JWT bearer.
* **D3:** no background jobs; Phase 6 ports sites, devices, device import and the auto-attendance settings only.
* **D4:** approved packages: Isopoh.Cryptography.Argon2, UglyToad.PdfPig, ExcelDataReader (+ .DataSet).
* **D5:** manual mapping (no AutoMapper), FluentAssertions 7.x (Apache-2.0), PDF export with PdfSharp/MigraDoc
  (MIT, so no QuestPDF licence is needed).
* **D6:** add `azure-pipelines.yml` and keep the GitHub Actions workflow until cutover.
* **D7:** support PostgreSQL 16 (CI) and 17 (local).
* **D8:** the .NET API listens on `127.0.0.1:8001`.
* **D9:** validation errors match status, code and field exactly; message wording is best effort.
* **Q8:** the .NET API reads `ATS_ENV_FILE` (absolute, or relative to `backend-dotnet/`), otherwise
  `backend-dotnet/.env` when it exists (gitignored), otherwise environment variables only. It uses the same key names
  as `backend/.env`.
* **Q9:** parity runs use a local copy of `ats_db`. It never leaves the machine and is never committed.

Scope: rewrite `backend/` (FastAPI, SQLAlchemy 2, Alembic) as an ASP.NET Core Web API in `backend-dotnet/`. The React
frontend in `frontend/` must keep working unchanged apart from its backend URL/port. `backend/` stays until cutover
is approved.

Source of truth for this document: `backend/app/**` (routes, schemas, models, services), `backend/alembic/versions/0001–0006`,
`backend/requirements.txt`, `backend/.env.example`, `.github/workflows/ci.yml`, `frontend/vite.config.ts` and `frontend/src/lib/api.ts`.
Sizes: about 10,000 lines of application code, 5,000 lines of tests (253 test functions), 78 endpoints and 20 tables.

---

## 0. Decisions needed before Phase 1

Several findings contradict the task description. Each needs your decision; my recommendation is given first.

| # | Finding | Recommendation |
|---|---|---|
| D1 | **.NET 8 support ended on 10 November 2026.** .NET 10 is the current LTS (supported to November 2028). This machine has the .NET 10 and 11 SDKs and the .NET 8.0.34 runtime, but no .NET 8 SDK. The .NET 10 SDK can still build `net8.0`. | Target **.NET 10 / C# 14 / EF Core 10** (same APIs, still supported). If .NET 8 is mandatory (for example an Azure policy), keep `net8.0` and accept running on an unsupported runtime. |
| D2 | **Authentication is not JWT.** The current mechanism is a server-side session: an HttpOnly cookie `ats_session` (the database keeps only an HMAC-SHA256 of the token), a readable `ats_csrf` cookie and a double-submit `X-CSRF-Token` header on every non-GET request. There are also idle and absolute expiry, login throttling, and Microsoft Entra ID OIDC with PKCE. Switching to JWT bearer would require frontend changes (storing the token, sending an `Authorization` header) and would lose server-side revocation. | **Replicate the session cookie and CSRF exactly** with a custom ASP.NET Core `AuthenticationHandler`, plus role/hierarchy authorization policies. With the same `SECRET_KEY` both backends accept the same sessions, which makes the parity tests much simpler. JWT is used only to validate Entra ID tokens, as today. |
| D3 | **There are no background jobs, Teams presence polling or Wi-Fi collectors** in the Python backend. Automatic attendance consists of a settings endpoint (`/api/admin/auto-attendance/settings`), the empty tables `wifi_events` and `presence_events`, and the office-site and device register (CRUD and import). The CLI has `seed`, `create-admin` and `wfo-refresh`. | Phase 6 ports what exists: sites, devices, device import and the settings. **No Hangfire or BackgroundService**, because there is no behaviour to match. The CLI commands are ported as `ATS.Api` command-line verbs. |
| D4 | Packages needed that are **not in the target stack**: an Argon2id library (existing password hashes are argon2-cffi PHC strings and must keep verifying), PDF text extraction with word positions (timesheet PDFs), and `.xls` (Excel 97–2003) reading. | Approve **Isopoh.Cryptography.Argon2** (verifies PHC strings), **UglyToad.PdfPig** (Apache-2.0, word bounding boxes, the equivalent of pdfminer) and **ExcelDataReader** (MIT, `.xls`). Full list in §9. |
| D5 | **License changes** in packages the task lists. **FluentAssertions 8+** is commercial (Xceed); 7.x is Apache-2.0. **AutoMapper 15+** is commercial (Lucky Penny). **QuestPDF** Community license is free only for organisations under USD 1M annual revenue. | Use **manual mapping** (no AutoMapper) and **FluentAssertions 7.2.x** (or AwesomeAssertions, the Apache-2.0 fork). For QuestPDF, either buy a Professional/Enterprise license or use PdfSharp/MigraDoc (MIT). Your call. |
| D6 | CI today is **GitHub Actions** (`.github/workflows/ci.yml`) on PostgreSQL 16. The task asks for `azure-pipelines.yml`. Both remotes exist (`origin` on GitHub, `azure` on Azure DevOps). | Add `azure-pipelines.yml` with a PostgreSQL 16 service container and keep the GitHub workflow until cutover. |
| D7 | Local PostgreSQL is **17** on port **5433** (the `ats_db` database); CI and docker-compose use 16. | Support both. Npgsql 8/10 works with 13–17. Test on 16 in CI and smoke-test on the local 17. |
| D8 | Python backend port: the Vite proxy targets `VITE_DEV_BACKEND_URL` (default `http://127.0.0.1:8000`). | .NET on **:8001** during the parity phase; at cutover set `VITE_DEV_BACKEND_URL=http://127.0.0.1:8001` (a config-only change). |
| D9 | Error **message texts** for request validation come from Pydantic ("Input should be greater than or equal to 1"). | Match status, `error.code`, `error.details[].field` and the field path exactly. Copy Pydantic's message wording for the handful of constraints in use (ge/le/min_length/max_length/pattern/enum/date/int/bool/missing). Treat exact wording as best effort and list any differences in the parity report. |

---

## 1. The current system

* **Stack:** FastAPI 0.162, Pydantic 2.14, SQLAlchemy 2.1 (sync, psycopg 3), Alembic 1.20, argon2-cffi, PyJWT
  (Entra ID tokens only), openpyxl/xlrd/pdfminer.six (imports), openpyxl/reportlab/Pillow (exports).
* **Concurrency model:** all route functions are plain `def`. They run in the threadpool, with one SQLAlchemy session
  per request (`autoflush=False`, `expire_on_commit=False`).
* **Databases:** PostgreSQL (production, CI and the local `ats_db` on 17) or SQLite (`data/ats-dev.sqlite3`, through
  `ATS_ENV_FILE=.env.sqlite`). Selection is explicit: `DATABASE_URL` is required and nothing falls back silently. The
  app refuses to start on a missing or empty SQLite file and never creates or migrates a database implicitly.
  PostgreSQL that cannot be reached gives a 503 per request.
* **Time:** every timestamp is stored in UTC (`timestamptz` on PostgreSQL, naive UTC text on SQLite). Workdays are
  office-local dates in `OFFICE_TIMEZONE` (default `Asia/Kolkata`). There is one clock (`app.core.clock.utcnow`), which
  tests replace.
* **Data source:** attendance comes **only** from timesheets the HR admin imports monthly (CSV, Excel or PDF), plus
  admin work-mode overrides. Check-in, corrections and leave requests were removed. Their tables still exist and are
  unused; they must be kept, not dropped.
* **Roles:** `EMPLOYEE`, `MANAGER`, `ADMIN` (shown as "HR admin"). A manager sees their whole reporting subtree
  (direct and indirect reports).

### 1.1 Request pipeline (to reproduce as middleware)

1. **Request ID:** take the incoming `X-Request-ID` if it is printable (up to 64 characters), otherwise generate
   `uuid4().hex`. Echo it in the `X-Request-ID` response header and in every error body.
2. **Security headers** on every response: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
   `Referrer-Policy: same-origin`. Every `/api/*` path except `/api/docs` and `/api/redoc` also gets
   `Cache-Control: no-store` (set only if not already present).
3. **CORS:** origins from `CORS_ORIGINS` (comma-separated), credentials allowed, methods
   `GET, POST, PUT, PATCH, DELETE`. Allowed headers are `Content-Type`, `X-CSRF-Token` (configurable through
   `CSRF_HEADER_NAME`) and `X-Request-ID`. Exposed headers are `Content-Disposition` and `X-Request-ID`.
4. **Error envelope** for every error:
   `{"error": {"code": "...", "message": "...", "details": ...optional}, "request_id": "..."}`

   | Source | Status | `code` |
   |---|---|---|
   | `ValidationFailed` / request validation | 422 | `validation_error` (request validation adds `details: [{field, message}]`, and the message is `"<field>: <msg>"`) |
   | `Unauthorized` | 401 | `unauthorized`, or a specific code: `not_authenticated`, `session_invalid`, `session_expired`, `account_inactive`, `invalid_credentials` |
   | `Forbidden` | 403 | `forbidden`, `csrf_failed`, `password_login_disabled` |
   | `NotFound` / unknown route | 404 | `not_found` |
   | Wrong method | 405 | `method_not_allowed` |
   | `Conflict`, or any unhandled DB integrity error | 409 | `conflict` (integrity message: "The change conflicts with existing data.") |
   | `TooManyRequests` | 429 | `too_many_requests`, plus a `Retry-After` header |
   | DB `OperationalError` | 503 | `database_unavailable` |
   | Report generation failure | 500 | `report_failed` |
   | Anything else | 500 | `internal_error` |

   The request-validation `field` is Pydantic's location path without `body`. Examples: `query.page`, `email`,
   `working_days.0`.
5. **OpenAPI and docs** at `/api/openapi.json`, `/api/docs` (Swagger) and `/api/redoc`. `dev-login` and
   `entra/callback` are excluded from the schema.

### 1.2 Shared parameter rules

* **Date range** (`start`, `end`, both optional, ISO dates): `end` defaults to today (office-local) and `start` to the
  1st of `end`'s month. Gives 422 if `end < start` or the range spans more than `MAX_REPORT_DAYS` (366) days.
* **Paging:** `page` ≥ 1 (default 1), `page_size` from 1 to 200 (default 20). The page shape is
  `{items, total, page, page_size}`.
* **`order`:** only `/attendance/employees/{id}/days` (`asc|desc`, default `desc`) and `/dashboard/employees`
  (`asc|desc` with `sort`).
* **`month`:** `YYYY-MM` (regex at the route; the service also checks a 1–12 month and the year range 2000–2100).
  Defaults to the current month.

---

## 2. Endpoint inventory (78 endpoints, all under `/api`)

Auth column: **Public** = no session; **User** = any signed-in active user; **M/A** = MANAGER or ADMIN (otherwise 403
`forbidden`); **A** = ADMIN. **Every non-GET request with a session must carry a valid CSRF header** (otherwise 403
`csrf_failed`). Every authenticated route can return 401 (no or expired session), and every route can return 503 when
the database is down. "Audited" means a row is written to `audit_logs`.

### 2.1 Health and auth

| Method | Route | Auth | Parameters / body | Response | Notes and status codes |
|---|---|---|---|---|---|
| GET | `/health` | Public | – | `{status:"ok", time}` | |
| GET | `/health/db` | Public | – | `{status:"ok", database:"postgresql"\|"sqlite"}` | 503 `{status:"unavailable", database:"unreachable"}`, **without** the error envelope |
| GET | `/auth/providers` | Public | – | `ProvidersOut {password, entra, dev}` | `dev` is false in production |
| POST | `/auth/login` | Public | `{email (EmailStr), password (1–128)}` | `MeResponse` and sets both cookies | 401 `invalid_credentials`, 403 `password_login_disabled`, 429 (throttle). Audited `LOGIN` |
| POST | `/auth/dev-login` | Public | `{email: email or employee ID}` | `MeResponse` and cookies | 404 unless `DEV_LOGIN_ENABLED` and not production; 401. Audited (method `dev`) |
| GET | `/auth/entra/login?next=` | Public | `next` (up to 500 characters) | 302 to Microsoft, sets the `ats_oidc` flow cookie | 404 when Entra is not configured |
| GET | `/auth/entra/callback?code&state&error` | Public | – | 302 to `next`, sets cookies; on error 302 to `/login?error=<code>` | Error codes: `entra_denied`, `entra_error`, `sign_in_expired`, `token_exchange_failed`, `entra_unreachable`, `invalid_token`, `no_account`, `account_mismatch`, `account_inactive` |
| POST | `/auth/logout` | User | – | `{message:"Signed out"}`, clears cookies | Audited `LOGOUT` |
| GET | `/auth/me` | User | – | `MeResponse` | Updates `last_seen_at` at most once a minute |
| POST | `/auth/change-password` | User | `{current_password, new_password}` | `{message}` | 401 wrong current password; 422 weak password (at least 10 characters, at most 128, letters **and** digits). Revokes the user's other sessions. Audited |

`MeResponse = {user: Profile, csrf_token, office_timezone, has_reports, latest_timesheet_date}`

`Profile = {id, employee_code, full_name, email, role, job_title, department: {id, name}|null, manager: {id, full_name, employee_code}|null, date_joined, is_active, attendance_tracked}`

### 2.2 Attendance (employee scope)

All `employees/{employee_id}` routes use `get_viewable_employee`: yourself, anyone if ADMIN, your subtree if MANAGER.
Anything else, **including a missing id, gives 404 `"Employee not found"`**, so ids cannot be probed.

| Method | Route | Auth | Parameters | Response |
|---|---|---|---|---|
| GET | `/attendance/current` | User | – | `CurrentStatusOut {server_time, today, office_timezone, schedule: ScheduleBrief\|null, timesheet_through, my_timesheet_through, last_import_at}` |
| GET | `/attendance/employees/{id}/summary` | User | `start`, `end` | `SummaryOut` (§3.1) with a nested `wfo: WfoRangeSummary` |
| GET | `/attendance/employees/{id}/trends` | User | `start`, `end`, `granularity=day\|week\|month` (default **month**) | `TrendsOut {granularity, headcount:1, items: TrendBucket[]}` |
| GET | `/attendance/employees/{id}/months` | User | `start`, `end` | `MonthOut[]` |
| GET | `/attendance/employees/{id}/days` | User | `start`, `end`, `page`, `page_size`, `status` (DayStatus), `exceptions_only` (bool), `order=asc\|desc` (default desc) | `DaysPage {items: DayRecordOut[], total, page, page_size}` |
| GET | `/attendance/employees/{id}/days/{day}` | User | `day` ISO date | `DayDetailOut {record\|null, timesheet: TimesheetEntryOut\|null, shift: ScheduleBrief\|null, late_grace_minutes}` |

### 2.3 Dashboards, organisation and team (manager and admin)

`manager_id` and `department_id` narrow the scope. A manager may only pass a `manager_id` inside their own subtree
(otherwise 404 `"Manager not found"`). `on_date` defaults to today.

| Method | Route | Auth | Parameters | Response |
|---|---|---|---|---|
| GET | `/dashboard/overview` | M/A | range, `on_date`, `manager_id`, `department_id` | `OverviewOut` |
| GET | `/dashboard/employees` | M/A | range, paging, `on_date`, `manager_id`, `department_id`, `search` (up to 100), `status`, `sort` ∈ {name, code, department, check_in, net_minutes, average_net_minutes, attendance_percentage, status, exceptions} (default name), `order` (default asc), `include_inactive` | `EmployeeRowsPage` |
| GET | `/dashboard/trends` | M/A | range, `granularity` (default **day**), `manager_id`, `department_id` | `TrendsOut` |
| GET | `/dashboard/exceptions` | M/A | range, paging, `manager_id`, `department_id`, `exception_type` (up to 40) | `ExceptionsOut {counts, items, total, page, page_size}` |
| GET | `/dashboard/departments` | A | range, `on_date` | `DepartmentStats[]` |
| GET | `/dashboard/managers` | A | range, `on_date`, `department_id` | `ManagerStats[]` |
| GET | `/org/hierarchy` | A | `include_inactive` | `HierarchyOut` |
| GET | `/team/members` | M/A | `manager_id`, `department_id`, `include_inactive`, `search`, `limit` (1–200) | `TeamMember[]` (includes untracked people) |
| GET | `/team/members/{id}` | M/A | – | `TeamMember`; 404 outside the scope |

### 2.4 Administration

| Method | Route | Auth | Parameters / body | Response | Notes |
|---|---|---|---|---|---|
| GET | `/admin/users` | A | paging, `search`, `role`, `department_id`, `manager_id`, `is_active` | `Page<UserAdminOut>` ordered by full_name | `search` matches name, email or code (LIKE, lower-case) |
| GET | `/admin/users/{id}` | A | – | `UserAdminOut` | 404 |
| POST | `/admin/users` | A | `UserCreate` | 201 `UserAdminOut` | 409 duplicate email or code; 422 for password, department or manager rules. Code stored **upper-case**, email lower-case. Audited |
| PATCH | `/admin/users/{id}` | A | `UserUpdate` (partial; an explicit `null` matters for job_title, department_id and manager_id) | `UserAdminOut` | Rules in §7.11. The audit action depends on what changed |
| POST | `/admin/users/{id}/reset-password` | A | `{password}` | `{message}` | Revokes all of that user's sessions. Audited |
| GET | `/managers` | A | – | `UserAdminOut[]` (active MANAGER and ADMIN users) | |
| GET | `/departments` | User | – | `DepartmentOut[]` with `member_count` (active members) | N+1 count per department today |
| POST | `/admin/departments` | A | `{name 2–100, description ≤2000, department_head_id}` | 201 `DepartmentOut` | Name unique ignoring case. Audited |
| PATCH | `/admin/departments/{id}` | A | partial | `DepartmentOut` | |
| DELETE | `/admin/departments/{id}` | A | – | `{message:"Department deleted"}` | 409 while it has any members (active or not) |
| GET | `/admin/schedules` | A | – | `ScheduleOut[]` (org default, then department, then employee; newest effective first) with `target_label` | |
| POST | `/admin/schedules` | A | `ScheduleBase` | 201 `ScheduleOut` | 422 for the target, days or times rules; 409 when it overlaps another schedule for the same target |
| PATCH | `/admin/schedules/{id}` | A | `ScheduleUpdate` | `ScheduleOut` | |
| DELETE | `/admin/schedules/{id}` | A | – | `{message}` | |
| GET | `/holidays?start&end` | User | – | `HolidayOut[]` | Non-admins see organisation-wide holidays and their own department's |
| POST | `/admin/holidays` | A | `{name 2–120, holiday_date, department_id}` | 201 `HolidayOut` | Unique per (date, department-or-org) |
| DELETE | `/admin/holidays/{id}` | A | – | `{message}` | |
| GET | `/policy` | User | – | `PolicyOut` | **Side effect:** creates the singleton policy row if it is missing, then commits |
| PUT | `/admin/policy` | A | `PolicyUpdate` (partial) | `PolicyOut` | Audited |
| GET | `/admin/audit-logs` | A | paging, `action`, `entity_type`, `actor_id`, `search`, `start`, `end` | `Page<AuditOut>` (with `actor_name`), newest first | start/end are office-local day bounds |
| GET | `/admin/auto-attendance/settings` | A | – | `AutoAttendanceSettingsOut` | |
| PUT | `/admin/auto-attendance/settings` | A | partial update (ranges match the DB CHECK constraints; `wfh_presence_states` deduplicated and trimmed, 1–20 values of at most 32 characters) | same | Audited |

### 2.5 Office sites and devices (admin)

| Method | Route | Parameters / body | Response | Notes |
|---|---|---|---|---|
| GET | `/admin/sites` | – | `OfficeSiteOut[]` (active first, then by name) | |
| POST | `/admin/sites` | `{name 2–100, code 1–16 [A-Za-z0-9_-], network_name ≤64, address ≤500}` | 201 | 409 when name or code is not unique ignoring case. Audited |
| PATCH | `/admin/sites/{id}` | partial plus `is_active` | `OfficeSiteOut` | 404 |
| GET | `/admin/devices` | paging, `search`, `assigned`, `has_mac`, `active` (default **true**) | `Page<DeviceOut>` (`random_mac` computed) | |
| GET | `/admin/devices/summary` | – | `RegisterSummary` | |
| POST | `/admin/devices` | `DeviceCreate` | 201 `DeviceOut` | Serial stored upper-case, MAC as `aa:bb:cc:dd:ee:ff`; 422 when neither is given; 409 when either is already taken |
| PATCH | `/admin/devices/{id}` | `DeviceUpdate` | `DeviceOut` | |
| POST | `/admin/devices/import?apply=false` | multipart `file` (CSV or Excel, ≤5 MB, ≤5000 rows) | `DeviceImportResult` | A dry run unless `apply=true` |

### 2.6 WFO and timesheet imports

WFO scope is the opposite of §2.2: **out-of-scope employees get 403** `"That employee is not in your team"`.
A manager's team is their whole subtree **plus themselves** (when tracked and active).

| Method | Route | Auth | Parameters / body | Response |
|---|---|---|---|---|
| GET | `/me/wfo` | User | `month` | `EmployeeWfoOut` |
| GET | `/team/wfo` | M/A | `month`, `employee_id` | `TeamWfoOut` (403 when `employee_id` is outside the team) |
| GET | `/team/wfo/employees/{id}` | M/A | `month` | `EmployeeWfoOut` |
| GET | `/org/wfo` | A | `month`, `manager_id`, `department_id` | `OrgWfoOut` (grouped by manager; "No manager" last) |
| GET | `/org/wfo/export.xlsx` | A | same | xlsx file. Audited `REPORT_EXPORTED` |
| PUT | `/admin/attendance/{employee_id}/{work_date}/work-mode` | A | `{work_mode, note ≤500}` | `OverrideOut`. 404 for an unknown employee; 422 for a date before joining or more than 366 days ahead. Audited |
| DELETE | `/admin/attendance/{employee_id}/{work_date}/work-mode` | A | – | `OverrideOut`. 404 when there is no override. Audited |
| GET | `/admin/wfo/policy` | A | – | `WfoPolicyOut {min_days_per_week, min_days_per_month, min_office_minutes}` |
| PUT | `/admin/wfo/policy` | A | partial | `WfoPolicyOut`. Audited |
| POST | `/admin/attendance-uploads?sheet&presence_mode=OFFICE\|WFH` | A | multipart `file` (≤5 MB, ≤20,000 rows; CSV/TSV/TXT/XLSX/XLSM/XLS/PDF) | 201 `UploadOut` (status PREVIEW). Audited |
| GET | `/admin/attendance-uploads` | A | – | `UploadOut[]` (recent 50) |
| GET | `/admin/attendance-uploads/{id}` | A | – | `UploadOut` (404) |
| POST | `/admin/attendance-uploads/{id}/commit` | A | `{overwrite_overrides: bool=false}` | `UploadOut`. 409 when not in PREVIEW. Audited |
| POST | `/admin/attendance-uploads/{id}/discard` | A | – | `UploadOut`. 409 when not in PREVIEW |
| POST | `/admin/attendance-uploads/{id}/create-employees` | A | `{employee_refs: string[]\|null (≤5000)}` | `UploadOut` (rows re-checked). 422 when there is nobody to add |

`UploadOut.summary` and `commit_summary` are JSON stored in the database. Old rows must be returned **as stored**
(passthrough `JsonDocument`). New uploads must produce the same keys (§7.9).

### 2.7 Reports (file downloads; same scope rules; every export audited as `REPORT_EXPORTED`)

| Method | Route | Auth | Parameters | Content type |
|---|---|---|---|---|
| GET | `/reports/employee-attendance.csv` | User | `employee_id`, range | `text/csv; charset=utf-8` (with a BOM) |
| GET | `/reports/employee-attendance.xlsx` | User | `employee_id`, range | xlsx |
| GET | `/reports/employee-attendance.pdf` | User | `employee_id`, range | `application/pdf` |
| GET | `/reports/team-attendance.csv` | M/A | range, `manager_id`, `department_id` | csv |
| GET | `/reports/monthly-attendance.csv` | M/A | `year` (2000–2100), `month` (1–12), `manager_id`, `department_id` | csv |
| GET | `/reports/department-attendance.csv` | A | range | csv |
| GET | `/reports/exceptions.csv` | M/A | range, `manager_id`, `department_id` | csv |

All downloads set `Content-Disposition: attachment; filename="..."` and `Cache-Control: no-store`. A generation
failure that is not an `AppError` becomes 500 `report_failed`.

---

## 3. Response shapes (DTOs)

Every property is **snake_case** (`JsonNamingPolicy.SnakeCaseLower`, available from .NET 8). Enums serialize as
their string value. `null` values are **included**, never omitted. Field order follows the Pydantic declaration order
(not required by JSON, but it keeps diffs readable).

### 3.1 Attendance
* `DayRecordOut {date, status, expected, scheduled_workday, required_minutes, net_minutes, hours_known, work_mode, first_in, last_out, holiday_name, exceptions[]}`
* `SummaryOut {start, end, expected_days, present_days, present_expected_days, extra_present_days, absent_days, wfh_days, leave_days, holiday_days, weekend_days, not_scheduled_days, no_timesheet_days, net_minutes, required_minutes, hours_days, hours_required_minutes, variance_minutes, average_net_minutes, attendance_percentage, counted_days, late_days, wfo}`
* `ScheduleBrief {name, start_time, end_time, required_minutes, working_days[]}` (working_days sorted)
* `TimesheetEntryOut {work_mode, wfo_source, upload_work_mode, upload_minutes, badge_minutes, first_in, last_out, upload_filename, override_work_mode, override_note, overridden_by_name, overridden_at, conflicts[]}`
* `MonthOut {label "Sep 2026", start, end, partial, covered, working_days, office_days, required_days, wfo_band, wfh_days, leave_days, absent_days, attendance_percentage, average_net_minutes}`

### 3.2 Dashboards
* `Snapshot {present, wfh, absent, leave, no_timesheet, off}`
* `PeriodStats {expected_days, present_expected_days, present_days, absent_days, wfh_days, leave_days, no_timesheet_days, late_days, net_minutes, required_minutes, hours_days, hours_required_minutes, variance_minutes, average_net_minutes, attendance_percentage, counted_days}`
* `OverviewOut {generated_at, on_date, start, end, headcount, snapshot, period, attendance_bands: [{label, count}], timesheet_through}`. The band labels are `<70%`, `70–80%`, `80–90%`, `90–95%`, `95–100%` (with en dashes).
* `EmployeeRow {employee_id, employee_code, full_name, job_title, role, is_active, department_id, department_name, manager_id, manager_name, day: {status, first_in, last_out, net_minutes, hours_known, work_mode, exceptions[]}, period: PeriodStats, exception_count}`
* `TrendBucket {start, end, label, expected, present_expected, present, absent, wfh, net_minutes, required_minutes, hours_required_minutes, attended, late, short_hours, attendance_percentage, average_net_minutes}`. Labels: day `"05 Oct"`, week `"Wk 41 (05 Oct)"`, month `"Oct 2026"`. **The JSON key order in Python differs** (it follows insertion order in `bucketize`); this is irrelevant to JSON consumers.
* `ExceptionRow {employee_id, employee_code, full_name, department_name, date, status, exceptions[], first_in, last_out, net_minutes, hours_known, required_minutes}`
* `DepartmentStats {department_id, department_name ("Unassigned" for null), head_name, headcount, snapshot, period}`
* `ManagerStats {manager_id, manager_name, employee_code, department_name, team_size, direct_reports, snapshot, period}`
* `HierarchyOut {total_people, departments: [{id, name, description, head: {id, full_name}|null, member_count, nodes: HierarchyNode[]}]}`, where `HierarchyNode {id, employee_code, full_name, role, job_title, is_active, direct_reports, children[]}`
* `TeamMember {id, employee_code, full_name, role, job_title, department_name, manager_id, manager_name, is_active}`

### 3.3 WFO
* `WfoSummaryOut {working_days, standard_days, wfo_days, wfh_days, office_days_below_minimum, leave_days, holiday_days, weekoff_days, required_days, shortfall_days, wfo_percentage, compliance (COMPLIANT|NON_COMPLIANT), wfo_band (MET|NEAR|BELOW)}`
* `WfoRangeSummary` = WfoSummaryOut plus `weekly_wfo_breakdown: WfoWeek[]`
* `WfoWeek {week_start, week_end, label "05 Oct – 11 Oct" (en dash), working_days, wfo_days, required, status (MET|SHORT|IN_PROGRESS|UPCOMING), shortfall}`
* `EmployeeWfoOut {employee: EmployeeBrief, month, start, end, today, policy, summary, weekly_wfo_breakdown, days: WfoDay[]}`
* `WfoDay {date, work_mode, wfo_source, is_wfo_day, office_minutes, wfh_minutes, conflicts[], scheduled, overridden, override_note, in_progress, future}`
* `EmployeeBrief {id, employee_code, full_name, department_name, manager_id, manager_name}`
* `WfoRow` = WfoSummaryOut plus `employee`. `WfoTotals {headcount, working_days, wfo_days, wfh_days, wfo_percentage, compliant, non_compliant, band_met, band_near, band_below}`
* `TeamWfoOut {month, start, end, policy, totals, rows}`; `OrgWfoOut {month, start, end, policy, totals, groups: [{manager_id, manager_name, department_name, totals, rows}]}`
* `OverrideOut {employee_id, work_date, work_mode, wfo_source, overridden, override_note, overridden_at}`
* `UploadOut {id, filename, status, file_size, row_count, error_count, summary, commit_summary, overwrite_overrides, uploaded_by_name, created_at, committed_at, period_start, period_end}`

### 3.4 Administration and devices
`UserAdminOut`, `DepartmentOut {id, name, description, department_head_id, head, member_count, created_at}`,
`ScheduleOut`, `HolidayOut`, `PolicyOut`, `AuditOut`, `OfficeSiteOut`, `DeviceOut`, `RegisterSummary`, `DeviceImportResult`.
The field lists are in `backend/app/schemas/admin.py` and `devices.py` and are ported one-to-one.

---

## 4. JSON wire-format contract (what the .NET serializer must reproduce)

Verified against the running Pydantic version:

| Type | Python output | .NET requirement |
|---|---|---|
| Aware datetime (UTC) | `"2026-11-19T08:39:37.577436Z"`; with zero microseconds, `"2026-11-19T08:00:00Z"` | Custom `DateTimeOffset` converter: UTC, `Z`, **6** fraction digits, omitted when zero (the .NET default writes 7 digits and `+00:00`) |
| Date | `"2026-09-01"` | `DateOnly`, ISO |
| Time | `"09:30:00"` (and `"09:30:00.123456"` when it has microseconds) | `TimeOnly` converter `HH:mm:ss[.ffffff]` |
| Float | `100.0`, `66.7` | System.Text.Json writes `100` for 100.0. The frontend is unaffected (JS numbers). The parity diff compares numbers by value; byte-identical output would need a custom double converter (optional, decided in Phase 4). |
| Enum | `"OFFICE"` | `JsonStringEnumConverter` with the exact upper-case names |
| Dict keys | as stored | Passthrough for stored JSON (`summary`, `before_values`, ...) |
| Query booleans | `true/false/1/0/yes/no/on/off` (Pydantic accepts all of these) | Custom model binder for `bool?` query parameters |
| Query enums | case-sensitive exact values | Same, 422 otherwise |

Python `round()` rounds halves to even on the exact binary value of the double (`round(2.675, 2) == 2.67`), and
`round_half_up` in `wfo_rules` uses `Decimal(str(x))`. .NET needs a `PyMath.Round(double, digits)` that reproduces
CPython's algorithm (correctly rounded through the shortest repr) and `PyMath.RoundHalfUp` via
`decimal.Parse(x.ToString("R"))`. Both get unit tests over generated ratios against values captured from Python.

---

## 5. Database schema inventory

* **Naming convention** (must be reproduced exactly, since migrations and constraint names depend on it):
  `pk_<table>`, `fk_<table>_<col>_<reftable>`, `ix_<label>`, `uq_<table>_<col>`, `ck_<table>_<name>`.
* **Enums** are `VARCHAR(n)` with a CHECK constraint listing the values (`native_enum=False`); there are no native
  PostgreSQL enums.
* **JSON** columns are PostgreSQL `json` (not `jsonb`) and SQLite `TEXT`.
* **Timestamps** use the `UTCDateTime` decorator: `timestamptz` on PostgreSQL, naive UTC `DATETIME` text on SQLite.
  Naive values are rejected on write.
* `TimestampMixin` = `created_at`, `updated_at` (both NOT NULL; `updated_at` is set on update by the ORM, not by a
  DB trigger).
* Integer primary keys are `SERIAL` on PostgreSQL. `BigId` = `BIGINT` on PostgreSQL and `INTEGER` on SQLite (so it
  autoincrements). The migration history is in the `alembic_version` table (current head `0006`).

### 5.1 Tables in use

| Table | Columns (type, null, default) | Keys, indexes, constraints |
|---|---|---|
| `users` | id int PK; employee_code varchar(32) NN **unique**; full_name varchar(120) NN; email varchar(255) NN **unique** (lower-case); password_hash varchar(255) NN; role varchar(16) NN CHECK; job_title varchar(120); department_id → departments SET NULL; manager_id → users SET NULL; is_active bool NN; attendance_tracked bool NN; date_joined date NN; graph_user_id varchar(64); wifi_identity varchar(255); created_at, updated_at | `ck_users_not_own_manager` (manager_id IS NULL OR manager_id <> id); `ix_users_manager_id`; `ix_users_department_id`; unique indexes `uq_users_graph_user_id` and `uq_users_wifi_identity` |
| `departments` | id; name varchar(100) NN; description text; department_head_id → users SET NULL (`fk_departments_head_users`, use_alter); timestamps | **expression** unique index `uq_departments_name_lower` on `lower(name)` |
| `work_schedules` | id; name varchar(100) NN; employee_id → users CASCADE; department_id → departments CASCADE; start_time time NN; end_time time NN; required_minutes int NN; working_days **json** NN (ISO weekdays); effective_from date NN; effective_to date; timestamps | ck `single_target`, `required_minutes_range` (1–1440), `effective_range`; ix on employee and on department |
| `holidays` | id; name varchar(120) NN; holiday_date date NN (indexed); department_id → departments CASCADE; created_at | **expression** unique index `uq_holidays_date_department` (holiday_date, `COALESCE(department_id, 0)`) |
| `attendance_policy` | singleton id=1; min_present_minutes int 0; count_incomplete_as_present bool false; max_session_minutes int 960; late_grace_minutes 15; correction_window_days 30; manager_can_approve true; auto_attendance_enabled false; auto_shadow_mode true; gap_minutes 30; full_day_minutes 480; half_day_minutes 240; wifi_poll_minutes 3; presence_poll_minutes 5; no_data_alert_minutes 15; raw_retention_days 90; min_wfo_days_per_week 3; min_wfo_days_per_month 12; min_office_minutes_for_wfo_day 240; wfh_presence_states json `["Available","Busy","InAMeeting"]`; updated_at; updated_by_id → users SET NULL; notes text | 12 CHECK constraints (singleton plus the ranges in the model). Several columns have **server defaults** from migrations 0002–0004 |
| `attendance_daily` | id int; employee_id → users CASCADE NN; work_date date NN; first_in, last_out timestamptz; office_minutes int NN 0; wfh_minutes int NN 0; status varchar(16) CHECK (DailyStatus) NN; corrected bool NN; computed_at; work_mode varchar(16) CHECK; wfo_source varchar(20) CHECK; conflicts json NN `'[]'`; upload_work_mode, override_work_mode (CHECK); upload_minutes, badge_minutes, wifi_minutes int; upload_id → attendance_uploads SET NULL; override_note varchar(500); overridden_by_id → users SET NULL; overridden_at; timestamps | **unique** (employee_id, work_date); ix (work_date, status); ck minutes non-negative; ck in/out order |
| `attendance_uploads` | id int; filename varchar(255); file_sha256 varchar(64); file_size int; status varchar(16) CHECK; row_count, error_count int; summary json; commit_summary json; overwrite_overrides bool; uploaded_by_id, committed_by_id → users SET NULL; created_at; committed_at; period_start, period_end date | ix created_at |
| `attendance_upload_rows` | id bigint; upload_id → uploads CASCADE NN; row_number int NN; employee_ref varchar(255); employee_id → users CASCADE; work_date date; raw_work_mode varchar(64); work_mode CHECK; raw_source varchar(64); is_badge bool NN; minutes int; first_in, last_out; error varchar(255) | ix (upload_id, row_number); ix (employee_id, upload_id); ck minutes ≥ 0 |
| `audit_logs` | id bigint; actor_user_id → users SET NULL; action varchar(64) NN; entity_type varchar(64) NN; entity_id varchar(64); before_values json; after_values json; request_id varchar(64); ip_address varchar(64); created_at | ix created_at; ix (entity_type, entity_id); ix actor |
| `user_sessions` | id bigint; user_id → users CASCADE NN; token_hash varchar(64) **unique**; csrf_token varchar(64) NN; created_at, last_seen_at, expires_at NN; revoked_at; ip_address varchar(64); user_agent varchar(255) | ix user_id |
| `login_attempts` | id bigint; email varchar(255) NN; ip_address varchar(64); succeeded bool NN; created_at | ix (email, created_at); ix (ip_address, created_at) |
| `office_sites` | id; name varchar(100) NN; code varchar(16) NN; network_name varchar(64); address text; is_active bool; timestamps | expression unique indexes on `lower(name)` and on `lower(code)` |
| `devices` | id; serial_number varchar(64) (upper-case); asset_tag varchar(64); mac_address varchar(17); hostname varchar(64); model varchar(120); user_id → users SET NULL; is_active bool; source varchar(16) default `MANUAL`; notes text; timestamps | ck `has_identifier`; unique serial; unique mac; ix user_id; expression ix `lower(hostname)` |
| `wifi_events` | id bigint; employee_id → users CASCADE; ts_bucket, first_seen_at, last_seen_at NN; event_type varchar(16) CHECK; source varchar(32); access_point varchar(64); site_id → office_sites SET NULL; device_id → devices SET NULL; created_at | unique (employee_id, ts_bucket); ix ts_bucket; ck seen order. **Empty, unused by the API** |
| `presence_events` | id bigint; employee_id CASCADE; ts_bucket, polled_at; availability varchar(32) NN; activity varchar(32); source varchar(32); created_at | unique (employee_id, ts_bucket); ix ts_bucket. **Empty, unused** |

### 5.2 Legacy tables (unused, must be kept with their data)
`attendance_sessions` (with the **PostgreSQL-only** `btree_gist` extension and the exclusion constraint
`ex_attendance_no_overlap`, plus a partial unique index `uq_attendance_one_open_session` WHERE status='OPEN'),
`attendance_corrections`, `leave_records`, `attendance_daily_corrections`. The columns are in
`backend/app/models/attendance.py`, `requests.py` and `auto_attendance.py`.

---

## 6. Migration strategy (EF Core in place of Alembic)

1. **Hand-write the EF model to match the database**, not the other way round: explicit table, column and constraint
   names, `HasConversion<string>()` with lengths for the enums, `HasCheckConstraint` with the exact names, and JSON
   value converters. Npgsql would otherwise map `List<int>` to `integer[]`, and `working_days` must stay `json`.
   Use `UseSerialColumns()` on Npgsql so a fresh database matches SQLAlchemy's `SERIAL`.
2. **Baseline migration `0001_Baseline`** creates the full head-`0006` schema for **new** databases. Things EF cannot
   model go in `migrationBuilder.Sql(...)`, branched per provider:
   * the expression indexes (`lower(name)`, `lower(code)`, `lower(hostname)`, `COALESCE(department_id, 0)`);
   * `CREATE EXTENSION btree_gist` and the exclusion constraint (PostgreSQL only);
   * the partial unique index (both providers);
   * server defaults on `attendance_policy`.
3. **Existing databases** (`ats_db`, `ats-dev.sqlite3`): no DDL is run. A one-off, idempotent `ef-baseline` command
   checks that `alembic_version` = `0006`, compares the live schema with the expected one (tables, columns, indexes and
   constraints, by name), and only then inserts the baseline row into `__EFMigrationsHistory`. `alembic_version` is
   left in place so the Python backend keeps working side by side until cutover.
4. **Schema-equivalence test** in CI: Alembic `upgrade head` on one database and EF `database update` on another, then
   compare `information_schema`, `pg_indexes` and `pg_constraint` (and `sqlite_master` for SQLite). This must show no
   differences.
5. **SQLite storage formats:** SQLAlchemy writes `YYYY-MM-DD HH:MM:SS.ffffff` (always 6 digits) for datetimes,
   `HH:MM:SS.ffffff` for times and `YYYY-MM-DD` for dates. EF Core's defaults differ (7 digits, trimmed). Custom
   SQLite value converters will write SQLAlchemy's exact format, so text comparisons in SQL keep ordering correctly and
   both backends can share a SQLite file.

### 6.1 As built (phase 2)

* **PostgreSQL DDL:** the baseline SQL is `pg_dump --schema-only` of a new Alembic database, except for 19 CHECK
  constraints. pg_dump prints enum CHECKs as `= ANY ((ARRAY[...])::text[])`, which PostgreSQL stores as a different
  expression when re-run. So they are written as SQLAlchemy emitted them (`col IN (...)`, `... BETWEEN 1 AND 60`), and
  the stored definitions are then identical.
* **Long constraint names:** two foreign-key names exceed 63 characters. On PostgreSQL SQLAlchemy shortened them
  (`…_attendanc_e605`, `…_attendan_ca71`); on SQLite they are full length. The model uses each provider's name.
* **SQLite comparison:** Alembic's batch mode rebuilt some tables with the table constraints in reflection order,
  which differs between databases. The catalog sorts table-level constraints; column order still counts.
* **EF bookkeeping:** EF's `__EFMigrationsHistory` and `__EFMigrationsLock` tables are excluded from schema
  comparisons. EF's foreign-key index convention is off, since Alembic only created declared indexes.
* **Verified:** new databases created by `migrate` match the Alembic catalog exactly (PostgreSQL: 445 entries,
  SQLite: 55). `ats_db` and the dev SQLite file match too, and every row of both loads through the EF model.

### 6.2 As built (phase 3: authentication)

* **Mechanism:** a custom `AuthenticationHandler` (scheme `ats-session`) authenticates only on `[Authorize]`
  endpoints, because there is no default scheme. Public endpoints never read the session or check CSRF, as in Python.
  Failures are raised as the Python errors (401 codes, 403 `csrf_failed` / `forbidden`) and rendered by the error
  middleware.
* **Verified across backends:** Python and .NET running on one database with the same `SECRET_KEY`:
  * a session cookie from either backend works on the other, and a logout on one is honoured by the other;
  * `/api/auth/me`, `/api/auth/providers`, the 401 body and a 422 body are identical;
  * argon2 hashes verify in both directions, with no rehash.
* **Validation messages:** request validation uses FluentValidation with Pydantic's wording ("Field required",
  "String should have at least 1 character", email-validator's reasons). Validators are registered by a small
  reflection helper rather than the FluentValidation.DependencyInjectionExtensions package.

### 6.3 As built (phase 4: attendance, dashboards and WFO reads)

* **Layout:** the rule engines are pure code in `ATS.Domain/Rules` (`AttendanceRules`, `WfoRules`, `PyMath` for
  Python's rounding and `OfficeTime` for zoneinfo's local-to-UTC rule). Loading, coverage, the record cache and the
  services are in `ATS.Application` (`Attendance`, `Wfo`, `Dashboard`, `Reference`). The controllers only read
  parameters and call one service.
* **Parameters:** `QueryReader` collects errors in FastAPI's order (dependency parameters first, then path, then
  query) with Pydantic's messages, so a 422 body lists the same errors in the same order.
* **Record cache:** a singleton with one computation per key at a time, an LRU bound of 1M day records and a 300 s
  TTL. An EF `SaveChangesInterceptor` bumps the write version when `attendance_daily` or `attendance_uploads`
  change, which retires cached results, like the Python write hook.
* **Verified:**
  * The rule engines are compared with Python on generated fixtures (`tools/make_rule_fixtures.py`, synthetic,
    seed 20261120): 300 attendance cases in three time zones (with DST), 1,500 day classifications, 300 WFO
    summaries and over 3,000 rounding cases. All identical.
  * The endpoints are compared with `tools/parity/compare.py` (admin, manager and employee; values, validation
    errors, 403/404 scope errors, every sort). On a local copy of the dev SQLite database and on a copy of `ats_db`
    restored into `ats_test`, 1316 of 1316 responses are identical.
* **Small fix:** `/api/health` now returns `time` as `isoformat()` text (`+00:00`), because the Python route returns
  a plain dict rather than a Pydantic model.
* **Parity quirks** P1–P4 and P6–P11 are kept, each marked `TODO(parity)` in the code.


### 6.4 As built (phase 5: timesheet import)

* **Readers** (`ATS.Application/Uploads`):
  * CSV is a port of the C `csv` reader's state machine (`PyCsv`), with the same decoding and delimiter choice.
  * `.xlsx` / `.xlsm` use a small reader of our own (`XlsxReader`: `System.IO.Compression` and `XmlReader`) that copies
    openpyxl's read-only mode: rows cut to the sheet's declared dimension, its date / timedelta format rules and
    built-in format table. ExcelDataReader could not reproduce the row widths, which decide where copied swipe times
    land. ExcelDataReader is used for `.xls` only, with xlrd's date rules.
  * PDF uses PdfPig letters. pdfminer's characters and text lines are rebuilt from them (advance width, a box one font
    size high, `group_objects` with line_overlap 0.5 and char_margin 2.0); the word, row, column and glue rules are
    ported line by line.
  * `PyStrptime` reproduces CPython 3.14 `strptime` for the date and time formats used.
* **Packages:** `ExcelDataReader` 3.9.0 (MIT) and `PdfPig` 0.1.16 (Apache-2.0; the approved "UglyToad.PdfPig" is
  published under the NuGet id `PdfPig`). ClosedXML is not needed until the exports (phase 7).
* **Mapping:** `config/wfo_mapping.json` is a copy of the Python file, shipped next to the API; `WFO_MAPPING_FILE` replaces
  it, as in Python.
* **Writes:** preview, create-employees, commit and override run in one transaction each and save in between where
  Python flushes; PostgreSQL takes the same `FOR UPDATE` row lock on the upload.
* **Verified:**
  * Readers: `tools/make_upload_fixtures.py` records what Python reads from 148 synthetic files (CSV variants, workbooks
    with every value type and number format, 63 PDFs with glued labels, IDs and dates) and the value parsers on 75 inputs
    and 62 date sequences. .NET reads all of them identically. The ten real June–October timesheet PDFs and four real
    workbooks on this machine also read identically (opt-in test, files never committed).
  * Writes: `tools/parity/compare_writes.py` sent the same uploads (samples, synthetic workbooks, an error file, real
    PDFs and a real workbook), create-employees, commits (with and without overwrite), discards, overrides and policy
    changes to Python and .NET, each on its own copy of the dev database. All 77 responses were identical, and so were
    the resulting `attendance_daily`, uploads, upload rows, users, policy and the 406 audit rows (P11 aside).
  * The pytest cases of `test_wfo_upload.py`, `test_timesheet_files.py` and the write tests of `test_wfo_api.py` are
    ported to xUnit, with a PostgreSQL run of the whole upload lifecycle.
* **Not covered by parity runs:** `.xls` (no tool here writes one for the fixtures); it is read with ExcelDataReader and
  xlrd's rules and tested for its error message only.


### 6.5 As built (phase 6: administration, devices, settings, CLI)

* **Endpoints:** users (list, get, create, update, reset password), managers, departments, schedules, holidays, the
  attendance policy, the audit log, office sites, the device register (list, summary, create, update, import) and the
  automatic-attendance settings. Services: `AdminUserService`, `ConfigurationService`, `DeviceService` (with the import).
* **Request bodies** are read by `BodyReader` field by field, in each Pydantic model's order and with its lax JSON rules
  and messages. It records which fields were sent, so PATCH keeps Pydantic's `exclude_unset` behaviour (an explicit null
  clears a field). Body problems are reported after the path and query problems, as FastAPI does.
* **Audit values** now compare lists by content (Python's `==`), so an unchanged `working_days` or presence list is not
  reported as a change.
* **CLI:** `seed`, `create-admin` and `wfo-refresh` run inside the API with its configuration and services
  (`dotnet run --project src\ATS.Api -- <command>`). SEED_* and ATS_ADMIN_PASSWORD come from the environment, then the
  selected env file. The seed refuses production. It uses a port of CPython's `random.Random` (`PyRandom`: Mersenne
  Twister with string seeding, `gauss`, `choice`), so it loads exactly the data the Python seed loads.
* **Verified:**
  * `tools/parity/compare_admin.py` sent the same 138 requests to Python and .NET, each on its own seeded copy:
    reads with filters and paging, every write, validation and conflict errors, and a manager's 403s. All responses
    were identical, and so were the resulting users, departments, schedules, holidays, policy, sites, devices, revoked
    sessions and audit rows. P14 was found this way and is now copied.
  * `seed` on two new databases (one per backend) gave identical tables, including all 676 random demo days.
    `wfo-refresh` and `create-admin` printed the same output and left identical tables (HTTP request ids aside).
  * The pytest cases of `test_admin.py`, `test_devices.py`, `test_auto_settings.py` and the admin parts of
    `test_permissions.py` are ported to xUnit, with a PostgreSQL run of the admin endpoints.


### 6.6 As built (phase 7: reports and exports)

* **Endpoints:** the five CSV reports (`employee-attendance`, `team-attendance`, `monthly-attendance`,
  `department-attendance`, `exceptions`), one employee's attendance as xlsx and as PDF, and `GET /org/wfo/export.xlsx`.
  The download headers are Python's (`attachment; filename="..."`, `Cache-Control: no-store`), every export is audited
  as `REPORT_EXPORTED`, and a failure is 500 `report_failed`.
* **CSV** (`CsvReport`): BOM, CRLF, minimal quoting and `safe_cell`. Hours are text, so a negative variance gets the
  formula apostrophe, as in Python.
* **xlsx** with ClosedXML 0.105.1 (MIT; its SixLabors.Fonts 1.0.0 is Apache-2.0). ClosedXML treats a leading apostrophe
  as Excel's quote prefix and drops it, so text that starts with one is written with the apostrophe doubled; the stored
  value is then the same as openpyxl's.
* **PDF** with **PdfPig's builder** instead of PdfSharp/MigraDoc. PdfPig is already a dependency (phase 5) and writes the
  same standard fonts reportlab uses (Helvetica, ZapfDingbats), so no font files or font resolver are needed on Linux.
  `EmployeePdf` reproduces reportlab's frame, tables (repeated header rows, page breaks), tiles, month chart, header
  band with logo (PNG or JPEG, embedded once) and "Page n of N" footer.
* **Verified** with `tools/parity/compare_reports.py`: 167 requests as admin, manager and employee on a copy of the dev
  database, including scope errors (404 / 403) and parameter errors. CSV files are byte-identical; xlsx files have the
  same sheets and cell values; PDFs have the same pages and words. This also held with a company name, brand colours
  and a PNG or JPEG logo, and with names that need formula escaping. On one PDF, the 291 text lines found in both files
  sit at the same x position and within 1 pt (at most 2.5 pt) vertically, and the logo is in the same box.
  pdfminer extracts the status bullet (ZapfDingbats code 0x6C, a filled circle in both files) as "n" from reportlab's
  font dictionary and as "l" from PdfPig's; the script ignores that.
* The pytest cases of `test_attendance_export.py` and the report tests of `test_dashboards_reports.py` are ported to
  xUnit; the PostgreSQL admin test also exports.

### 6.7 As built (phase 8: tests and CI)

* **pytest ports:** the remaining cases of the 253 pytest functions are ported: the rule engines, the attendance,
  dashboard, permission and WFO read endpoints, the scale tests, the PostgreSQL behaviour tests and the database start-up
  checks. [`backend-dotnet/tests/COVERAGE.md`](../backend-dotnet/tests/COVERAGE.md) maps every Python test file to its
  xUnit classes and lists what is not ported and why: Alembic-only migration steps, the psycopg driver check and
  `test_identities.py`, whose code serves only a standalone Python script. The suite has 363 tests. All pass with
  `ATS_REQUIRE_POSTGRES=1` against a local PostgreSQL 17 test database; the one skip is the opt-in read-only check of a
  real database.
* **Testcontainers:** `ATS_TEST_POSTGRES_CONTAINER=1` starts a throwaway `postgres:16` container
  (Testcontainers.PostgreSql 4.16.0, MIT) when no `ATS_TEST_POSTGRES_URL` is given. CI uses a service container and
  the URL instead. The container path is **not verified locally**, because Docker is not running on the development
  machine; without Docker it fails with Testcontainers' `DockerUnavailableException`.
* **CI:** `azure-pipelines.yml` (decision D6) runs three jobs: the .NET backend (build with warnings as errors, tests on
  a PostgreSQL 16 service with the PostgreSQL tests required, TRX results published), the Python backend and the
  frontend (the same steps as the GitHub workflow). `.github/workflows/ci.yml` gains the same .NET job and keeps its
  existing jobs until cutover. Neither pipeline has run yet, because the branch is not pushed.
* **Record cache fingerprints:** each fingerprint (row count, latest change, highest id) is now one aggregate query
  instead of three. The scale test showed the query count growing with the number of people once an organisation
  crossed the cache threshold (25 people). Python's count stays flat. The .NET count now grows by 2 queries between 30
  and 73 people (both above the threshold). The test starts at 30 for that reason, and the reason is written in the
  test.
* **Parity scripts:** `tools/parity/compare.py` (reads), `compare_writes.py`, `compare_admin.py` and
  `compare_reports.py`, described in the backend README; their results are recorded in §6.3–§6.6.

### 6.8 As built (phase 9: container, docs and cutover)

* **Container:** `backend-dotnet/Dockerfile` is a multi-stage build. The SDK is `sdk:10.0.404-noble` (the `global.json`
  pin) and the runtime is `aspnet:10.0.15-noble`, which includes ICU and tzdata; it runs as the image's non-root user.
  `URLS=http://+:8001` and `ATS_HOME=/app` are set, settings come from environment variables or a mounted
  `/app/.env`, and `.dockerignore` keeps env files and databases out of the build context. Docker is not running on
  the development machine, so the image has **not been built locally**. The `api_image` job in both pipelines builds
  it, runs `migrate` and `schema-check` against PostgreSQL 16 with `APP_ENV=production`, starts it and checks
  `/api/health/db`. Locally, the same steps were run on the published output (`dotnet publish`, the Dockerfile's
  command) with environment variables only and `ATS_HOME` set: migrate, seed, and the existing end-to-end
  `scripts/smoke_test.py` through a Vite dev server proxying to it. All 33 checks passed.
* **Cutover:** [`dotnet-cutover.md`](dotnet-cutover.md) covers configuration, backup (`pg_dump`), `schema-check`,
  `ef-baseline`, starting the API, a read-only check on real data, switching the Vite proxy (`VITE_DEV_BACKEND_URL`)
  or a reverse proxy, watching, and rolling back.
* **`tools/cutover_check.py`** (standard library only): a read-only check with a real account. It compares each read
  with the Python backend when `--compare` is given. On a seeded database it found every response identical:
  23 reads as an administrator, 16 as a manager and 9 as an employee. JSON media types are compared without
  `charset`, because .NET adds `; charset=utf-8` and FastAPI does not.
* **Rollback safety:** `ef-baseline` adds `__EFMigrationsHistory`, which made `alembic check` report drift; a later
  `--autogenerate` would also have proposed dropping the table. `backend/alembic/env.py` now ignores that table
  (`include_name`), with a pytest test. The Python backend needs this commit before step 5 of the cutover.
* **Small fixes:** `migrate` refuses a SQLite path whose folder does not exist, with a clear message instead of an
  unhandled `SqliteException` (it never creates folders). The authentication handler's Information line for every
  anonymous request ("was not authenticated") is now logged only at Warning and above.

### 6.9 Python backend removed (2026-11-23)

* **Removed:** `backend/` (the FastAPI app, Alembic migrations, pytest suite and requirements); the scripts that
  imported it (`backfill_identities.py`, `copy_sqlite_to_postgres.py`, `scale_benchmark.py`, `pg_probe.py`); the
  parity and fixture generators in `backend-dotnet/tools/` (they ran the Python code); and the Python jobs in both CI
  pipelines. All of it remains in the git history. The fixtures they generated stay in `tests/ATS.Tests/Fixtures`.
* **Moved:** the local, git-ignored settings and data to `backend-dotnet/`: `.env` (an identical copy was already
  there), `.env.sqlite`, and `data/` (the SQLite database, backups, PostgreSQL dumps and the report logo). Relative
  paths in the settings resolve against the env file's folder, so they keep pointing at the same files.
* **Rewritten for .NET:** `scripts/setup.ps1`, `run-local-sqlite.ps1` and `test-postgres.ps1`; `smoke_test.py` now
  uses only the standard library. The frontend's default proxy target is port 8001 (`vite.config.ts`,
  `.env.example`). The README, the docs map and the architecture, rules, WFO, scaling, database and sample docs point
  at the .NET code. `docs/plans/` is left as history.
* **Still open:** `ef-baseline` on the real database (after a `pg_dump`, with a go-ahead), needed before a future EF
  migration. The API runs on the database without it. The C# doc comments that name the Python file a piece was
  ported from are kept as provenance.

---

## 7. Business logic inventory (port line by line)

### 7.1 Time
`today_local()` gives the office-local date of the UTC clock. `local_to_utc(date, time)` interprets a wall-clock time
in the office zone (DST gaps resolve with fold=0). `local_day_bounds(start, end)` gives UTC `[start 00:00, end+1 00:00)`.
.NET: `TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")` (IANA through ICU on Windows and Linux), plus an
`IClock` abstraction (with a fake clock in tests). DST-gap behaviour must be checked against zoneinfo for zones that
have DST (IST has none).

### 7.2 Scope and hierarchy (`services/scope.py`)
* `subtree_ids` is a **recursive CTE** using UNION, which also stops on cycles. EF: raw SQL `WITH RECURSIVE` (works
  on both providers).
* `can_view_employee`: self, ADMIN, or MANAGER with the employee in the subtree. A department head gets no extra
  access.
* `resolve_scope`: an EMPLOYEE gets 403. A MANAGER is limited to their subtree, and a `manager_id` inside it narrows
  the scope to that subtree (404 otherwise). An ADMIN with a `manager_id` gets that subtree (404 when the manager is
  unknown). Further filters are department, `is_active` unless `include_inactive`, `attendance_tracked` unless
  `tracked_only=False`, and search (name or code, LIKE on lower case). The result is ordered by `full_name, id` and
  can be limited with `limit`. Department and manager are loaded with `selectinload`, giving two IN queries.
* `manager_chain_ids` walks up the chain with cycle detection (used to stop circular reporting).

### 7.3 Attendance rules (`attendance_rules.py`, pure)
* `resolve_schedule`: chooses the highest (tier, effective_from, id), where employee = 3, department = 2 and
  organisation = 1, among the schedules effective on that date.
* `build_day_records(start..min(end, today))`, per day:
  * **Before joining, or no schedule:** NOT_SCHEDULED.
  * **Scheduled working day, not a holiday, no entry:** ABSENT (with the `ABSENT` exception) if `d < today` and a
    committed timesheet covers `d` for this person; otherwise NO_TIMESHEET (not expected).
  * **Holiday:** HOLIDAY. **Off day:** WEEKEND.
  * **OFFICE:** PRESENT. If `hours_known` and `d < today` and `min_present_minutes > 0` and `net < min`, the day is
    ABSENT with `BELOW_MINIMUM`. On an expected day, `LATE_ARRIVAL` when `first_in` is later than shift start (local,
    converted to UTC) plus grace, and `SHORT_HOURS` when the hours are known, `d < today` and `net < required`.
  * **WFH, LEAVE, HOLIDAY, WEEKOFF:** WFH (expected on a working day), LEAVE, HOLIDAY, WEEKEND.
  * `holiday_name` is shown whenever a schedule exists, even on a non-working day. The holiday map takes the
    **first** holiday per date (setdefault; organisation-wide or the employee's department).
* `summarize`: counters by status. `counted_days = expected + extra_present`;
  `attendance_percentage = round((present_expected + extra) * 100 / counted, 1)`;
  `average_net_minutes = round(net / hours_days)`; `variance = net − hours_required`.
* A per-computation memo shares schedule lookups per (department or employee, day) and late-after times per
  (schedule, day) across employees.

### 7.4 Attendance data loading (`attendance_data.py`)
* `timesheet_day`: turns an `attendance_daily` row into (work_mode, office minutes). A fast path is used when there
  is no override, no badge and no Wi-Fi minutes; otherwise `wfo_rules.classify_day`. A `last_out` before `first_in`
  is dropped.
* `load_timesheet`: a column projection (not entities). Chunks of 500 ids, or **one range scan when there are 2000 or
  more employees**.
* `coverage`: per employee, the merged periods `[period_start, period_end]` of **committed** uploads that list that
  employee in a valid row. Adjacent periods (a gap of at most one day) merge. `covered(d)` is a bisect over the
  periods.
* `latest_import_through` = the maximum `period_end` of committed uploads.
* `compute_records` uses an **in-process cache** (`record_cache.py`) when the scope has 25 or more people:
  * single flight: concurrent identical requests wait for one computation (up to 120 s);
  * LRU bounded at 1,000,000 day records, with a TTL of 300 s;
  * the key holds the people, range, today, schedules, holidays, policy, a coverage fingerprint (count, max id and
    max committed_at of committed uploads), a timesheet fingerprint (count, max updated_at and max id of
    `attendance_daily` in range) and a process-wide **write version**, bumped after any commit that touched
    `AttendanceDaily` or `AttendanceUpload`.

  .NET: `IMemoryCache` with a size limit plus a per-key `SemaphoreSlim`/`Lazy<Task<T>>`. The write version comes from
  a `SaveChangesInterceptor` (and from raw bulk writes). The Python code pauses the garbage collector during big
  builds; .NET does not need that (struct-friendly records, Server GC).

### 7.5 WFO classification and policy (`wfo_rules.py`, pure; `wfo.py`)
* **`classify_day` priority:**
  1. An override wins (all worked time goes to that mode).
  2. Office signals, in order: upload column OFFICE, badge, Wi-Fi, check-in. Office minutes = the **maximum** of the
     signals, never the sum. Conflict strings are written word for word (`"UPLOAD_COLUMN said WFH; office signal from BADGE"`).
  3. An upload mode of LEAVE/HOLIDAY/WEEKOFF (a conflict if minutes were worked).
  4. Upload WFH.
  5. Check-in WFH.
  6. Hours without an office signal give WFH (INFERRED).
  7. Otherwise HOLIDAY or WEEKOFF from the calendar, or no work mode.

  `hours_known` is false when no source gave hours.
* `day_status`: LEAVE, HOLIDAY or WEEKEND for those modes. PRESENT when hours are unknown but the day is OFFICE or
  WFH. Otherwise PRESENT at or above full-day minutes, HALF_DAY at or above half-day minutes (and above 0), and ABSENT
  below that. Stored in `attendance_daily.status`.
* **Policy:**
  * A **WFO day** is OFFICE and (`!hours_known` or office minutes ≥ `min_office_minutes`). The current value of
    `min_office_minutes` is 0.
  * A **working day** is a scheduled non-holiday day that is not LEAVE/HOLIDAY/WEEKOFF, plus any OFFICE/WFH day.
    Only days **before today** count.
  * `standard_days` = the scheduled days in the range.
  * The monthly requirement is calculated **per calendar month**:
    `min(done, round_half_up(min_per_month × done / standard))`, where `done` = the completed scheduled working days
    in that month inside the range. Months add up over a multi-month range.
  * Weeks run Monday to Sunday and are judged on all 7 days (classification is padded by 6 days at either end):
    `required = min(sized, round_half_up(min_per_week × sized / nominal))`. The status is UPCOMING, IN_PROGRESS,
    SHORT or MET.
  * `shortfall` = max(monthly shortfall, sum of weekly shortfalls). COMPLIANT means no shortfall of either kind.
  * `wfo_percentage = round(wfo × 100 / working, 1)`.
  * The band is MET when wfo ≥ required, NEAR within 3 days below, and BELOW beyond that.
* `classify_range` loads `attendance_daily` in chunks of 500 for range ±6 days. The calendar `scheduled(d)` resolves
  the schedule per date. The holiday map here is **last-wins** (a dict comprehension), unlike §7.3 (see P2).
* `store`/`refresh_days` write the classification back to `attendance_daily`, creating rows only for days with
  evidence.
* Team scope = the subtree plus the viewer (when tracked and active). Org rows are grouped by manager_id; groups are
  sorted with "No manager" last, then by name, and rows within a group by full name.
* **Overrides:** before and after snapshots go to the audit log (`WORK_MODE_OVERRIDDEN` /
  `WORK_MODE_OVERRIDE_CLEARED`, entity id `"<employee>:<date>"`).

### 7.6 Dashboards (`dashboard.py`)
* `_compute`: the records for `[min(start, on_date), max(end, on_date)]`, then derived values (period records, the
  on-date record, a summary and an exception count per person), which are themselves cached against the identity of
  the cached records.
* `aggregate` sums the summaries. `attendance_percentage = attendance_rate(present_expected + extra, expected + extra)`.
* Snapshot mapping: HOLIDAY, WEEKEND and NOT_SCHEDULED count as `off`.
* `attendance_bands`: the half-open intervals `[0,70) [70,80) [80,90) [90,95) [95,100.01)`.
* `employee_rows`: computes for the whole scope, then applies search and the status filter, sorts **in Python, stably**
  (sort keys in §8 P1), then pages.
* `bucketize`: day, week (Monday start; label `Wk <ISO week> (<dd Mon>)`) or month buckets. `end` is clipped to the
  range end; `start` is **not** clipped. A present day that is not expected adds 1 to both `expected` and
  `present_expected`. Average = net / attended (days with known hours).
* `exceptions`: counts per exception type over the whole scope. Matches are sorted by (date, name) **descending**,
  then paged.
* `manager_comparison`: the (id, manager_id) pairs give every manager's subtree in memory. Only non-EMPLOYEE managers
  are listed, sorted by name.
* `department_comparison`: every department plus "Unassigned" (only when it has members).
* `org.hierarchy`: department → roots (members whose manager is missing or in another department) → children in the
  same department, sorted by name, with cycle protection.

### 7.7 Month rows (`attendance_export.month_rows`)
One row per calendar month of the range, stopping at months that start after today. The WFO figures come from
`wfo.range_summary` per month and the attendance figures from `summarize`. `partial` means the range covers only part
of the month. `covered` is true when present + absent + WFH + leave > 0.

### 7.8 CSV reports (`reports.py`)
* The file starts with a UTF-8 BOM and lines end in CRLF, with minimal quoting (the `csv` module).
* `safe_cell` turns booleans into Yes/No and datetimes into office-local `YYYY-MM-DD HH:MM`. Text starting with
  `= + - @ \t \r \n` is prefixed with an apostrophe (formula-injection protection).
* Hours are written as `minutes/60` to 2 decimal places.
* Filenames: `attendance_<code>_<start>_<end>.csv`, `team_attendance_...`, `monthly_attendance_YYYY-MM.csv`,
  `department_attendance_...` and `attendance_exceptions_...`. The column headers are listed in the code and are
  ported word for word.
* The exceptions export uses page size 100,000.

### 7.9 Timesheet import (`timesheet_files.py`, `wfo_upload.py`, `core/wfo_mapping.py`, `config/wfo_mapping.json`)
* **Reading files:**
  * **CSV:** UTF-8 with BOM, falling back to cp1252 with replacement characters. The delimiter is whichever of
    `, ; \t |` is most frequent in the first 4096 characters; **ties go to the first** in that order.
  * **Excel:** `.xlsx`/`.xlsm` with openpyxl (values). `.xls` with xlrd, with Excel dates converted.
  * **Sheet choice:** a month-named sheet that has the employee and date columns, else the first usable sheet, or the
    one requested.
  * **PDF:** words with positions are grouped into rows by height, and into columns by the header's x-bounds. On top
    of that:
    * glued headers are split (`"Time InTime OutTotal Hrs"`, `"Sr No.Code"`);
    * a date glued to a name, and a glued ID and name (`"10002-ExtAsha Quill"`), are separated;
    * repeated headers on later pages and text outside the header columns (pivot tables) are skipped;
    * a scanned PDF gives 422.

  This is the hardest part to port: pdfminer's character grouping (LAParams) has to be approximated with PdfPig
  words. The plan is to port the algorithm onto PdfPig's `GetWords()` with letter-level re-grouping, and to verify it
  on every PDF fixture plus synthetic glued cases.
* **Header search:** the header is found in the first 20 rows. Headers are normalised (lower case, punctuation
  becomes spaces) and mapped through `wfo_mapping.json`. When an ID column and a plain "Employee" column both exist,
  "Employee" is the name.
* **Times from other sheets:** hours and in/out times are taken from another sheet ("TS Raw Data") when the chosen
  sheet has none, matched on (employee and day) or (name and day).
* **Values:**
  * Dates are read day-first in 9 `strptime` formats, plus Excel serial numbers and `datetime` values.
  * `DateReader`: a date that only makes sense month-first within the months the file covers is read swapped, with
    notes in the summary.
  * Times accept `HH:MM`, `HH:MM:SS`, `h:MM AM` and others; hours accept `HH:MM:SS`, decimals and similar, from 0 to
    24 hours.
  * An overnight shift (out ≤ in) puts the out time on the next day.
* **Matching people (`parse_rows`):**
  * By `ref_key` = the code or email in lower case with runs of spaces collapsed.
  * Otherwise by a **unique normalised full name**, using the name column or, when there is no name column, the ID
    text itself.
  * **Guessed joins:** a person added from a file has a placeholder email domain, and their joining date may move
    back.
  * Row errors are Python-formatted strings (see P5).
* **Preview:** stores the upload, its rows (bulk insert) and the summary, then commits and audits.

  Summary keys: `sheet, sheets, presence_list, presence_mode, unknown_employees[{employee_ref, name, other_names, rows, first_date, can_add, reason}], name_mismatches, matched_by_name[{employee_ref, name, app_code, app_name, rows}], hours_from, date_notes, blank_rows, columns, rows, valid_rows, error_rows, badge_rows, duplicate_rows, employee_days, employees, new_days, by_work_mode, office, wfh, office_below_minimum, conflict_count, conflicts[...≤200], protected_overrides, errors[{row, employee, error}≤200], date_from, date_to, joining_dates_moved[{employee_code, full_name, from, to}]`.
* **`create_employees`:**
  * Adds the unknown refs that can be added. The ID must match `CODE_PATTERN` and be 2–32 characters; an email or an
    invalid value cannot be added.
  * The new person gets the placeholder email `@imported.example.com` style (a unique email), a random password hash,
    the EMPLOYEE role and a joining date of the 1st of the month of their first date. The ID is kept **as written**.
  * The rows are then re-checked.
* **Commit:**
  * `aggregate` groups rows by (employee, date); `kept_minutes` keeps earlier hours when the file has none.
  * Upserts `attendance_daily`, skipping overridden days unless `overwrite_overrides` (which clears the override,
    audited).
  * Moves guessed joining dates back.
  * `timesheet_period(date_from, date_to, committed)` = (1st of the first month, max(min(end of the last month,
    committed − 1 day), date_to)).
  * Then `refresh_days`, audit and commit.

  `commit_summary`: `applied_days, created_days, skipped_overrides, overrides_overwritten, error_rows, joining_dates_moved, period_start, period_end`.

### 7.10 Device register (`devices.py`)
* MAC addresses are normalised to `aa:bb:cc:dd:ee:ff`. `is_random_mac` checks the locally administered bit.
* Serial numbers are stored upper-case; junk serials ("TO BE FILLED BY O.E.M." and similar) count as missing.
* Import: header aliases from the asset list or an Intune export, at most 5000 rows. Rows are matched by serial, then
  by MAC. **A blank cell never clears a value.** Each row gets an action (create, update, unchanged or error) with
  changes and problems. A dry run unless `apply`; audited.
* `register_summary` counts: devices, with MAC, without MAC, unassigned, random MAC, people tracked, people with a
  device, sites.

### 7.11 Administration rules (`admin_users.py`, `configuration.py`)
* **Users:**
  * Unique email and code; a weak password gives 422.
  * A manager must exist, be active and have the MANAGER or ADMIN role, and the change must not create a cycle.
  * You cannot change your own role or deactivate yourself, and the last active admin cannot be demoted or
    deactivated.
  * Before someone becomes EMPLOYEE or is deactivated, their direct reports must be reassigned. Deactivation revokes
    their sessions.
  * Audit actions: `USER_UPDATED`, `USER_ROLE_CHANGED`, `USER_MANAGER_ASSIGNED` (only when it is the sole change),
    `USER_ACTIVATED` / `USER_DEACTIVATED`.
  * A unique-key race is caught and mapped to 409 with "email" or "employee ID" wording.
* **Schedules:**
  * Only one target; distinct weekdays 1–7; start ≠ end.
  * Effective periods must not overlap for the same target (409 naming the other schedule).
* **Audit:** keys `password`, `password_hash`, `token`, `token_hash`, `csrf_token` and `secret` are stripped
  recursively. Enums are stored as values and dates as ISO strings. `changes(before, after)` gives only the changed
  keys.

### 7.12 Authentication (`auth_service.py`, `security.py`, `entra.py`)
* **Passwords:** argon2-cffi `PasswordHasher()` defaults (Argon2id, RFC 9106 low-memory profile: t=3,
  m=65536 KiB, p=4). Hashes are rehashed on login when the parameters change.
* **Timing:** an unknown email is still verified against a dummy hash, so response time does not reveal it.
* **Throttling:** too many recent failures (in `LOGIN_WINDOW_MINUTES`) give 429 with `Retry-After = window × 60`.
  The limits are `LOGIN_MAX_FAILURES` per email and `LOGIN_IP_MAX_FAILURES` per IP. Every attempt is stored in
  `login_attempts`.
* **Client IP:** `X-Forwarded-For` is used only when `TRUST_PROXY_HEADERS` is on.
* **Sessions:**
  * The token is `token_urlsafe(32)` and the database keeps `HMAC-SHA256(SECRET_KEY, token)` as hex.
  * The CSRF token is also `token_urlsafe(32)`.
  * Expiry: absolute `SESSION_ABSOLUTE_HOURS`, idle `SESSION_IDLE_MINUTES`. `last_seen_at` is written at most once
    per minute.
* **Cookies:**
  * `ats_session` is HttpOnly and `ats_csrf` is readable by the SPA. Both use path `/`, `max-age` = absolute hours,
    `SameSite` = `COOKIE_SAMESITE`, `Secure` = `COOKIE_SECURE` (defaulting to production only) and domain =
    `COOKIE_DOMAIN`.
  * Logout deletes both cookies with the same attributes.
* **CSRF check:** a constant-time comparison of the header with `session.csrf_token` on POST, PUT, PATCH and DELETE.
* **Entra ID:**
  * Authorization code flow with PKCE (S256). The flow state lives in an HMAC-signed cookie `ats_oidc` (key
    `sha256("entra-flow:" + SECRET_KEY)`, base64url payload `.` MAC, `exp` after 600 s, path `/api/auth/entra`,
    SameSite=Lax).
  * `next` is restricted to same-site relative paths.
  * ID-token checks: RS256 with the tenant's JWKS, issuer `<authority>/<tenant>/v2.0`, audience = client id, 60 s
    leeway, required claims, nonce, `tid` and `oid`.
  * Account matching: by `graph_user_id` (lower case), otherwise by email or preferred_username, after which the
    object id is linked (audited `USER_IDENTITY_LINKED`). A different existing link gives `account_mismatch`. Nobody
    is ever created automatically.
* **Production guards** (refuse to start):
  * `SECRET_KEY` shorter than 32 characters or containing an insecure marker;
  * `COOKIE_SECURE=false`;
  * a wildcard in CORS;
  * `DEV_LOGIN_ENABLED`;
  * an Entra redirect URI that is not https;
  * no sign-in method enabled;
  * Entra settings that are only partly filled in;
  * `SameSite=None` without secure cookies.

### 7.13 Exports
* **xlsx (employee):** sheets with the summary facts, the months and the days, branded with `REPORT_*` colours.
  Formula-risky text is escaped.
* **PDF (employee):** a coloured header band with an optional logo (`REPORT_LOGO_FILE`), fact tiles, a month table
  and a day table. Built with reportlab today. The port reproduces the layout and the content, **not byte-identical
  files**. Parity is checked on the text content (extracted) and the structure.
* **WFO xlsx (org):** sheets by manager group with a header fill of `E8EDF5`, using the same escaping.

### 7.14 CLI (`app/cli.py`)
* `seed`: fictional demo data, development only.
* `create-admin`: prompts for a password.
* `wfo-refresh --start --end`: recomputes the stored work modes.

Port as `dotnet ATS.Api.dll seed | create-admin | wfo-refresh`, using the same DI container and refusing to run in
production where Python refuses.

---

## 8. Parity quirks to keep (each gets a `// TODO(parity)` in the C#)

| # | Behaviour (looks like a bug or an inconsistency) | Where |
|---|---|---|
| P1 | `/dashboard/employees?sort=check_in` is accepted by the route, but the service has no `check_in` key (its key is `first_in`, which the route rejects), so `check_in` silently sorts **by name**. | `routes/dashboard.py` SortKey vs `services/dashboard.py` SORT_KEYS |
| P2 | Two holidays on the same date (organisation-wide and department): the attendance rules take the **first** name, the WFO calendar takes the **last**. | `attendance_rules.build_day_records` vs `wfo.classify_range` |
| P3 | `GET /policy` writes: it creates the singleton policy row when it is missing and commits. | `routes/admin.py read_policy` |
| P4 | Trend buckets clip `end` to the range but not `start`, so a week bucket can start before the requested start date. | `dashboard._bucket_key` / `bucketize` |
| P5 | Row error messages embed Python `repr()` of strings (`unknown employee '10001' (not in the app yet)`, single-quoted with Python escaping). These are stored in the database and shown in the UI; .NET needs a `PyRepr()` helper. | `wfo_upload.parse_rows`, `timesheet_files`, `devices` |
| P6 | Employee endpoints answer 404 for out-of-scope people, but WFO endpoints answer 403 (deliberate per spec, but inconsistent). | `scope.py` vs `wfo.scoped_employee` |
| P7 | `/departments` runs one COUNT query per department (N+1). Kept as is for parity of results; the .NET version may use one grouped query, since the output is identical. | `routes/admin._dept_out` |
| P8 | Sorting and lower-casing use Python semantics: `str.lower()` (full Unicode) and code-point ordering. .NET must use `ToLowerInvariant()` and `StringComparer.Ordinal`, **not** culture comparison. `List.Sort` is unstable, so use LINQ `OrderBy` (stable). | `dashboard.SORT_KEYS`, org grouping, conflict and error sorts |
| P9 | Month and week labels use `strftime("%b")`, which depends on the locale (English on every current host). .NET must use `CultureInfo.InvariantCulture`. | labels in trends, WFO weeks and month rows |
| P10 | The `employee_trends` response always has `headcount: 1`. | `routes/attendance.py` |
| P11 | Storage only: SQLAlchemy writes a `None` it was given in a JSON column as the JSON text `'null'` (the audit log's before/after values), but a column never given as SQL NULL. .NET reads both as null and writes SQL NULL (EF's null-converting converters are an internal API). Both backends read either form as null, so the API output is identical. | `services/audit.py`, `persistence/ValueConversions.cs` |
| P12 | Python answers 500 where .NET answers a row problem, 422 or keeps the value (kept kinder on purpose, marked `TODO(parity)`): an hours cell of `inf` (OverflowError), a CSV with a lone carriage return inside a record (`csv.Error`), a workbook whose shared-string index is out of range or that has a chart sheet, and a schedule PATCH that sends a required field (name, times, minutes, working days, start date) as null. | `wfo_upload.parse_hours`, `timesheet_files._csv_rows`, `_xlsx_sheets`, `configuration.update_schedule` |
| P13 | A request whose path **and** JSON body are both invalid (e.g. `PUT /admin/attendance/x/2026-02-30/work-mode` with a bad body): FastAPI lists the path and the body problems together; .NET answers with the body problems only (request bodies are validated before the action runs). Status and the body messages are the same. | `ValidationFilter` |
| P14 | `PATCH /admin/devices/{id}` answers with the owner the device had **before** the change: Python loads `Device.user` with the device (`lazy="joined"`) and its session does not expire objects on commit, so changing or removing the owner returns the old one. The database (and the next read) has the new owner. | `routes/devices.update_device` |

---

## 9. Python → .NET library mapping

| Python | Purpose | .NET | In the target stack? |
|---|---|---|---|
| FastAPI / Starlette / uvicorn | Web API, routing, middleware, CORS | ASP.NET Core (controllers), Kestrel | ✅ |
| Pydantic / pydantic-settings / email-validator | DTOs, validation, settings | records + **FluentValidation**; `IOptions<T>` with appsettings and env vars; `MailAddress`-based check matching email-validator's rules | ✅ |
| SQLAlchemy 2 + psycopg 3 | ORM | **EF Core** + Npgsql.EntityFrameworkCore.PostgreSQL + Microsoft.EntityFrameworkCore.Sqlite | ✅ |
| Alembic | Migrations | EF Core migrations (§6) | ✅ |
| argon2-cffi | Argon2id PHC hashes | **Isopoh.Cryptography.Argon2** (reads and writes `$argon2id$v=19$m=…,t=…,p=…$salt$hash`) | ❌ approval needed |
| hmac / hashlib / secrets | Tokens, HMAC | `System.Security.Cryptography` (`RandomNumberGenerator`, `HMACSHA256`, `CryptographicOperations.FixedTimeEquals`); base64url via `Base64Url` (.NET 9+) or a helper | ✅ built in |
| PyJWT[crypto] | Entra ID token validation | Microsoft.IdentityModel.Protocols.OpenIdConnect + Microsoft.IdentityModel.JsonWebTokens (they come with JwtBearer) | ✅ (via JwtBearer) |
| zoneinfo / tzdata | Office timezone | `TimeZoneInfo` (IANA through ICU) | ✅ built in |
| openpyxl | Read and write xlsx | **ClosedXML** | ✅ |
| xlrd | Read `.xls` | **ExcelDataReader** (+ ExcelDataReader.DataSet) | ❌ approval needed |
| pdfminer.six | PDF words with positions | **UglyToad.PdfPig** | ❌ approval needed |
| reportlab + Pillow | PDF export with logo | **QuestPDF** (license, D5), or PdfSharp/MigraDoc (MIT) | ✅ / license decision |
| csv | CSV read and write | `Microsoft.VisualBasic.FileIO.TextFieldParser` (in the box) for reading, a small writer for writing | ✅ built in |
| `bytes.decode("cp1252")` | Encoding fallback | `CodePagesEncodingProvider` (in the box) | ✅ built in |
| logging | Logs | **Serilog** (Serilog.AspNetCore, Sinks.Console) | ✅ |
| FastAPI OpenAPI | Docs | **Swashbuckle.AspNetCore** (served at `/api/docs`, `/api/openapi.json`) | ✅ |
| pytest + httpx TestClient | Tests | **xUnit**, **FluentAssertions 7.x** (D5), `WebApplicationFactory`, **Testcontainers.PostgreSql** | ✅ |
| (none) | Background jobs | Not needed (D3) | – |
| (none) | Mapping | Manual mapping (D5) | – |

Packages to approve: **Isopoh.Cryptography.Argon2, UglyToad.PdfPig, ExcelDataReader (+ .DataSet)**. Optional:
PdfSharp/MigraDoc instead of QuestPDF, and AwesomeAssertions instead of FluentAssertions.

---

## 10. Proposed .NET design

* **Solution:** `backend-dotnet/ATS.sln` with the projects you specified: `src/ATS.Api`, `src/ATS.Application`,
  `src/ATS.Domain`, `src/ATS.Infrastructure` and `tests/ATS.Tests`.
* **ATS.Domain:** entities, enums, and the **pure rule engines** (attendance rules, WFO rules), with no EF
  references, so they port line by line and are tested like the Python unit tests.
* **ATS.Application:** services (scope, attendance, WFO, dashboard, reports, uploads, devices, admin, auth),
  DTOs, validators, the `IClock`, and the Python-compatibility helpers (`PyMath.Round`, `RoundHalfUp`, `PyRepr`,
  date and time parsing).
* **ATS.Infrastructure:** `AtsDbContext` (with the provider chosen from `DATABASE_URL`: `postgresql…` or `sqlite…`),
  the migrations, value converters, raw SQL (recursive CTE), the file readers (ClosedXML, ExcelDataReader, PdfPig),
  the exporters, the password hasher, the Entra client and the record cache.
* **ATS.Api:**
  * thin controllers;
  * middleware for the request ID, security headers and error envelope;
  * a `SessionCookieAuthenticationHandler` plus a CSRF filter;
  * the policies `User`, `ManagerOrAdmin` and `Admin`;
  * a custom validation-problem → envelope factory;
  * JSON options (snake_case, string enums, the custom date/time converters).
* **Async everywhere** with a `CancellationToken`. Bulk reads use `AsNoTracking()` projections. Large scopes use the
  same chunk/range-scan thresholds (500 and 2000) as Python.
* **Config:** `appsettings.json` holds non-secret defaults only. Every `.env` key keeps its **exact name** as an
  environment variable (`DATABASE_URL`, `SECRET_KEY`, ...), mapped explicitly onto an options class, so the existing
  `.env` values work unchanged. A local `.env` is loaded only when `ATS_ENV_FILE` is set or `backend-dotnet/.env`
  exists (gitignored). The same startup validation as §7.12 applies, with no default database.

---

## 11. Parity testing strategy

* **Same database, same secret:** both backends point at one database copy (PostgreSQL `ats_parity`, restored from a
  `pg_dump` of `ats_db`) with the same `SECRET_KEY`. A session cookie created by either backend then works on both.
* **Script:** `scripts/parity/compare.py` (Python, because it ships next to the existing backend; httpx).
  1. Sign in once (password or dev login) as an admin, a manager and an employee.
  2. Replay a request matrix against `:8000` and `:8001`. The matrix covers every GET endpoint × each role × a set
     of ranges (single month, multi-month, a partial month, today, a holiday month), sort and order options, paging,
     filters, `granularity`, `month`, and error cases (out of scope, a bad date, a bad enum, missing CSRF).
  3. Compare status code, the selected headers (`Content-Type`, `Content-Disposition`, `Cache-Control`) and the JSON
     body.
* **Comparison rules:**
  * Numbers by value.
  * Datetimes exact (string).
  * Arrays in order.
  * `generated_at`, `server_time` and `request_id` excluded.
  * Error `message` text reported but not failing (D9).
* **Files:**
  * CSV byte-for-byte.
  * xlsx cell by cell (values and sheet names).
  * PDF: extracted text, compared line by line.
* **Writes** (uploads, overrides, admin CRUD) run against **two separate copies** of the same snapshot in the same
  order; the resulting tables are then diffed, ignoring ids, timestamps and audit request ids.
* **Rule-engine parity:** fixtures generated by running the Python `build_day_records` / `classify_day` /
  `summarize_wfo` over randomised inputs, stored as JSON and asserted in xUnit. This catches rounding and calendar
  differences without HTTP.
* **Public repo:** parity fixtures are synthetic. Real `ats_db` data is only used locally and never committed.

---

## 12. Phase plan (adjusted)

| Phase | Content | Notes |
|---|---|---|
| 1 | Scaffold (`dotnet new sln/webapi/classlib/xunit`), configuration loading and validation, Serilog, Swagger at `/api/docs`, request-ID, security-header and error-envelope middleware, `/api/health` and `/api/health/db` | |
| 2 | Entities, `AtsDbContext`, converters, baseline migration, the `ef-baseline` command for existing databases, the schema-equivalence test (PostgreSQL and SQLite) | |
| 3 | Session-cookie authentication, CSRF, throttling, Argon2, login, logout, me, providers, change-password, dev-login, Entra OIDC, the role and scope helpers | Depends on D2 and D4 |
| 4 | Attendance read endpoints, the rule engines, coverage, the record cache, dashboards, org and team, WFO reads, holidays, departments, policy | The bulk of the logic |
| 5 | Timesheet import: file readers, parse, preview, create-employees, commit, discard; overrides; WFO policy | PdfPig (D4) |
| 6 | Office sites, devices, device import, auto-attendance settings, admin CRUD (users, departments, schedules, holidays, audit log); CLI verbs | No background jobs (D3) |
| 7 | CSV, xlsx and PDF exports; the WFO xlsx | Depends on D5 |
| 8 | Port the remaining pytest cases to xUnit (the 253 test functions → equivalent coverage), Testcontainers PostgreSQL, the parity script, `azure-pipelines.yml` (PostgreSQL 16 service) | |
| 9 | Dockerfile (multi-stage, ICU and tzdata present), README, docs, cutover checklist (backup, `ef-baseline`, switch the Vite proxy, smoke test, rollback) | |

The steps in every phase are: implement, `dotnet build -warnaserror`, `dotnet test`, commit to
`feature/dotnet-backend`, and report (files, how to run, test results, deviations). Then I wait for your OK.

---

## 13. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| PDF table reading differs between pdfminer and PdfPig (word grouping, glyph gaps) | Wrong or missing timesheet rows | Port the algorithm onto letter positions; run a golden test on every PDF layout seen so far (synthetic copies) and on the real files locally; parity-diff the preview summaries |
| Rounding differences (`round`, `round_half_up`) | Off-by-0.1 percentages, off-by-one required days | `PyMath` helpers, generated parity fixtures |
| Date and time formats (6 vs 7 fraction digits, `Z`) | Frontend parsing is fine, but the parity diffs fail | Custom converters (§4) |
| SQLite datetime text format written by EF | Mixed formats in a shared dev file; text comparisons in SQL | SQLAlchemy-format converters (§6.5) |
| Expression and partial indexes, exclusion constraint, `json` vs `jsonb`, SERIAL | Schema drift; a fresh database differs from Alembic's | Raw SQL in the migration plus the schema-equivalence CI test |
| Python-specific behaviour (stable sort, Unicode lower, repr, strptime) | Small ordering or message differences | P5, P8 and P9 helpers; parity matrix |
| Performance with thousands of employees (the Python code has range scans, chunking, a single-flight cache and GC tuning) | Slow dashboards | Port the same thresholds and cache; reuse the benchmark scenario in `scripts/scale_benchmark.py` against both backends |
| .NET 8 out of support (D1) | No security patches | Target .NET 10 |
| Licensing (QuestPDF, FluentAssertions, AutoMapper) | Legal exposure | D5 |
| Two backends writing to one database during parity runs | Corrupted comparisons | Separate snapshots for write tests; read-only matrix on a shared copy |
| Entra ID untested end to end (no tenant yet) | Sign-in regressions at go-live | Port the Python Entra tests, which mock the token endpoint and the JWKS; manual test with IT before cutover |

---

## 14. Open questions

1. **D1:** target .NET 10 LTS instead of .NET 8? (Recommended.)
2. **D2:** keep the session cookie and CSRF (needed for no frontend changes) rather than JWT bearer? (Recommended.)
3. **D3:** confirm that Phase 6 has no background jobs, since none exist today. Or do you want the planned Wi-Fi
   collector built in .NET as **new** scope after parity?
4. **D4:** approve Isopoh.Cryptography.Argon2, UglyToad.PdfPig and ExcelDataReader?
5. **D5:** QuestPDF with a commercial license, or PdfSharp/MigraDoc? FluentAssertions 7.x or AwesomeAssertions?
   Manual mapping instead of AutoMapper?
6. **D9:** is "same status, code and field; message wording best effort" acceptable for validation errors?
7. Keep the GitHub Actions workflow alongside `azure-pipelines.yml` until cutover?
8. Should the .NET backend also read the existing `backend/.env` / `.env.sqlite` files directly (same keys), or only
   its own gitignored `backend-dotnet/.env` plus environment variables?
9. Can the parity runs use a local copy of `ats_db` (a `pg_dump` restored into `ats_parity` on the same server)? It
   never leaves the machine and is never committed.
