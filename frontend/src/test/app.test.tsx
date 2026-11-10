import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import { DataTable } from "@/components/ui/DataTable";
import { DateRangeFilter } from "@/components/ui/DateRangeFilter";
import { ErrorState } from "@/components/ui/States";
import { ApiError } from "@/lib/api";
import { addDays, endOfMonth, presetRange } from "@/lib/dates";
import { formatMinutes } from "@/lib/format";

import { current, day, employeeRoutes, overview, row, summary } from "./fixtures";
import { apiError, emptyPage, me, mockApi, renderApp } from "./utils";

describe("login", () => {
  it("validates the form before calling the API", async () => {
    const { calls } = mockApi({ "/auth/me": apiError(401, "Not signed in", "not_authenticated") });
    renderApp("/login");
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Sign in" }));
    expect(await screen.findByText("Email is required")).toBeInTheDocument();
    expect(screen.getByText("Password is required")).toBeInTheDocument();
    await user.type(screen.getByLabelText("Email"), "not-an-email");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByText("Enter a valid email address")).toBeInTheDocument();
    expect(calls.some((c) => c.path === "/auth/login")).toBe(false);
  });

  it("shows the server's error message on failed sign-in", async () => {
    mockApi({
      "/auth/me": apiError(401, "Not signed in"),
      "POST /auth/login": apiError(401, "Invalid email or password", "invalid_credentials"),
    });
    renderApp("/login");
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText("Email"), "rahul.reddy@nexa.example");
    await user.type(screen.getByLabelText("Password"), "wrong");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Invalid email or password");
  });

  it("signs in and lands on the dashboard", async () => {
    let signedIn = false;
    mockApi({
      "/auth/me": () => (signedIn ? me() : apiError(401, "Not signed in")),
      "POST /auth/login": () => {
        signedIn = true;
        return me();
      },
      ...employeeRoutes(),
    });
    renderApp("/login");
    const user = userEvent.setup();
    await user.type(await screen.findByLabelText("Email"), "rahul.reddy@nexa.example");
    await user.type(screen.getByLabelText("Password"), "Secret12345");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("heading", { name: /Good (morning|afternoon|evening), Rahul/ })).toBeInTheDocument();
  });

  it("offers Microsoft sign-in, keeping the page to return to", async () => {
    mockApi({ "/auth/me": apiError(401, "Not signed in"), "/auth/providers": { password: true, entra: true, dev: false } });
    renderApp("/login");
    const link = await screen.findByRole("link", { name: "Sign in with Microsoft" });
    expect(link).toHaveAttribute("href", "/api/auth/entra/login?next=%2F");
    expect(screen.getByLabelText("Password")).toBeInTheDocument(); // password still available until Entra is live
    expect(screen.queryByText(/Development sign-in/)).not.toBeInTheDocument();
  });

  it("hides the password form when only Microsoft sign-in is enabled", async () => {
    mockApi({ "/auth/me": apiError(401, "Not signed in"), "/auth/providers": { password: false, entra: true, dev: false } });
    renderApp("/login");
    expect(await screen.findByRole("link", { name: "Sign in with Microsoft" })).toBeInTheDocument();
    expect(screen.queryByLabelText("Password")).not.toBeInTheDocument();
  });

  it("explains a refused Microsoft sign-in", async () => {
    mockApi({ "/auth/me": apiError(401, "Not signed in"), "/auth/providers": { password: true, entra: true, dev: false } });
    renderApp("/login?error=no_account");
    expect(await screen.findByRole("alert")).toHaveTextContent("isn't linked to an attendance account yet. Ask HR");
  });

  it("signs in with the dev bypass when the backend enables it", async () => {
    let signedIn = false;
    const { calls } = mockApi({
      "/auth/me": () => (signedIn ? me() : apiError(401, "Not signed in")),
      "/auth/providers": { password: false, entra: false, dev: true },
      "POST /auth/dev-login": () => {
        signedIn = true;
        return me();
      },
      ...employeeRoutes(),
    });
    renderApp("/login");
    const user = userEvent.setup();
    expect(await screen.findByText("Development sign-in (no password)")).toBeInTheDocument();
    await user.type(screen.getByLabelText("Sign in as (email)"), "rahul.reddy@nexa.example");
    await user.click(screen.getByRole("button", { name: "Dev sign-in" }));
    expect(await screen.findByRole("heading", { name: /Good (morning|afternoon|evening), Rahul/ })).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/auth/dev-login")?.body).toEqual({ email: "rahul.reddy@nexa.example" });
  });
});

