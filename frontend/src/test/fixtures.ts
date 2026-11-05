import type { CurrentStatus, DayRecord, EmployeeRow, Overview, Summary, Trends } from "@/lib/types";

import { emptyPage } from "./utils";

export const summary: Summary = {
  start: "2026-10-01",
  end: "2026-10-27",
  expected_days: 18,
  present_days: 16,
  present_expected_days: 16,
  extra_present_days: 0,
  absent_days: 1,
  leave_days: 1,
  holiday_days: 0,
  weekend_days: 7,
  not_scheduled_days: 0,
  incomplete_days: 1,
  not_yet_checked_in_days: 0,
  net_minutes: 7800,
  required_minutes: 8640,
  variance_minutes: -840,
  average_net_minutes: 487,
  attendance_percentage: 88.9,
  attended_days_for_average: 16,
  unresolved_sessions: 1,
  late_days: 2,
};

export function day(date: string, overrides: Partial<DayRecord> = {}): DayRecord {
  return {
    date,
    status: "PRESENT",
    expected: true,
    scheduled_workday: true,
    required_minutes: 480,
    net_minutes: 495,
    session_count: 1,
    completed_sessions: 1,
    first_check_in: date + "T04:05:00Z",
    last_check_out: date + "T13:30:00Z",
    has_active_session: false,
    unresolved_sessions: 0,
    holiday_name: null,
    leave_type: null,
    exceptions: [],
    ...overrides,
  };
}

export function current(active: boolean): CurrentStatus {
  return {
    server_time: "2026-10-27T06:30:00Z",
    today: "2026-10-27",
    office_timezone: "Asia/Kolkata",
    today_record: day("2026-10-27", active ? { has_active_session: true, net_minutes: 0 } : { status: "NOT_YET_CHECKED_IN", expected: false, net_minutes: 0, first_check_in: null, last_check_out: null }),
    active_session: active
      ? { id: 99, employee_id: 5, check_in_at: "2026-10-27T04:00:00Z", check_out_at: null, break_minutes: 0, net_minutes: null, status: "OPEN", source: "WEB", notes: null, voided_at: null, void_reason: null }
      : null,
    unresolved_session: null,
    schedule: { name: "General shift", start_time: "09:30:00", end_time: "18:30:00", required_minutes: 480, working_days: [1, 2, 3, 4, 5] },
    max_session_minutes: 960,
  };
}

export const trends: Trends = { granularity: "month", headcount: 1, items: [] };

/** Routes shared by every page that renders the employee dashboard. */
export function employeeRoutes(active = false) {
  return {
    "/attendance/current": current(active),
    "/attendance/employees/5/summary": summary,
    "/attendance/employees/5/days": (url: URL) => ({
      items: [day("2026-10-26"), day("2026-10-23", { status: "ABSENT", net_minutes: 0, exceptions: ["ABSENT"], first_check_in: null, last_check_out: null })],
      total: 2,
      page: Number(url.searchParams.get("page") ?? 1),
      page_size: 10,
    }),
    "/attendance/employees/5/trends": trends,
    "/corrections": emptyPage,
  };
}

export const period = {
  expected_days: 40,
  present_expected_days: 36,
  present_days: 36,
  absent_days: 2,
  leave_days: 1,
  incomplete_days: 1,
  late_days: 3,
  unresolved_sessions: 1,
  net_minutes: 17000,
  required_minutes: 19200,
  variance_minutes: -2200,
  attended_days_for_average: 36,
  average_net_minutes: 472,
  attendance_percentage: 90,
};

export const overview: Overview = {
  generated_at: "2026-10-27T06:30:00Z",
  on_date: "2026-10-27",
  start: "2026-10-01",
  end: "2026-10-27",
  headcount: 3,
  snapshot: { present: 2, absent: 0, not_checked_in: 1, on_leave: 0, incomplete: 0, off: 0, checked_in_now: 1 },
  period,
  pending_corrections: 1,
  pending_leave: 0,
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
    day: { status: "PRESENT", first_check_in: "2026-10-27T04:00:00Z", last_check_out: null, net_minutes: 0, has_active_session: true, exceptions: [] },
    period,
    exception_count: 0,
    ...overrides,
  };
}
