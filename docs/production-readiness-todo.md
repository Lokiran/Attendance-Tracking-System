# Production readiness checklist

Goal: take the Office Attendance Tracker from a well-tested prototype to something real employees
can rely on, with bugs caught before users see them.

Priorities: **P0** = before anyone uses it for real · **P1** = before a pilot team ·
**P2** = before company-wide rollout · **P3** = after launch.
Status: `[x]` done · `[ ]` open · `[~]` blocked (reason given). Each item has a "done when" test.

## Where we are (2026-11-05)

| Area | State |
|---|---|
| Features | Employee / manager / admin dashboards, check-in/out, corrections, reports, audit — working |
| Backend tests | 161 passed on SQLite; **26 PostgreSQL-only tests never run** |
| Frontend tests | 21 passed (one test file); type-check and build clean |
| Database | Verified on SQLite only; PostgreSQL install blocked on this laptop (download 403, no admin) |
| Delivery | Not a git repository; no CI; no Dockerfile; no staging environment |
| Auto attendance | Phase 1 (foundation: schema, settings, Entra sign-in) done; Phases 2–5 open (see `TODO.md`); off by default |

---

## P0 — Foundations (nothing else is trustworthy without these)

### Version control and review
- [x] Git initialised and pushed to GitHub (repository is currently public; no secrets committed).
      *Done when:* the repo has history and a protected `main` branch.
- [ ] Branch protection: pull requests only, one reviewer, CI must pass.
      *Done when:* a direct push to `main` is rejected.

### Continuous integration (the main defence against regressions)
- [x] CI pipeline running on every push and pull request (`.github/workflows/ci.yml`, green 2026-11-05):
      backend tests **on PostgreSQL 16** (service container) with `--require-postgres`,
      frontend tests, type-check, production build, `alembic upgrade head` + `alembic check`.
      *Done when:* all 187 backend tests (incl. the 26 PostgreSQL ones) pass in CI.
- [ ] Linters and formatters as blocking checks: `ruff` (Python), `eslint` (TypeScript — currently
      not installed even though the code has eslint comments), `prettier`.
      *Done when:* CI fails on a lint error; the codebase is clean.
- [ ] Static typing for the backend: `mypy` on `app/` (start non-strict, tighten module by module).
- [ ] Coverage reporting with a floor (backend ≥ 85 %, rules engine and permissions ≥ 95 %;
      frontend ≥ 70 %) that can only go up.
- [ ] Dependency and secret scanning (`pip-audit`, `npm audit`, a secret scanner).
      *Done when:* CI blocks known-vulnerable dependencies and committed secrets.

### Verify PostgreSQL
- [x] Verified on PostgreSQL 16 in CI (GitHub Actions service container), 2026-11-05: 0 PostgreSQL tests
      skipped; migrations 0001→0002 up/down/up and drift check pass. PostgreSQL is still unavailable on the
      laptop, so CI is the PostgreSQL gate (`scripts\test-postgres.ps1` remains for an approved server).

### Fix known rough edges found during testing
- [ ] Admin and other *untracked* accounts: the personal dashboard shows "Absent" days and greets
      "System". Hide personal attendance (or show an explanatory note) for untracked accounts.
- [ ] Overnight shifts: a session that *starts* after midnight counts to the next day even mid-shift
      (documented limitation). Decide the rule with HR; implement + test if it matters to you.
- [ ] Demo data is fixed in time (ends Oct 27): add a documented "refresh demo" path that backs up
      first, so demos never show a wall of absences.
