# Documentation

Start with the project [README](../README.md) (setup, running, tests). Then:

| Document | Read it for |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | How the parts fit, the security model, the full API list |
| [ATTENDANCE_RULES.md](ATTENDANCE_RULES.md) | How each day's status and every figure is calculated |
| [wfo.md](wfo.md) | Work-from-office rules, timesheet imports (formats, matching people, periods), exports and branding |
| [design-system.md](design-system.md) | Colour tokens and how the UI uses them |
| [scaling.md](scaling.md) | What grows with headcount and the measured dashboard timings |
| [dotnet-cutover.md](dotnet-cutover.md) | Historical: the checklist used to switch from the former Python backend to the .NET API |
| [dotnet-migration-plan.md](dotnet-migration-plan.md) | The .NET port of the former Python backend: inventory, decisions, parity rules and what each phase built |

Plans and history, in [plans/](plans/):

| Document | Status |
|---|---|
| [production-readiness.md](plans/production-readiness.md) | **Current**: what is left before go-live, by priority |
| [wifi-attendance.md](plans/wifi-attendance.md) | **Current**: office presence from the office Wi-Fi (device register done; log import next) |
| [automatic-attendance.md](plans/automatic-attendance.md) | On hold: earlier Wi-Fi / Teams design; its Wi-Fi part continues in wifi-attendance.md |
| [feature-roadmap.md](plans/feature-roadmap.md) | Historical: the original feature-by-feature roadmap |

## Where to change what

Backend paths are under `backend-dotnet/src/`.

| To change… | Backend | Frontend (`frontend/src/`) |
|---|---|---|
| How a day or a month is counted | `ATS.Domain/Rules/AttendanceRules.cs`, `ATS.Domain/Rules/WfoRules.cs` | — |
| Reading timesheet files (Excel, CSV, PDF) | `ATS.Application/Uploads/TimesheetFiles.cs` (`PdfTimesheet.cs`, `XlsxReader.cs`) | — |
| Import checks, matching people, preview and commit | `ATS.Application/Uploads/WfoUploadService.cs` | `pages/wfo/AttendanceImportPage.tsx` |
| Accepted column names and work-mode words | `backend-dotnet/config/wfo_mapping.json` | — |
| Employee dashboard | `ATS.Api/Controllers/AttendanceController.cs`, `ATS.Application/Attendance/AttendanceService.cs` | `pages/MyDashboard.tsx`, `components/attendance/` |
| Team and organisation dashboards, hierarchy | `ATS.Api/Controllers/DashboardController.cs`, `ATS.Application/Dashboard/DashboardService.cs` | `pages/TeamDashboard.tsx`, `pages/admin/OrgDashboard.tsx`, `pages/admin/HierarchyPage.tsx` |
| WFO pages | `ATS.Api/Controllers/WfoReadController.cs`, `WfoAdminController.cs`, `ATS.Application/Wfo/WfoService.cs` | `pages/wfo/WfoPages.tsx`, `components/wfo/WfoParts.tsx` |
| PDF / Excel export of one employee | `ATS.Application/Reports/EmployeeReport.cs`, `EmployeePdf.cs`, `EmployeeWorkbook.cs` | `components/attendance/EmployeeAttendanceView.tsx` (Export menu) |
| CSV reports, organisation Excel | `ATS.Api/Controllers/ReportsController.cs`, `ATS.Application/Reports/ReportService.cs`, `CsvReport.cs` | `pages/ReportsPage.tsx` |
| Who may see whom | `ATS.Application/Scope/ScopeService.cs` | — |
| Sign-in (password, Microsoft, dev) | `ATS.Api/Controllers/AuthController.cs`, `ATS.Application/Auth/AuthService.cs`, `ATS.Infrastructure/Security/EntraSignIn.cs` | `pages/LoginPage.tsx`, `auth/` |
| Employees, departments, schedules, holidays, policy | `ATS.Api/Controllers/AdminController.cs`, `ATS.Application/Admin/AdminUserService.cs`, `ConfigurationService.cs` | `pages/admin/` |
| Offices and the device register (laptop → person for Wi-Fi) | `ATS.Api/Controllers/DevicesController.cs`, `ATS.Application/Devices/DeviceService.cs` | `pages/admin/DevicesAdmin.tsx` |
| Database tables | `ATS.Domain/Entities/`, `ATS.Infrastructure/Persistence/AtsDbContext.cs` + a new EF Core migration in `ATS.Infrastructure/Persistence/Migrations/` (an existing database needs `ef-baseline` once first) | — |
| Settings (environment variables) | `ATS.Application/Configuration/SettingsLoader.cs`, `AtsSettings.cs`, `backend-dotnet/.env.example` | `frontend/.env.example` |
| Colours and themes | — | `index.css` |

Tests: `backend-dotnet/tests/ATS.Tests/` (xUnit) and `frontend/src/test/` (Vitest).
