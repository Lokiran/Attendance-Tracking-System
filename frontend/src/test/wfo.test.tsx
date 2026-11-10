import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it } from "vitest";

import type { AttendanceUpload, EmployeeWfo, OrgWfo, TeamWfo, WfoDay, WfoRow } from "@/lib/types";

import { current, employeeRoutes } from "./fixtures";
import { me, mockApi, renderApp } from "./utils";

const policy = { min_days_per_week: 3, min_days_per_month: 12, min_office_minutes: 240 };
const summary = {
  working_days: 7, standard_days: 21, wfo_days: 2, wfh_days: 4, office_days_below_minimum: 1, leave_days: 0,
  holiday_days: 0, weekoff_days: 2, required_days: 4, shortfall_days: 2, wfo_percentage: 28.6,
  compliance: "NON_COMPLIANT" as const,
};

function wfoDay(date: string, over: Partial<WfoDay> = {}): WfoDay {
  return { date, work_mode: null, wfo_source: null, is_wfo_day: false, office_minutes: 0, wfh_minutes: 0, conflicts: [],
    scheduled: true, overridden: false, override_note: null, in_progress: false, future: false, ...over };
}

function employeeWfo(): EmployeeWfo {
  const days: WfoDay[] = [];
  for (let d = 1; d <= 30; d++) {
    const date = `2026-11-${String(d).padStart(2, "0")}`;
    const dow = new Date(date + "T00:00:00").getDay();
    if (dow === 0 || dow === 6) days.push(wfoDay(date, { work_mode: "WEEKOFF", scheduled: false, future: d > 10 }));
    else if (d === 2 || d === 3) days.push(wfoDay(date, { work_mode: "OFFICE", wfo_source: "CHECK_IN", is_wfo_day: true, office_minutes: 500 }));
    else if (d === 4) days.push(wfoDay(date, { work_mode: "OFFICE", wfo_source: "BADGE", office_minutes: 180, conflicts: ["CHECK_IN said WFH; office signal from BADGE"] }));
    else if (d <= 9) days.push(wfoDay(date, { work_mode: "WFH", wfo_source: "INFERRED", wfh_minutes: 480 }));
    else days.push(wfoDay(date, { in_progress: d === 10, future: d > 10 }));
  }
  return {
    employee: { id: 5, employee_code: "EMP001", full_name: "Rahul Reddy", department_name: "AI Engineering", manager_id: 3, manager_name: "Priya Sharma" },
    month: "2026-11", start: "2026-11-01", end: "2026-11-30", today: "2026-11-10", policy, summary,
    weekly_wfo_breakdown: [
      { week_start: "2026-11-02", week_end: "2026-11-08", label: "02 Nov – 08 Nov", working_days: 5, wfo_days: 2, required: 3, status: "SHORT", shortfall: 1 },
      { week_start: "2026-11-09", week_end: "2026-11-15", label: "09 Nov – 15 Nov", working_days: 1, wfo_days: 0, required: 1, status: "IN_PROGRESS", shortfall: 0 },
    ],
    days,
  };
}

function row(id: number, name: string, wfo: number, compliant: boolean, manager_id = 3): WfoRow {
  return { ...summary, wfo_days: wfo, compliance: compliant ? "COMPLIANT" : "NON_COMPLIANT", shortfall_days: compliant ? 0 : 2,
    employee: { id, employee_code: `EMP00${id}`, full_name: name, department_name: "AI Engineering", manager_id, manager_name: "Priya Sharma" } };
}

const totals = { headcount: 3, working_days: 21, wfo_days: 9, wfh_days: 8, wfo_percentage: 42.9, compliant: 1, non_compliant: 2 };

beforeEach(() => localStorage.clear());

