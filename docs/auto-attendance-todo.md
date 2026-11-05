# Automatic attendance: implementation checklist

Status legend: `[x]` done and verified · `[ ]` open · `[~]` blocked (reason given)

Approved design decisions: A–H (see `docs/auto-attendance.md` once written), plus the eight
amendments from the approval message (manual supersedes auto, advisory-locked workers, hashed
rotating ingest keys, VPN-excluded low-confidence office IP, this checklist, PostgreSQL
verification, phase order, safe defaults).

Order of work: Phase 1 → Phase 5 (pure engine) → Phases 3, 4 → 6 → 7 → 8 → 9.

## Phase 1 – Database ✅ (2026-11-04)
- [x] Migration `0002` (additive): `signals` table + index (employee_id, seen_at) + dedupe key
- [x] Migration `0002`: `auto_attendance_days` (unique employee_id + work_date; check_in/out, location,
      source, needs_review, review reasons, shadow flag, superseded flag, review fields)
- [x] Migration `0002`: `users.graph_user_id`, `users.wifi_identity` (nullable, unique indexes)
- [x] Migration `0002`: policy columns `auto_attendance_enabled` (false), `auto_shadow_mode` (true)
- [x] Migration `0002`: session sources `AUTO_WIFI` / `AUTO_TEAMS` (CHECK widened; data preserved)
- [x] Migration `0002`: `worker_jobs` table (last_run_at / last_success_at / last_error per job)
- [x] Migration `0002`: `ingest_api_keys` table (hashed keys, two active for rotation)
- [x] `scripts/backfill_identities.py` + `app/services/identities.py`: CSV → graph_user_id / wifi_identity
      (dry-run by default, all-or-nothing validation, MAC/GUID normalisation, audit `USER_IDENTITY_LINKED`)
- [x] 0002 up / down / up on SQLite, including a copy of real dev data: all rows identical, every CHECK,
      FK and index preserved; downgrade refuses while AUTO_* sessions exist, then restores data exactly
- [x] Safety: Alembic runs SQLite migrations with `PRAGMA foreign_keys=OFF` (a table rebuild would
      otherwise cascade-delete / null referencing rows); fixed version-stamp commit
- [x] Dev database backed up to `backend/data/ats-dev.pre-0002.sqlite3`, then migrated to 0002
- [~] 0002 up / down on PostgreSQL — tests written (`app/tests/postgres/test_pg_migrations.py`), not run:
      no PostgreSQL available (winget download blocked with HTTP 403; installer also needs admin)

## Phase 5 – Rules engine (pure, no external systems)
- [ ] Day evaluation: Wi-Fi → OFFICE; Teams ≥ N confirming polls → WFH; sessions split by gap; check-out grace
- [ ] needs_review reasons: weak/single signal, office-IP only, Wi-Fi vs Teams conflict, short hours, no signal
- [ ] Skip leave / holiday / weekend
- [ ] Idempotent upsert; only AUTO_* sessions ever created/modified; shadow vs official
- [ ] Manual supersedes auto: VOID auto session (SUPERSEDED_BY_MANUAL), flag day, audit
- [ ] Tests: office day, WFH day, lunch gap, forgot-to-disconnect, no signals, leave day, manual override
      (incl. manual 10:00 after auto 09:35), IST boundaries, idempotent reruns

## Phase 3 – Wi-Fi ingestion
- [ ] `POST /api/ingest/wifi`: hashed API keys (2 active), constant-time compare, body-size cap,
      single or batch (≤ 500), seen_at window (−24 h / +5 min), identity mapping, dedupe, rate limit
- [ ] Office-IP fallback on login: `OFFICE_PUBLIC_IPS` minus `OFFICE_VPN_CIDRS`, ≤ 1 per 10 min,
      trusted proxy only, low confidence (needs_review, never a clean official day on its own)
- [ ] `scripts/simulate_wifi.py`

## Phase 4 – Teams presence worker
- [ ] Graph client: client-credentials token, batches ≤ 600, 429 / Retry-After, token expiry, partial failures, metrics
- [ ] Poll loop: weekdays, SHIFT_START−1 h … SHIFT_END+3 h
- [ ] Mock Graph client; no network in tests

## Phase 6 – API
- [ ] `GET /api/attendance/today` (+ `/current`) with location, source, needs_review
- [ ] `GET /api/attendance/review-queue` (manager scope / admin) + approve / reject
- [ ] `GET /api/admin/auto-attendance/shadow-report` (+ CSV); settings + health endpoints

## Phase 7 – Frontend
- [ ] Dashboard auto-detected badge (Office/WFH, first/last seen, source) + Regularize
- [ ] Review queue page
- [ ] Admin page: toggles, shadow report, health indicators
- [ ] Tokens: pending-review (amber), late (orange); icon + label; light + dark; tests / type-check / build

## Phase 8 – Privacy and ops
- [ ] `docs/auto-attendance.md`, `docs/privacy-notice.md`
- [ ] Signal retention purge (90 days)
- [ ] Audit log for every auto-created / changed record
- [ ] Workers: advisory lock per job (no-op on SQLite), persisted job health, back-off on errors
- [ ] Backend Dockerfile + compose worker services + healthchecks

## Phase 9 – Verification
- [ ] Full suites, type-check, build
- [~] docker-compose smoke test — blocked: Docker cannot start here (no WSL / admin)
- [~] PostgreSQL: 23 PG-only tests, 0002 up/down, advisory lock + upsert — blocked (see Phase 1)
- [ ] Manual demo script: 5 employees, simulated Wi-Fi + mocked presence
- [ ] Final summary

## Verification log
| Date | Phase | Backend tests | Frontend tests | Type-check | Build |
|---|---|---|---|---|---|
| 2026-11-04 | Baseline (before Phase 1) | 146 passed, 23 skipped (PG) | 18 passed | clean | ok |
| 2026-11-04 | Phase 1 | 161 passed, 26 skipped (PG) | 18 passed | clean | ok |