describe("employee dashboard", () => {
  it("loads profile and summary metrics from the API", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    expect(await screen.findByRole("heading", { name: /Good (morning|afternoon|evening), Rahul/ })).toBeInTheDocument();
    expect(screen.getByText("EMP001")).toBeInTheDocument();
    expect(screen.getAllByText("Priya Sharma").length).toBeGreaterThan(0);
    expect(await screen.findByText("77.8%")).toBeInTheDocument();
    expect(screen.getByText(formatMinutes(6720))).toBeInTheDocument();
    expect(screen.getAllByText(formatMinutes(480)).length).toBeGreaterThan(1); // avg per day and the shift
    expect(screen.getByText("exactly the shift hours on 14 days with hours")).toBeInTheDocument();
  });

  it("has no check-in, check-out or correction requests", async () => {
    const { calls } = mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    expect(await screen.findByText("Your timesheets up to")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /check in|check out|fix a past day/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "My requests" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /import a timesheet/i })).not.toBeInTheDocument(); // admins only
    expect(calls.some((c) => c.method !== "GET")).toBe(false);
  });

  it("says when hours are not in the timesheet instead of showing a shortfall", async () => {
    mockApi({
      "/auth/me": me(),
      ...employeeRoutes(),
      "/attendance/employees/5/summary": { ...summary, net_minutes: 0, hours_days: 0, hours_required_minutes: 0, variance_minutes: 0, average_net_minutes: null },
    });
    renderApp("/");
    expect(await screen.findByText("Not recorded")).toBeInTheDocument();
    expect(screen.getByText("the timesheet has no hours for these days")).toBeInTheDocument();
    expect(screen.queryByText(/short of the shift hours/)).not.toBeInTheDocument();
  });

  it("renders the attendance history table with statuses", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    const table = await screen.findByRole("table", { name: "Daily attendance records" });
    expect(within(table).getAllByText("Present").length).toBeGreaterThan(0);
    expect(within(table).getByText("Absent")).toBeInTheDocument();
    expect(within(table).getByText("Not in the timesheet")).toBeInTheDocument();
  });

  it("shows the timesheet entry behind a day", async () => {
    mockApi({
      "/auth/me": me(),
      ...employeeRoutes(),
      "/attendance/employees/5/days/2026-10-26": {
        record: day("2026-10-26", { net_minutes: 0, hours_known: false, first_in: null, last_out: null }),
        timesheet: { work_mode: "OFFICE", wfo_source: "UPLOAD_COLUMN", upload_work_mode: "OFFICE", upload_minutes: null, badge_minutes: null,
          first_in: null, last_out: null, upload_filename: "October 2026.xlsx", override_work_mode: null, override_note: null,
          overridden_by_name: null, overridden_at: null, conflicts: [] },
      },
    });
    renderApp("/?day=2026-10-26");
    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByText("October 2026.xlsx")).toBeInTheDocument();
    expect(within(dialog).getByText("Imported timesheet")).toBeInTheDocument();
    expect(within(dialog).getByText("Not recorded")).toBeInTheDocument();
  });
});