describe("WFO: employee", () => {
  it("shows KPIs, weekly progress, a labelled calendar and the donut", async () => {
    mockApi({ "/auth/me": me(), "/me/wfo": employeeWfo() });
    renderApp("/my/wfo?month=2026-11");
    expect(await screen.findByRole("heading", { name: "Office days" })).toBeInTheDocument();
    expect(await screen.findByText("28.6%")).toBeInTheDocument();
    expect(screen.getAllByText("Non-compliant").length).toBeGreaterThan(0);
    expect(screen.getByRole("progressbar", { name: "02 Nov – 08 Nov: 2 of 3 WFO days" })).toBeInTheDocument();
    expect(screen.getByText(/1 short/)).toBeInTheDocument();
    expect(screen.getByText(/In progress/)).toBeInTheDocument();
    // calendar cells carry text labels, not colour alone
    expect(screen.getAllByText("WFO").length).toBeGreaterThan(1);
    expect(screen.getByText("WFO <min")).toBeInTheDocument(); // 3 h office day: below the 4 h minimum
    expect(screen.getAllByText("WFH").length).toBeGreaterThan(1);
    expect(screen.getByLabelText(/Conflicting signals/)).toBeInTheDocument();
    // donut has a table view with the same numbers
    await userEvent.setup().click(screen.getAllByRole("button", { name: /table/i })[0]);
    expect(await screen.findByRole("cell", { name: "4" })).toBeInTheDocument();
  });

  it("checks in with the chosen work location", async () => {
    let active = false;
    const { calls } = mockApi({
      "/auth/me": me(), ...employeeRoutes(),
      "/attendance/current": () => current(active),
      "POST /attendance/check-in": () => { active = true; return { ...current(true).active_session, work_location: "WFH" }; },
    });
    renderApp("/");
    const user = userEvent.setup();
    const wfh = await screen.findByRole("button", { name: "WFH" });
    expect(screen.getByRole("button", { name: "Office" })).toHaveAttribute("aria-pressed", "true"); // default
    await user.click(wfh);
    expect(wfh).toHaveAttribute("aria-pressed", "true");
    await user.click(screen.getByRole("button", { name: "Check in" }));
    await waitFor(() => expect(calls.find((c) => c.path === "/attendance/check-in")?.body).toEqual({ work_location: "WFH" }));
    expect(localStorage.getItem("ats-work-location")).toBe("WFH");
  });
});

describe("WFO: manager and admin", () => {
  it("filters the team table to non-compliant people", async () => {
    const team: TeamWfo = { month: "2026-11", start: "2026-11-01", end: "2026-11-30", policy, totals,
      rows: [row(5, "Rahul Reddy", 5, true), row(6, "Sneha Rao", 2, false), row(7, "Farah Khan", 2, false)] };
    mockApi({ "/auth/me": me("MANAGER", { id: 3, full_name: "Priya Sharma" }), "/team/wfo": team });
    renderApp("/team/wfo?month=2026-11");
    const table = await screen.findByRole("table", { name: "Team office days" });
    expect(within(table).getByText("Rahul Reddy")).toBeInTheDocument();
    expect(screen.getByText("42.9%")).toBeInTheDocument();
    await userEvent.setup().click(screen.getByLabelText("Non-compliant only"));
    expect(within(table).queryByText("Rahul Reddy")).not.toBeInTheDocument();
    expect(within(table).getByText("Sneha Rao")).toBeInTheDocument();
    expect(screen.getByText("WFO days per person")).toBeInTheDocument();
  });

  it("expands managers in the org view and offers the Excel export", async () => {
    const org: OrgWfo = { month: "2026-11", start: "2026-11-01", end: "2026-11-30", policy, totals,
      groups: [{ manager_id: 3, manager_name: "Priya Sharma", department_name: "AI Engineering", totals, rows: [row(5, "Rahul Reddy", 5, true), row(6, "Sneha Rao", 2, false)] }] };
    mockApi({ "/auth/me": me("ADMIN", { id: 1, full_name: "System Administrator" }), "/org/wfo": org, "/managers": [], "/departments": [] });
    renderApp("/admin/wfo?month=2026-11");
    const toggle = await screen.findByRole("button", { name: "Priya Sharma" });
    expect(screen.queryByRole("link", { name: "Sneha Rao" })).not.toBeInTheDocument();
    await userEvent.setup().click(toggle);
    expect(toggle).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("link", { name: "Sneha Rao" })).toHaveAttribute("href", "/admin/wfo/6?month=2026-11");
    expect(screen.getByRole("button", { name: "Export Excel" })).toBeInTheDocument();
  });

  it("lets an admin override a day from the calendar", async () => {
    const { calls } = mockApi({
      "/auth/me": me("ADMIN", { id: 1, full_name: "System Administrator" }),
      "/team/wfo/employees/5": employeeWfo(),
      "PUT /admin/attendance/5/2026-11-05/work-mode": { employee_id: 5, work_date: "2026-11-05", work_mode: "OFFICE", wfo_source: "MANUAL_OVERRIDE", overridden: true, override_note: "Client visit", overridden_at: "2026-11-10T07:00:00Z" },
    });
    renderApp("/admin/wfo/5?month=2026-11");
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: /^Thursday,? 05 Nov,? 2026/ }));
    const dialog = await screen.findByRole("dialog");
    await user.selectOptions(within(dialog).getByLabelText("Work mode"), "OFFICE");
    await user.type(within(dialog).getByLabelText("Note (optional)"), "Client visit");
    await user.click(within(dialog).getByRole("button", { name: "Save override" }));
    await waitFor(() => expect(calls.find((c) => c.method === "PUT")?.body).toEqual({ work_mode: "OFFICE", note: "Client visit" }));
  });
});

