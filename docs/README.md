# Documentation

Start with the project [README](../README.md) (setup, running, tests). Then:

| Document | Read it for |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | How the parts fit, the security model, the full API list |
| [ATTENDANCE_RULES.md](ATTENDANCE_RULES.md) | How each day's status and every figure is calculated |
| [wfo.md](wfo.md) | Work-from-office rules, timesheet imports (formats, matching people, periods), exports and branding |
| [design-system.md](design-system.md) | Colour tokens and how the UI uses them |
| [scaling.md](scaling.md) | What grows with headcount and the measured dashboard timings |

Plans and history, in [plans/](plans/):

| Document | Status |
|---|---|
| [production-readiness.md](plans/production-readiness.md) | **Current**: what is left before go-live, by priority |
| [wifi-attendance.md](plans/wifi-attendance.md) | **Current**: office presence from the office Wi-Fi (device register done; log import next) |
| [automatic-attendance.md](plans/automatic-attendance.md) | On hold: earlier Wi-Fi / Teams design; its Wi-Fi part continues in wifi-attendance.md |
| [feature-roadmap.md](plans/feature-roadmap.md) | Historical: the original feature-by-feature roadmap |

## Where to change what

| To change… | Backend (`backend/app/`) | Frontend (`frontend/src/`) |
|---|---|---|
| How a day or a month is counted | `services/attendance_rules.py`, `services/wfo_rules.py` | — |
| Reading timesheet files (Excel, CSV, PDF) | `services/timesheet_files.py` | — |
| Import checks, matching people, preview and commit | `services/wfo_upload.py` | `pages/wfo/AttendanceImportPage.tsx` |
| Accepted column names and work-mode words | `backend/config/wfo_mapping.json` | — |
| Employee dashboard | `api/routes/attendance.py`, `services/attendance_service.py` | `pages/MyDashboard.tsx`, `components/attendance/` |
| Team and organisation dashboards | `api/routes/dashboard.py`, `services/dashboard.py` | `pages/TeamDashboard.tsx`, `pages/admin/OrgDashboard.tsx` |
| WFO pages | `api/routes/wfo.py`, `services/wfo.py` | `pages/wfo/WfoPages.tsx`, `components/wfo/WfoParts.tsx` |
| PDF / Excel export of one employee | `services/attendance_export.py` | `components/attendance/EmployeeAttendanceView.tsx` (Export menu) |
| CSV reports, organisation Excel | `services/reports.py`, `services/wfo_export.py` | `pages/ReportsPage.tsx` |
| Who may see whom | `services/scope.py` | — |
| Sign-in (password, Microsoft, dev) | `api/routes/auth.py`, `services/auth_service.py`, `services/entra.py` | `pages/LoginPage.tsx`, `auth/` |
| Employees, departments, schedules, holidays, policy | `api/routes/admin.py`, `services/admin_users.py`, `services/configuration.py` | `pages/admin/` |
| Offices and the device register (laptop → person for Wi-Fi) | `api/routes/devices.py`, `services/devices.py`, `models/devices.py` | `pages/admin/DevicesAdmin.tsx` |
| Organisation hierarchy view | `services/org.py` | `pages/admin/HierarchyPage.tsx` |
| Database tables | `models/` + a new migration in `backend/alembic/versions/` | — |
| Settings (environment variables) | `core/config.py`, `backend/.env.example` | `frontend/.env.example` |
| Colours and themes | — | `index.css` |

Tests sit next to the code they cover: `backend/app/tests/` (pytest) and `frontend/src/test/` (Vitest).
