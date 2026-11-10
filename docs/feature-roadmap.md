# Feature roadmap: standalone attendance system

Source: *Must-Have Features: Organization Attendance Tracking System* (4 pages).
We build **one feature at a time**. Each feature is agreed, built, verified and merged before the next one starts.

Status: `[x]` done · `[~]` partly there · `[ ]` not started · `⏸` deliberately deferred

---

## How every feature is built (the same 8 steps, every time)

| Step | What happens | You see |
|---|---|---|
| 1. Agree | I list the decisions the feature needs (rules, wording, limits). You answer. Nothing is coded before this. | A short decision list |
| 2. Database | An **additive** Alembic migration (nothing dropped or rewritten), tested up → down → up on SQLite and on a copy of the dev data. PostgreSQL tests written alongside. | Tables / columns added and why |
| 3. Backend | Rules in a pure, unit-tested service module, then API endpoints with Pydantic schemas, permission checks and audit-log entries. | New endpoints and rules |
| 4. Access | Who may see / do what (Employee, Manager, HR, Admin), enforced on the server, tested per role. | A permission table |
| 5. Frontend | Screens built from the existing components and colour tokens, with loading, empty and error states, light and dark mode, mobile layout. | Screenshots |
| 6. Tests | Unit tests for the rules, API tests for each role, UI tests for the main interaction. Every bug found gets a test first. | Test counts |
| 7. Verify | Full backend and frontend suites, type-check, production build, a run against the dev database. | Pass/fail report |
| 8. Ship | Docs updated, this checklist ticked, commit on a feature branch, push, pull request into `main`. | PR link |

Rules that apply throughout: server time only; nothing overwrites history (changes are new records + audit);
manager scope is enforced on the server; new settings default to "off" or the current behaviour;
I ask before any destructive database change or any new dependency that needs approval.

---

## Gap check (verified against the code, 2026-11-05)

| Area | Requirement | Current build |
|---|---|---|
| 1 Capture | Server-side timestamps | [x] |
| | Multiple sessions per day | [x] |
| | **Work location tag (Office / WFH / On-site)** | [ ] not on check-ins |
| | **Missed check-out auto-close** | [~] detected and flagged; no scheduled auto-close |
| | Duplicate / overlap prevention | [x] (verified on PostgreSQL 16 in CI) |
| | Timezone-correct days | [x] |
| 2 Rules | Schedules (employee / department / org) | [x] |
| | Holidays and weekly offs | [x] |
| | Late with grace period | [x] |
| | **Early check-out and half-day rules** | [ ] |
| | **Overtime calculation** | [ ] |
| | Net vs elapsed hours | [x] breaks and multiple sessions |
| 3 Leave | Removed on 2026-11-05 by decision: attendance is office presence measured against shift hours | n/a |
| 4 Corrections | Request, approve/reject, original kept | [x] |
| | Cutoff window | [~] configurable window (default 30 days); **payroll-lock cutoff missing** |
| 5 Roles | Employee, Manager, Admin + manager scope | [x] |
| | **HR role** | [ ] |
| 6 Reports | Daily / monthly / per-employee, dashboards | [x] CSV |
| | Late / absent summaries | [~] exceptions report; **no overtime summary** |
| | **Excel and PDF export** | [ ] |
| | **Payroll-ready monthly summary with lock** | [ ] |
| 7 Security | **SSO (Entra ID) + MFA via SSO** | [ ] local email/password login |
| | CSRF, sessions, login throttling | [x] |
| | Audit log (who, what, when, old → new) | [x] |
| | **Data retention + privacy notice** | [ ] |
| | **Backups and restore** | [ ] |
| 8 Notifications | **Missed check-in/out reminders, approval alerts, email/Teams** | [ ] |
| 9 Admin | **Employee import (CSV)** | [ ] (deactivation exists) |
| | Departments, managers, reporting lines | [x] |
| | Policy settings | [~] grace, minimums, windows; **no shift rules / retention settings** |
| | **Payroll lock** | [ ] |
| 10 Non-functional | Responsive, icon + label, health checks, tests + migrations | [x] |
| | Fast dashboards | [x] 5,000 employees: admin page 1.3–4.4 s, manager page < 0.6 s (see docs/scaling.md) |
| | Error logging | [~] server logs only; no error tracking |

---

## The order we will work in

Earlier features unlock later ones (the HR role is needed before HR-only screens; the background worker is needed
before auto-close and notifications; the payroll lock is needed before the payroll summary).