describe("date range filter", () => {
  it("applies presets and flags invalid custom ranges", async () => {
    const onChange = vi.fn();
    const { rerender } = render(<DateRangeFilter start="2026-10-01" end="2026-10-27" onChange={onChange} />);
    const user = userEvent.setup();
    await user.selectOptions(screen.getByLabelText("Preset"), "last_7");
    const today = new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Kolkata" }).format(new Date());
    expect(onChange).toHaveBeenCalledWith({ start: addDays(today, -6), end: today });
    rerender(<DateRangeFilter start="2026-10-20" end="2026-10-10" onChange={onChange} />);
    expect(screen.getByRole("alert")).toHaveTextContent("End date is before start date");
  });

  it("computes month boundaries without timezone drift", () => {
    expect(endOfMonth("2024-02-10")).toBe("2024-02-29");
    expect(presetRange("last_month", "2026-03-31")).toEqual({ start: "2026-02-01", end: "2026-02-28" });
    expect(addDays("2026-12-31", 1)).toBe("2027-01-01");
  });
});

describe("manager dashboard", () => {
  it("shows team cards and rows from the API and filters by search", async () => {
    const { calls } = mockApi({
      "/auth/me": me("MANAGER", { full_name: "Priya Sharma", id: 3 }),
      "/dashboard/overview": overview,
      "/dashboard/employees": (url: URL) => {
        const q = url.searchParams.get("search");
        const items = [row(5, "Rahul Reddy"), row(6, "Sneha Rao")].filter((r) => !q || r.full_name.toLowerCase().includes(q.toLowerCase()));
        return { items, total: items.length, page: 1, page_size: 10 };
      },
      "/dashboard/trends": { granularity: "day", headcount: 3, items: [] },
      "/dashboard/exceptions": { ...emptyPage, counts: {} },
    });
    renderApp("/team");
    expect(await screen.findByRole("heading", { name: "Team attendance" })).toBeInTheDocument();
    const table = await screen.findByRole("table", { name: "Team attendance" });
    expect(within(table).getByText("Rahul Reddy")).toBeInTheDocument();
    expect(within(table).getByText("Sneha Rao")).toBeInTheDocument();
    expect(screen.getAllByText("85.0%").length).toBeGreaterThan(0);
    expect(screen.getByText("timesheets up to 30 Sept 2026")).toBeInTheDocument();
    await userEvent.setup().type(screen.getByLabelText("Search employees"), "sne");
    await waitFor(() => expect(within(screen.getByRole("table", { name: "Team attendance" })).queryByText("Rahul Reddy")).not.toBeInTheDocument());
    expect(calls.some((c) => c.path === "/dashboard/employees")).toBe(true);
  });

  it("drills down to an employee when a summary card is clicked (status filter)", async () => {
    const statuses: (string | null)[] = [];
    const { calls } = mockApi({
      "/auth/me": me("MANAGER", { full_name: "Priya Sharma", id: 3 }),
      "/dashboard/overview": overview,
      "/dashboard/employees": (url: URL) => {
        statuses.push(url.searchParams.get("status"));
        return { items: [row(5, "Rahul Reddy")], total: 1, page: 1, page_size: 10 };
      },
      "/dashboard/trends": { granularity: "day", headcount: 3, items: [] },
      "/dashboard/exceptions": { ...emptyPage, counts: {} },
    });
    renderApp("/team");
    await userEvent.setup().click(await screen.findByRole("button", { name: /In office/ }));
    await waitFor(() => expect(statuses).toContain("PRESENT"));
    expect(calls.some((c) => c.path === "/dashboard/employees")).toBe(true);
    expect(screen.getByRole("button", { name: /In office/ })).toHaveAttribute("aria-pressed", "true");
  });
});