describe("WFO: attendance import", () => {
  const preview: AttendanceUpload = {
    id: 7, filename: "hr.csv", status: "PREVIEW", file_size: 1200, row_count: 182, error_count: 1, overwrite_overrides: false,
    uploaded_by_name: "System Administrator", created_at: "2026-11-10T07:00:00Z", committed_at: null, commit_summary: null,
    summary: {
      columns: { employee: "Employee Code" }, rows: 182, valid_rows: 181, error_rows: 1, badge_rows: 2, duplicate_rows: 0,
      employee_days: 180, employees: 9, new_days: 180, by_work_mode: { OFFICE: 100, WFH: 79, LEAVE: 1 }, office: 100, wfh: 79,
      office_below_minimum: 4, conflict_count: 1, protected_overrides: 2, date_from: "2026-10-05", date_to: "2026-10-30",
      conflicts: [{ employee_code: "EMP003", full_name: "Arjun Kumar", date: "2026-10-08", conflicts: ["UPLOAD_COLUMN said WFH; office signal from BADGE"], rows: [18, 182] }],
      errors: [{ row: 9, employee: "NOPE", error: "unknown employee 'NOPE'" }],
    },
  };

  it("previews an upload and commits it, choosing to overwrite overrides", async () => {
    const { calls } = mockApi({
      "/auth/me": me("ADMIN", { id: 1, full_name: "System Administrator" }),
      "/admin/attendance-uploads": [],
      "POST /admin/attendance-uploads": preview,
      "POST /admin/attendance-uploads/7/commit": { ...preview, status: "COMMITTED", commit_summary: { applied_days: 180, created_days: 178, skipped_overrides: 0, overrides_overwritten: 2, error_rows: 1 } },
    });
    renderApp("/admin/attendance-import");
    const user = userEvent.setup();
    const input = await screen.findByLabelText("File");
    await user.upload(input, new File(["Employee Code,Date\n"], "hr.csv", { type: "text/csv" }));
    await user.click(screen.getByRole("button", { name: "Upload and preview" }));
    expect(await screen.findByText("hr.csv · preview")).toBeInTheDocument();
    expect(calls.find((c) => c.method === "POST" && c.path === "/admin/attendance-uploads")?.body).toEqual({ file: "hr.csv" });
    expect(screen.getByText("Conflicting signals")).toBeInTheDocument();
    expect(screen.getByText("unknown employee 'NOPE'")).toBeInTheDocument();
    await user.click(screen.getByLabelText(/Overwrite 2 day\(s\) an admin overrode/));
    await user.click(screen.getByRole("button", { name: "Import 180 day(s)" }));
    await waitFor(() => expect(calls.find((c) => c.path === "/admin/attendance-uploads/7/commit")?.body).toEqual({ overwrite_overrides: true }));
    expect(await screen.findByText(/Applied 180 day\(s\)/)).toBeInTheDocument();
  });

  it("reads a monthly workbook, switches sheet and adds unknown employees", async () => {
    const workbook: AttendanceUpload = {
      ...preview, id: 8, filename: "October 2026.xlsx", row_count: 9, error_count: 3,
      summary: {
        ...preview.summary!, columns: { employee: "Employee ID", name: "Employee", date: "Date" }, rows: 9, valid_rows: 6, error_rows: 3,
        badge_rows: 0, duplicate_rows: 1, employee_days: 5, employees: 2, new_days: 5, by_work_mode: { OFFICE: 5 }, office: 5, wfh: 0,
        office_below_minimum: 0, conflict_count: 0, conflicts: [], protected_overrides: 0,
        errors: [{ row: 8, employee: "Intern - 10001", error: "unknown employee 'Intern - 10001' (not in the app yet)" }],
        sheet: "October 2026", presence_list: true, presence_mode: "OFFICE", blank_rows: 6,
        sheets: [{ name: "Raw Data", usable: true, rows: 1 }, { name: "October 2026", usable: true, rows: 9 }],
        unknown_employees: [{ employee_ref: "Intern - 10001", name: "New Intern", other_names: ["N. Intern"], rows: 3, first_date: "2026-10-21", can_add: true, reason: null }],
        name_mismatches: [{ employee_ref: "EMP002", file_name: "Somebody Else", app_name: "Priya Sharma" }],
        date_notes: ["2 date(s) had day and month swapped and were read in October 2026."],
      },
    };
    const added: AttendanceUpload = {
      ...workbook, error_count: 0,
      summary: { ...workbook.summary!, valid_rows: 9, error_rows: 0, errors: [], unknown_employees: [], employee_days: 8, office: 8, employees_added: 1 },
    };
    const { calls } = mockApi({
      "/auth/me": me("ADMIN", { id: 1, full_name: "System Administrator" }),
      "/admin/attendance-uploads": [],
      "POST /admin/attendance-uploads": (url: URL) => (url.searchParams.get("sheet") === "Raw Data" ? { ...workbook, id: 9, summary: { ...workbook.summary!, sheet: "Raw Data" } } : workbook),
      "POST /admin/attendance-uploads/8/discard": { ...workbook, status: "DISCARDED" },
      "POST /admin/attendance-uploads/9/create-employees": { ...added, id: 9 },
    });
    renderApp("/admin/attendance-import");
    const user = userEvent.setup();
    await user.upload(await screen.findByLabelText("File"), new File(["x"], "October 2026.xlsx"));
    await user.click(screen.getByRole("button", { name: "Upload and preview" }));
    expect(await screen.findByText("October 2026.xlsx · preview")).toBeInTheDocument();
    expect(screen.getByText(/1 employee ID\(s\) are not in the app yet \(3 row\(s\)\)/)).toBeInTheDocument();
    expect(screen.getByText(/2 date\(s\) had day and month swapped/)).toBeInTheDocument();
    expect(screen.queryByText("Rows that will not be imported")).not.toBeInTheDocument(); // unknown IDs have their own list
    expect(screen.getByLabelText("Each listed day counts as")).toHaveValue("OFFICE");

    await user.selectOptions(screen.getByLabelText("Sheet read"), "Raw Data");
    await waitFor(() => expect(calls.some((c) => c.path === "/admin/attendance-uploads/8/discard")).toBe(true));
    await waitFor(() => expect(screen.getByLabelText("Sheet read")).toHaveValue("Raw Data"));
    expect(calls.filter((c) => c.method === "POST" && c.path === "/admin/attendance-uploads")).toHaveLength(2);

    await user.click(screen.getByRole("button", { name: "Add 1 employee(s) and re-check" }));
    expect(await screen.findByText(/1 employee\(s\) were added from this file/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Import 8 day(s)" })).toBeEnabled();
  });
});