| # | Feature | Why this position | Needs from outside |
|---|---|---|---|
| F0 ✅ | Quality gate: CI on GitHub with PostgreSQL | Every later feature gets tested on the real database automatically | — |
| F1 | HR role | Later screens (balances, payroll, reports) are HR's | — |
| F2 | Work location on check-in | Small, visible, used by reports later | — |
| F3 | Early check-out and half-day rules | Changes day status; overtime and payroll build on it | HR rules |
| F4 | Overtime calculation | Needed for payroll summary | HR rules |
| ~~F5~~ | ~~Leave balances, accrual, half-day and hourly leave~~ | Dropped: leave removed from the product | — |
| F6 | Background worker + missed check-out auto-close | Worker is reused by notifications and retention | — |
| F7 | Payroll lock for closed periods | Freezes a month before payroll reports | — |
| F8 | Reports: Excel / PDF, overtime and payroll summary | Uses F3–F7 | approval for 2 libraries |
| F9 | Employee CSV import | Onboarding a real organisation | — |
| F10 | Notifications (in-app, then email, then Teams) | Uses worker (F6) | SMTP server / Teams webhook |
| F11 | Single sign-on with Entra ID | Corporate identity, MFA | IT app registration + admin consent |
| F12 | Data retention + privacy notice | DPDP Act 2023 | HR / Legal sign-off |
| F13 | Backups and restore | Attendance is a legal record | PostgreSQL server |
| F14 | Non-functional hardening | Accessibility tests, error tracking, performance | error-tracking account (optional) |

---

## Feature details

### F0 — Quality gate (CI with PostgreSQL)  [x] done 2026-11-05
- **Build:** GitHub Actions workflow: backend tests on PostgreSQL 16 with `--require-postgres`, frontend tests,
  type-check, build, `alembic upgrade head` + `alembic check`, on every push and pull request.
- **Why first:** the 26 PostgreSQL tests have never run; CI runs them on GitHub's servers, not this laptop.
- **Done when:** the workflow is green on a pull request, with 0 skipped tests.

### F1 — HR role  [ ]
- **Decisions:** what HR can do vs Admin. Proposal: **HR** = org-wide attendance and correction approvals,
  reports, holidays, schedules, policies, payroll lock. **Admin** = user accounts, roles, departments,
  system settings, audit log. Can HR see the audit log? Can HR create users?
- **DB:** add `HR` to the role values (constraint widening, like migration 0002).
- **Backend:** a permission map (one place that says which role can do what) replacing scattered role checks;
  HR scope = whole organisation for people features, no account/role management.
- **Frontend:** sidebar sections per permission; HR sees "People & policies" without "Users / Audit".
- **Tests:** role × endpoint matrix for all ~57 endpoints.
- **Done when:** an HR user can do exactly the agreed list and gets 403 everywhere else.

### F2 — Work location on check-in  [ ]
- **Decisions:** values (Office / WFH / On-site; site name for On-site?), required or default Office,
  can a correction change it?
- **DB:** `attendance_sessions.location` (+ optional `site_note`), nullable for old rows.
- **Backend:** accept location on check-in; include in day records, history, reports; filter in team table.
- **Frontend:** location picker on the Check-in button; location shown in history, calendar day details, team table.
- **Done when:** a check-in records its location and it shows everywhere attendance is shown.

### F3 — Early check-out and half-day rules  [ ]
- **Decisions:** early check-out = last check-out earlier than shift end minus grace? Half day = net hours below X
  (e.g. 4 h) but above the absent threshold? Do repeated lates convert to a half day (e.g. 3 lates = ½ day)?
- **DB:** policy columns (`early_leave_grace_minutes`, `half_day_min_minutes`, optional late-conversion rule).
- **Backend:** new day status `HALF_DAY` and exception `EARLY_LEAVE` in the rules engine (single source of truth);
  attendance % counts a half day as 0.5.
- **Frontend:** new status badge (icon + label), policy settings, calendar and history updates.
- **Done when:** unit tests cover every boundary (exactly at grace, exactly at threshold, overnight shifts).

### F4 — Overtime  [ ]
- **Decisions:** daily (beyond required hours + threshold) and/or weekly? Do weekend/holiday hours count as overtime?
  Does overtime need manager approval before it counts for payroll?
- **DB:** policy columns; if approval is needed, an `overtime_approvals` table.
- **Backend:** overtime minutes per day and per period in the rules engine; approval endpoints if chosen.
- **Frontend:** overtime card and column, approval list if chosen.
- **Done when:** overtime appears in summaries and reports and matches hand-calculated examples.

### F5 — Leave  [dropped 2026-11-05]
- Leave was removed from the product: no applying, approving or recording leave. Attendance is office
  presence (check-in / check-out) measured against the shift's required hours.
- The old `leave_records` table is still in the database, unused, until a migration to drop it is approved.

### F6 — Background worker + missed check-out auto-close  [ ]
- **Decisions:** when to auto-close (at shift end, or at maximum session length)? Should an auto-closed session
  count hours, or stay "needs correction" (recommended: closed at shift end, flagged for review, hours not
  credited until the employee confirms)?