describe("large organisations", () => {
  const member = { id: 5, employee_code: "EMP001", full_name: "Rahul Reddy", role: "EMPLOYEE", job_title: "ML Engineer", department_name: "AI Engineering", manager_id: 3, manager_name: "Priya Sharma", is_active: true };

  it("charts only the lowest 12 of a big team, sorted by the server", async () => {
    const requests: URLSearchParams[] = [];
    mockApi({
      "/auth/me": me("MANAGER", { full_name: "Priya Sharma", id: 3 }),
      "/dashboard/overview": { ...overview, headcount: 625 },
      "/dashboard/employees": (url: URL) => {
        requests.push(url.searchParams);
        return { items: [row(5, "Rahul Reddy")], total: 625, page: 1, page_size: Number(url.searchParams.get("page_size")) };
      },
      "/dashboard/trends": { granularity: "day", headcount: 625, items: [] },
      "/dashboard/exceptions": { ...emptyPage, counts: {} },
    });
    renderApp("/team");
    expect(await screen.findByText(/The 12 lowest of 625 by attendance %/)).toBeInTheDocument();
    expect(screen.getByText(/The 12 lowest of 625 by average office hours/)).toBeInTheDocument();
    const ranked = requests.filter((p) => p.get("page_size") === "12");
    expect(ranked.map((p) => p.get("sort")).sort()).toEqual(["attendance_percentage", "average_net_minutes"]);
    expect(ranked.every((p) => p.get("order") === "asc")).toBe(true);
    expect(requests.some((p) => Number(p.get("page_size")) > 50)).toBe(false);
  });

  it("finds a person for a report by searching, never by downloading everyone", async () => {
    const searches: URLSearchParams[] = [];
    const { calls } = mockApi({
      "/auth/me": me("MANAGER", { full_name: "Priya Sharma", id: 3 }),
      "/team/members": (url: URL) => {
        searches.push(url.searchParams);
        const q = (url.searchParams.get("search") ?? "").toLowerCase();
        return [member].filter((m) => m.full_name.toLowerCase().includes(q));
      },
    });
    renderApp("/reports");
    const user = userEvent.setup();
    const box = await screen.findByRole("combobox", { name: "Employee" });
    await user.type(box, "rah");
    await waitFor(() => expect(searches.some((p) => p.get("search") === "rah")).toBe(true)); // debounced
    await user.click(await screen.findByRole("option", { name: /Rahul Reddy/ }));
    expect(screen.getByText("Rahul Reddy")).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Employee" })).not.toBeInTheDocument();
    expect(searches.length).toBeGreaterThan(0);
    expect(searches.every((p) => p.get("limit") === "20")).toBe(true);
    expect(searches.some((p) => p.get("search") === "rah")).toBe(true);
    expect(calls.filter((c) => c.path === "/team/members").length).toBe(searches.length);
  });

  it("opens one employee with a single lookup instead of the whole team list", async () => {
    const { calls } = mockApi({
      "/auth/me": me("MANAGER", { full_name: "Priya Sharma", id: 3 }),
      "/team/members/5": member,
      ...employeeRoutes(),
    });
    renderApp("/people/5");
    expect(await screen.findByText(/EMP001 · ML Engineer · reports to Priya Sharma/)).toBeInTheDocument();
    expect(calls.some((c) => c.path === "/team/members")).toBe(false);
  });

  it("says when a person is outside the manager's scope", async () => {
    mockApi({ "/auth/me": me("MANAGER", { full_name: "Priya Sharma", id: 3 }), "/team/members/99": apiError(404, "Employee not found", "not_found") });
    renderApp("/people/99");
    expect(await screen.findByText("Employee not found")).toBeInTheDocument();
  });
});

describe("navigation and guards", () => {
  it("shows administration links only to admins", async () => {
    mockApi({ "/auth/me": me("ADMIN", { full_name: "System Administrator", id: 1 }), ...employeeRoutes(), "/attendance/employees/1/summary": {}, "/attendance/employees/1/days": emptyPage, "/attendance/employees/1/trends": { items: [] } });
    renderApp("/");
    const nav = await screen.findAllByRole("navigation", { name: "Main" });
    expect(within(nav[0]).getByRole("link", { name: "Organisation" })).toBeInTheDocument();
    expect(within(nav[0]).getByRole("link", { name: "Audit log" })).toBeInTheDocument();
  });

  it("hides team and admin links from employees and redirects /admin", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/admin");
    expect(await screen.findByRole("heading", { name: /Good (morning|afternoon|evening)/ })).toBeInTheDocument();
    const nav = screen.getAllByRole("navigation", { name: "Main" })[0];
    expect(within(nav).queryByRole("link", { name: "Team attendance" })).not.toBeInTheDocument();
    expect(within(nav).queryByRole("link", { name: "Organisation" })).not.toBeInTheDocument();
  });

  it("redirects to login when the session has expired", async () => {
    mockApi({ "/auth/me": apiError(401, "Your session has expired", "session_expired") });
    renderApp("/team");
    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
  });
});

