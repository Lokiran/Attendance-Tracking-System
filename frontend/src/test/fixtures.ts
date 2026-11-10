import type { CurrentStatus, DayRecord, EmployeeRow, Overview, PeriodStats, Summary, Trends } from "@/lib/types";

export const summary: Summary = {
  start: "2026-10-01",
  end: "2026-10-27",
  expected_days: 18,
  present_days: 14,
  present_expected_days: 14,
  extra_present_days: 0,
  absent_days: 1,
  wfh_days: 3,
  leave_days: 1,
  holiday_days: 0,
  weekend_days: 7,
  not_scheduled_days: 0,
  no_timesheet_days: 0,
  net_minutes: 6720,
  required_minutes: 8640,
  hours_days: 14,
  hours_required_minutes: 6720,
  variance_minutes: 0,
  average_net_minutes: 480,
  attendance_percentage: 77.8,
  late_days: 2,
  wfo: {
    working_days: 17, standard_days: 18, wfo_days: 14, wfh_days: 3, office_days_below_minimum: 0, leave_days: 1,
    holiday_days: 0, weekoff_days: 7, required_days: 12, shortfall_days: 0, wfo_percentage: 82.4,
    compliance: "COMPLIANT", wfo_band: "MET",
    weekly_wfo_breakdown: [{ week_start: "2026-10-05", week_end: "2026-10-11", label: "05 Oct – 11 Oct", working_days: 5, wfo_days: 4, required: 3, status: "MET", shortfall: 0 }],
  },
};

export function day(date: string, overrides: Partial<DayRecord> = {}): DayRecord {
  return {
    date,
    status: "PRESENT",
    expected: true,
    scheduled_workday: true,
    required_minutes: 480,
    net_minutes: 495,
    hours_known: true,
    work_mode: "OFFICE",
    first_in: date + "T04:05:00Z",
    last_out: date + "T13:30:00Z",
    holiday_name: null,
    exceptions: [],
    ...overrides,
  };
}

export function current(through: string | null = "2026-09-30"): CurrentStatus {
  return {
    server_time: "2026-10-27T06:30:00Z",
    today: "2026-10-27",
    office_timezone: "Asia/Kolkata",
    schedule: { name: "General shift", start_time: "09:30:00", end_time: "18:30:00", required_minutes: 480, working_days: [1, 2, 3, 4, 5] },
    timesheet_through: through,
    my_timesheet_through: through,
    last_import_at: through ? "2026-10-05T06:00:00Z" : null,
  };
}

export const trends: Trends = { granularity: "month", headcount: 1, items: [] };

/** Routes shared by every page that renders the employee dashboard. */
export function employeeRoutes() {
  return {
    "/attendance/current": current(),
    "/attendance/employees/5/summary": summary,
    "/attendance/employees/5/days": (url: URL) => ({
      items: [
        day("2026-10-26"),
        day("2026-10-23", { status: "ABSENT", net_minutes: 0, hours_known: false, work_mode: null, exceptions: ["ABSENT"], first_in: null, last_out: null }),
      ],
      total: 2,
      page: Number(url.searchParams.get("page") ?? 1),
      page_size: 10,
    }),
    "/attendance/employees/5/trends": trends,
  };
}

export const period: PeriodStats = {
  expected_days: 40,
  present_expected_days: 34,
  present_days: 34,
  absent_days: 2,
  wfh_days: 4,
  leave_days: 1,
  no_timesheet_days: 0,
  late_days: 3,
  net_minutes: 16048,
  required_minutes: 19200,
  hours_days: 34,
  hours_required_minutes: 16320,
  variance_minutes: -272,
  average_net_minutes: 472,
  attendance_percentage: 85,
};

export const overview: Overview = {
  generated_at: "2026-10-27T06:30:00Z",
  on_date: "2026-10-27",
  start: "2026-10-01",
  end: "2026-10-27",
  headcount: 3,
  snapshot: { present: 2, wfh: 0, absent: 0, leave: 0, no_timesheet: 1, off: 0 },
  period,
  attendance_bands: [
    { label: "<70%", count: 0 },
    { label: "70–80%", count: 0 },
    { label: "80–90%", count: 1 },
    { label: "90–95%", count: 1 },
    { label: "95–100%", count: 1 },
  ],
  timesheet_through: "2026-09-30",
};

export function row(id: number, name: string, overrides: Partial<EmployeeRow> = {}): EmployeeRow {
  return {
    employee_id: id,
    employee_code: "EMP00" + id,
    full_name: name,
    job_title: "Engineer",
    role: "EMPLOYEE",
    is_active: true,
    department_id: 1,
    department_name: "AI Engineering",
    manager_id: 3,
    manager_name: "Priya Sharma",
    day: { status: "PRESENT", first_in: "2026-10-27T04:00:00Z", last_out: "2026-10-27T12:30:00Z", net_minutes: 510, hours_known: true, work_mode: "OFFICE", exceptions: [] },
    period,
    exception_count: 0,
    ...overrides,
  };
}