- **DB:** reuses `worker_jobs` (already created in 0002) for job health; session status `AUTO_CLOSED`.
- **Backend:** `python -m app.worker`, one loop per job, PostgreSQL advisory locks (one instance per job),
  back-off on errors; auto-close job every 15 minutes.
- **Frontend:** "auto-closed — please confirm" prompt for the employee; job health on the admin page.
- **Done when:** a forgotten session is closed automatically and visible to the employee and manager.

### F7 — Payroll lock  [ ]
- **Decisions:** who locks (HR); lock by calendar month; unlock allowed with a reason?
- **DB:** `payroll_periods` (month, locked_at, locked_by, unlock history).
- **Backend:** every write that touches a locked date (check-in edits, corrections, schedule and
  holiday changes affecting it) is refused with a clear message; corrections cutoff = earlier of window or lock.
- **Frontend:** HR "Payroll periods" screen; lock badges on calendars and requests.
- **Done when:** nothing can change attendance inside a locked month, and every lock/unlock is audited.

### F8 — Reports: Excel / PDF, overtime and payroll summary  [ ]
- **Decisions:** payroll summary columns (present, half days, absent days, shift hours vs worked hours, overtime,
  late count…). Approval for two Python libraries: `openpyxl` (Excel) and `reportlab` (PDF).
- **Backend:** one report builder, three outputs (CSV, Excel, PDF); payroll summary only for locked months.
- **Frontend:** format picker on the Reports page.
- **Done when:** the payroll summary for a locked month downloads in all three formats with identical numbers.

### F9 — Employee CSV import  [ ]
- **Decisions:** columns; create-only or create-and-update; how new people sign in (temporary password now,
  SSO after F11).
- **Backend:** validate-everything-first preview (like the identity backfill), then apply with audit.
- **Frontend:** upload → preview with errors per row → confirm.
- **Done when:** a 200-row file imports with a correct preview and zero partial imports on error.

### F10 — Notifications  [ ]
- **Decisions:** channels (in-app first; email needs an SMTP server; Teams needs a webhook or Graph);
  which events (missed check-in/out, request submitted, decision made, pending for > N days); quiet hours.
- **DB:** `notifications` (in-app inbox), per-user preferences.
- **Backend:** notification service + worker job for reminders; email via SMTP (standard library).
- **Frontend:** bell icon with unread count and inbox; preference settings.
- **Done when:** each event produces exactly one notification per recipient, with tests for every event.

### F11 — Single sign-on with Entra ID  [ ]
- **Needs IT:** app registration, redirect URL, admin consent. MFA is then enforced by Entra (Conditional Access).
- **Decisions:** match users by email or Entra object id; keep one local break-glass admin login.
- **Backend:** OpenID Connect login (authorization code + PKCE), same server-side session cookie afterwards.
- **Frontend:** "Sign in with Microsoft" button; local form only for break-glass.
- **Done when:** staff sign in with their work account; unknown accounts are refused; everything else unchanged.

### F12 — Data retention + privacy notice  [ ]
- **Needs HR / Legal:** privacy notice text and retention periods (attendance, audit, login attempts).
- **Backend:** retention settings and purge job (worker); individual data export on request.
- **Frontend:** privacy notice page linked from sign-in; retention settings for Admin.
- **Done when:** purge runs on schedule, is audited, and never touches locked payroll months.

### F13 — Backups and restore  [ ]
- **Needs:** the PostgreSQL server (and its storage) from IT.
- **Build:** scheduled `pg_dump` with retention, a restore script, a documented restore drill; a SQLite backup
  script for local development.
- **Done when:** a backup has been restored into a fresh database and the app works against it.

### F14 — Non-functional hardening  [ ]
- Accessibility tests (axe) on every page; error tracking for backend and frontend; load test done (docs/scaling.md);
  pre-aggregated daily totals only if year-long whole-organisation ranges become a regular need; smaller frontend bundle.

---

## After day one (deferred)
- Automatic attendance (Wi-Fi / Teams): in progress, phase by phase; checklist in `TODO.md` (Phase 1 done)
- ⏸ Geofencing or QR check-in
- ⏸ Biometric / kiosk integration
- ⏸ Payroll / HRMS API integration
- ⏸ Shift rosters and rotations
- ⏸ Mobile app

## Progress log
| Date | Feature | Result |
|---|---|---|
| 2026-11-05 | Roadmap written and gap check done | — |
| 2026-11-05 | F0 CI on PostgreSQL 16 | Green: migrations up/down/up + drift check, full backend suite incl. 26 PostgreSQL integration tests, frontend tests/type-check/build. First run exposed 2 test bugs (fixed). [run](https://github.com/Lokiran/Attendance-Tracking-System/actions/runs/41979671735) |