describe("states", () => {
  it("renders error state with retry", async () => {
    const retry = vi.fn();
    render(<ErrorState error={new ApiError(503, "database_unavailable", "The database is temporarily unavailable.")} onRetry={retry} />);
    expect(screen.getByRole("alert")).toHaveTextContent("The database is temporarily unavailable.");
    await userEvent.setup().click(screen.getByRole("button", { name: "Try again" }));
    expect(retry).toHaveBeenCalled();
  });

  it("renders empty state for an empty table", () => {
    render(<DataTable columns={[{ key: "a", header: "A", cell: () => null }]} rows={[]} rowKey={() => 1} />);
    expect(screen.getByText("No records found")).toBeInTheDocument();
  });

  it("shows a friendly error when the backend is unreachable", async () => {
    vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("Failed to fetch"));
    renderApp("/");
    expect(await screen.findByText("Unable to reach the attendance service")).toBeInTheDocument();
  });
});

describe("today panel", () => {
  it("greets by the office clock", async () => {
    const { greeting } = await import("@/components/attendance/TodayCard");
    expect(greeting(new Date("2026-11-04T03:00:00Z"))).toBe("Good morning"); // 08:30 IST
    expect(greeting(new Date("2026-11-04T09:00:00Z"))).toBe("Good afternoon"); // 14:30 IST
    expect(greeting(new Date("2026-11-04T13:00:00Z"))).toBe("Good evening"); // 18:30 IST
  });

  it("shows how far the imported timesheets reach and the shift", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    expect(await screen.findByText("Reports to")).toBeInTheDocument();
    expect(await screen.findByText("30 Sept 2026")).toBeInTheDocument();
    expect(screen.getByText(/Later days show as “No timesheet”/)).toBeInTheDocument();
    expect(screen.getByText("Working days: Mon, Tue, Wed, Thu, Fri")).toBeInTheDocument();
  });

  it("gives the admin a shortcut to import a timesheet", async () => {
    mockApi({ "/auth/me": me("ADMIN", { full_name: "System Administrator", id: 1 }), ...employeeRoutes(), "/attendance/current": current(null),
      "/attendance/employees/1/summary": summary, "/attendance/employees/1/days": { items: [], total: 0, page: 1, page_size: 10 },
      "/attendance/employees/1/trends": { granularity: "month", headcount: 1, items: [] } });
    renderApp("/");
    expect(await screen.findByRole("link", { name: /Import a timesheet/ })).toHaveAttribute("href", "/admin/attendance-import");
    expect(screen.getByText("No timesheet has been imported yet")).toBeInTheDocument();
  });

  it("offers no leave requests", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    expect(await screen.findByText("Your timesheets up to")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /leave/i })).not.toBeInTheDocument();
  });
});

describe("default period", () => {
  it("opens on the latest timesheet month while the current month has none", async () => {
    const { calls } = mockApi({ "/auth/me": { ...me(), latest_timesheet_date: "2026-09-30" }, ...employeeRoutes() });
    renderApp("/");
    expect(await screen.findByText("Your timesheets up to")).toBeInTheDocument();
    await waitFor(() => expect(calls.some((c) => c.path === "/attendance/employees/5/summary")).toBe(true));
    const { setLatestTimesheetDate, defaultMonthDate } = await import("@/lib/dates");
    expect(defaultMonthDate("2026-11-10")).toBe("2026-09-30");
    setLatestTimesheetDate("2026-11-09");
    expect(defaultMonthDate("2026-11-10")).toBe("2026-11-10"); // the current month has a timesheet
    setLatestTimesheetDate(null);
    expect(defaultMonthDate("2026-11-10")).toBe("2026-11-10");
  });
});