- [ ] Silence the deprecation warning (`httpx` with Starlette's TestClient) before it becomes an error.

---

## P1 — Before a pilot team (quality you can show)

### Testing depth ("almost no bugs" lives here)
- [ ] End-to-end browser tests in the repo (Playwright): sign-in for each role, check-in/out,
      correction submit → approve, manager scope, admin user edit, CSV export,
      multi-tab sign-out. Run in CI against PostgreSQL. *Done when:* 15+ journeys green in CI.
- [ ] Property-based tests for the attendance rules engine (`hypothesis`): random sessions, schedules,
      holidays must never produce negative hours, double-counted days, or % outside 0–100.
- [ ] API contract tests: generate the TypeScript types from the OpenAPI schema so frontend and
      backend cannot silently drift.
- [ ] Split the single frontend test file per page/component; add tests for approvals, reports,
      admin screens, the calendar and error/empty states.
- [ ] Accessibility checks: automated (axe in Playwright) on every page + one manual keyboard /
      screen-reader pass. *Done when:* no serious axe violations.
- [ ] Timezone and date edge cases as explicit tests: month ends, leap day, IST midnight, a
      non-IST office timezone, browser in a different timezone from the office.
- [ ] Concurrency tests on PostgreSQL in CI (already written: parallel check-ins/check-outs, row locks).
- [ ] Rule: every bug fixed gets a regression test first (write the failing test, then fix).

### Security hardening
- [ ] Security headers: Content-Security-Policy, HSTS (behind HTTPS), Permissions-Policy.
- [ ] Single sign-on with Microsoft Entra ID (OIDC) instead of local passwords — also removes
      password-reset support burden. Keep local login only for a break-glass admin.
- [ ] If local passwords stay: self-service "forgot password" via email, and optional MFA for admins.
- [ ] Penetration-style review of authorisation: try every endpoint as each role (automated test
      matrix exists for key routes — extend to all 57).
- [ ] Rate limiting at the reverse proxy as well as in the app.
- [ ] Production config review: `APP_ENV=production`, strong `SECRET_KEY` from a secret store,
      `COOKIE_SECURE`, exact `CORS_ORIGINS`, `TRUST_PROXY_HEADERS` only behind the proxy.

### Environments and delivery
- [ ] Backend Dockerfile + production compose / deployment manifests (app, worker, PostgreSQL).
- [ ] A **staging** environment that mirrors production; every release goes there first.
- [ ] Automated deploys from `main` to staging; manual approval to production.
- [ ] Database migrations run as a deploy step with a pre-migration backup.

### User acceptance
- [ ] Pilot with one real team (5–15 people) for 2–4 weeks, with HR and a manager involved.
- [ ] Structured bug bash before the pilot (each role, written test charters, 2 hours).
- [ ] A simple way to report problems from inside the app (link to a form or ticket queue).

---

## P2 — Before company-wide rollout (operate it safely)

### Reliability and data safety
- [ ] Automated PostgreSQL backups (daily + point-in-time), **tested restore drill** documented.
- [ ] Uptime monitoring on `/api/health` and `/api/health/db`, alerting to the on-call channel.
- [ ] Error tracking (e.g. Sentry) for backend and frontend, with request IDs linked to logs.
- [ ] Structured JSON logs shipped to central logging; retention policy.
- [ ] Load test with realistic data (e.g. 1,000 employees × 1 year): dashboards and reports must stay
      under 2 s. Pre-aggregate daily totals if they don't.
- [ ] Frontend performance: main bundle ~845 KB — split Recharts/admin code further; target < 300 KB.

### Privacy and compliance (DPDP Act 2023)
- [ ] Privacy notice and HR/Legal sign-off on what is collected and why.
- [ ] Data retention rules (attendance history, audit log, login attempts, future auto-attendance signals)
      with scheduled purge jobs.
- [ ] Data export / deletion process for an individual employee on request.
- [ ] Access review: who can see whose attendance; quarterly admin-role review.

### Operations
- [ ] Runbook: deploy, roll back, restore, rotate secrets, common support questions.
- [ ] Ownership: named product owner, technical owner, and support contact.
- [ ] Release notes / changelog and semantic versioning shown in the app footer.

---

## P3 — After launch (improvements)
- [ ] Automatic attendance (Wi-Fi + Teams presence) — Phases 3–9, rolled out in shadow mode first.
- [ ] Notifications (email / Teams) for approvals and missing check-outs.
- [ ] Payroll / HRMS export integration.
- [ ] Multi-location holiday calendars.

---

## Definition of "production ready" for this project
All P0 and P1 items done, plus:
1. CI green on PostgreSQL with 0 skipped tests, coverage floors met, no lint errors.
2. E2E journeys green in CI; no open bugs rated high or critical; pilot ran 2+ weeks without data errors.
3. Staging and production deployed by the pipeline; backups tested by an actual restore.
4. HR/Legal sign-off on privacy; security headers and SSO in place.

## Needs people outside the code
| Item | Who |
|---|---|
| PostgreSQL server or install rights | IT |
| Git hosting + CI runners | IT / DevOps |
| Entra ID app registration for SSO (and later Teams presence) | IT (tenant admin consent) |
| Hosting, domain, HTTPS certificate | IT / DevOps |
| Privacy notice, retention rules, pilot team | HR / Legal |
