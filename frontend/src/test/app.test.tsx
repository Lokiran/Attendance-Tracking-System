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

  it("shows or hides the password and signs in with it", async () => {
    let signedIn = false;
    const { calls } = mockApi({
      "/auth/me": () => (signedIn ? me() : apiError(401, "Not signed in")),
      "/auth/providers": { password: true, entra: false, dev: false },
      "POST /auth/login": () => { signedIn = true; return me(); },
      ...employeeRoutes(),
    });
    renderApp("/login");
    const user = userEvent.setup();
    const password = await screen.findByLabelText("Password");
    expect(password).toHaveAttribute("type", "password");
    await user.click(screen.getByRole("button", { name: "Show password" }));
    expect(password).toHaveAttribute("type", "text");
    await user.click(screen.getByRole("button", { name: "Hide password" }));
    expect(password).toHaveAttribute("type", "password");
    await user.type(screen.getByLabelText("Email"), "rahul.reddy@nexa.example");
    await user.type(password, "Secret12345");
    await user.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("heading", { name: /Good (morning|afternoon|evening), Rahul/ })).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/auth/login")?.body).toEqual({ email: "rahul.reddy@nexa.example", password: "Secret12345" });
  });

  it("dev sign-in accepts an employee ID", async () => {
    let signedIn = false;
    const { calls } = mockApi({
      "/auth/me": () => (signedIn ? me() : apiError(401, "Not signed in")),
      "/auth/providers": { password: true, entra: false, dev: true },
      "POST /auth/dev-login": () => { signedIn = true; return me(); },
      ...employeeRoutes(),
    });
    renderApp("/login");
    const user = userEvent.setup();
    await user.click(await screen.findByText("Development sign-in (no password)")); // collapsed next to the password form
    await user.type(screen.getByLabelText("Sign in as (email or employee ID)"), "Intern - 22026");
    await user.click(screen.getByRole("button", { name: "Dev sign-in" }));
    await waitFor(() => expect(calls.find((c) => c.path === "/auth/dev-login")?.body).toEqual({ email: "Intern - 22026" }));
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
    await user.type(screen.getByLabelText("Sign in as (email or employee ID)"), "rahul.reddy@nexa.example");
    await user.click(screen.getByRole("button", { name: "Dev sign-in" }));
    expect(await screen.findByRole("heading", { name: /Good (morning|afternoon|evening), Rahul/ })).toBeInTheDocument();
    expect(calls.find((c) => c.path === "/auth/dev-login")?.body).toEqual({ email: "rahul.reddy@nexa.example" });
  });
});

describe("employee dashboard", () => {
  it("loads profile and summary metrics from the API", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    expect(await screen.findByRole("heading", { name: /Good (morning|afternoon|evening|night), Rahul/ })).toBeInTheDocument();
    expect(screen.getByText("EMP001")).toBeInTheDocument();
    expect(screen.getByText("Reports to Priya Sharma")).toBeInTheDocument();
    expect(await screen.findByText("77.8%")).toBeInTheDocument();
    // Office hours panel: the average in office against the shift, and the required hours
    expect(screen.getByText("Average office hours")).toBeInTheDocument();
    expect(screen.getByText("8h 00m")).toBeInTheDocument();
    expect(screen.getByText("per day · shift 8h 00m")).toBeInTheDocument();
    expect(screen.getByText(formatMinutes(8640))).toBeInTheDocument();
    expect(screen.getByText("18 days × 8h 00m")).toBeInTheDocument();
    // WFO panel: 14 of 12 -> green "On target"
    expect(screen.getByText("of 12 days required")).toBeInTheDocument();
    expect(screen.getAllByText("On target").length).toBeGreaterThan(0); // the status pill (and the chart legend)
    expect(screen.getByText("2 days above the 12-day minimum")).toBeInTheDocument();
  });

  it("colours WFO days amber when close and red when far below the requirement", async () => {
    for (const [wfo, label, message] of [[10, "Close", "2 more office days needed to reach 12"], [6, "Below target", "6 more office days needed to reach 12"]] as const) {
      mockApi({ "/auth/me": me(), ...employeeRoutes(), "/attendance/employees/5/summary": { ...summary, wfo: { ...summary.wfo!, wfo_days: wfo, wfo_band: undefined } } });
      const { unmount } = renderApp("/");
      expect(await screen.findByText(message)).toBeInTheDocument();
      expect(screen.getAllByText(label).some((el) => el.closest(".rounded-full"))).toBe(true); // the status pill
      unmount();
    }
  });

  it("shows office days month by month and leaves out months before the first timesheet", async () => {
    const { calls } = mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    expect(await screen.findByText("Month by month")).toBeInTheDocument();
    await waitFor(() => expect(calls.some((c) => c.path === "/attendance/employees/5/months")).toBe(true));
    const user = userEvent.setup();
    const card = screen.getByText("Month by month").closest("section, div.rounded-xl") as HTMLElement;
    await user.click(within(card).getByRole("button", { name: /table/i }));
    const table = await within(card).findByRole("table");
    expect(within(table).queryByText("Jul 2026")).not.toBeInTheDocument(); // no timesheet yet: not shown
    expect(within(table).getByRole("row", { name: /Aug 2026 10 12 Close/ })).toBeInTheDocument();
    expect(within(table).getByRole("row", { name: /Sep 2026 14 12 On target .* 7h 54m/ })).toBeInTheDocument();
  });

  it("exports my attendance as PDF, Excel or CSV", async () => {
    const file = (name: string) => () => new Response("x", { status: 200, headers: { "content-disposition": `attachment; filename="${name}"` } });
    const { calls } = mockApi({
      "/auth/me": me(), ...employeeRoutes(),
      "/reports/employee-attendance.pdf": file("attendance_EMP001.pdf"),
      "/reports/employee-attendance.xlsx": file("attendance_EMP001.xlsx"),
    });
    URL.createObjectURL = vi.fn(() => "blob:x");
    URL.revokeObjectURL = vi.fn();
    renderApp("/");
    const user = userEvent.setup();
    const button = await screen.findByRole("button", { name: "Export" });
    expect(screen.queryByRole("menu")).not.toBeInTheDocument();
    await user.click(button);
    const menu = screen.getByRole("menu", { name: "Export" });
    expect(within(menu).getAllByRole("menuitem").map((i) => i.textContent)).toEqual([
      expect.stringContaining("PDF report"), expect.stringContaining("Excel workbook"), expect.stringContaining("CSV")]);
    await user.click(within(menu).getByRole("menuitem", { name: /PDF report/ }));
    expect(await screen.findByText("Downloaded attendance_EMP001.pdf")).toBeInTheDocument();
    expect(screen.queryByRole("menu")).not.toBeInTheDocument();
    const pdf = calls.find((c) => c.path === "/reports/employee-attendance.pdf")!;
    expect(pdf).toBeDefined();
    await user.click(screen.getByRole("button", { name: "Export" }));
    await user.click(screen.getByRole("menuitem", { name: /Excel workbook/ }));
    expect(await screen.findByText("Downloaded attendance_EMP001.xlsx")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Export" }));
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("menu")).not.toBeInTheDocument();
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
      "/attendance/employees/5/days": { items: [day("2026-10-26", { net_minutes: 0, hours_known: false, first_in: null, last_out: null })], total: 1, page: 1, page_size: 10 },
    });
    renderApp("/");
    expect(await screen.findByText(/Not in this timesheet/)).toBeInTheDocument();
    expect(screen.queryByRole("progressbar", { name: /average office hours/i })).not.toBeInTheDocument();
    // No empty hours charts: the week-by-week view takes their place beside the calendar.
    expect(await screen.findByText("Week by week")).toBeInTheDocument();
    expect(screen.queryByText("Actual vs required hours")).not.toBeInTheDocument();
    expect(screen.queryByText("Office hours per day")).not.toBeInTheDocument();
    expect(screen.queryByText(/short of the shift hours/)).not.toBeInTheDocument();
    expect(screen.getByText(formatMinutes(8640))).toBeInTheDocument(); // required hours are always known
  });

  it("renders the attendance history table with statuses", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    const table = await screen.findByRole("table", { name: "Daily attendance records" });
    expect(within(table).getAllByText("Present").length).toBeGreaterThan(0);
    expect(within(table).getByText("Absent")).toBeInTheDocument();
    expect(within(table).getByText("Not in the timesheet")).toBeInTheDocument();
  });

  it("filters the history from the attendance counts and opens the hours from the KPI panel", async () => {
    const statuses: (string | null)[] = [];
    mockApi({
      "/auth/me": me(),
      ...employeeRoutes(),
      "/attendance/employees/5/days": (url: URL) => {
        statuses.push(url.searchParams.get("status"));
        return { items: [day("2026-10-26")], total: 1, page: 1, page_size: 10 };
      },
    });
    Element.prototype.scrollIntoView = vi.fn();
    renderApp("/");
    const user = userEvent.setup();
    await screen.findByText("77.8%");
    const absent = screen.getByRole("button", { name: /^1\s*Absent$/ });
    await user.click(absent);
    expect(absent).toHaveAttribute("aria-pressed", "true");
    await waitFor(() => expect(statuses).toContain("ABSENT"));
    expect(await screen.findByText(/showing 1 absent day/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Clear filter" }));
    expect(absent).toHaveAttribute("aria-pressed", "false");
    // The hours panel opens the daily chart, which can switch to arrival and leaving times.
    await user.click(screen.getByRole("button", { name: /Average office hours: show hours per day/ }));
    expect(Element.prototype.scrollIntoView).toHaveBeenCalled();
    await user.click(screen.getByRole("tab", { name: "In & out" }));
    expect(await screen.findByText("Arrival and leaving")).toBeInTheDocument();
  });

  it("shows a day against the shift, with the week around it", async () => {
    mockApi({
      "/auth/me": me(),
      ...employeeRoutes(),
      "/attendance/employees/5/days/2026-10-26": {
        record: day("2026-10-26", { net_minutes: 445, exceptions: ["LATE_ARRIVAL", "SHORT_HOURS"], first_in: "2026-10-26T04:22:00Z", last_out: "2026-10-26T11:47:00Z" }),
        timesheet: { work_mode: "OFFICE", wfo_source: "UPLOAD_COLUMN", upload_work_mode: "OFFICE", upload_minutes: 445, badge_minutes: null,
          first_in: "2026-10-26T04:22:00Z", last_out: "2026-10-26T11:47:00Z", upload_filename: "October 2026.pdf", override_work_mode: null,
          override_note: null, overridden_by_name: null, overridden_at: null, conflicts: [] },
        shift: { name: "General shift", start_time: "09:30:00", end_time: "18:30:00", required_minutes: 480, working_days: [1, 2, 3, 4, 5] },
        late_grace_minutes: 15,
      },
    });
    renderApp("/?day=2026-10-26");
    const dialog = await screen.findByRole("dialog");
    // 09:52 in (22 minutes after a 09:30 start), 17:17 out, 7h 25m: 35m short of the 8h shift.
    expect(await within(dialog).findByText("09:52 am")).toBeInTheDocument();
    expect(within(dialog).getByText("22m after shift start")).toBeInTheDocument();
    expect(within(dialog).getByText("1h 13m before shift end")).toBeInTheDocument();
    expect(within(dialog).getByText("35m short of the 8h 00m shift")).toBeInTheDocument();
    expect(within(dialog).getByRole("img", { name: /In 09:52 am, out 05:17 pm; shift 09:30 am to 06:30 pm/ })).toBeInTheDocument();
    expect(within(dialog).getByText(/Late arrival: in after 09:45 am/)).toBeInTheDocument();
    expect(within(dialog).getByText("That week")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Previous day" })).toBeInTheDocument();
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
    expect(greeting(new Date("2026-11-03T23:30:00Z"))).toBe("Good morning"); // 05:00 IST
    expect(greeting(new Date("2026-11-04T06:30:00Z"))).toBe("Good afternoon"); // 12:00 IST
    expect(greeting(new Date("2026-11-04T09:00:00Z"))).toBe("Good afternoon"); // 14:30 IST
    expect(greeting(new Date("2026-11-04T11:30:00Z"))).toBe("Good evening"); // 17:00 IST
    expect(greeting(new Date("2026-11-04T13:00:00Z"))).toBe("Good evening"); // 18:30 IST
    expect(greeting(new Date("2026-11-04T15:30:00Z"))).toBe("Good night"); // 21:00 IST
    expect(greeting(new Date("2026-11-04T18:30:00Z"))).toBe("Good night"); // 00:00 IST
    expect(greeting(new Date("2026-11-03T23:29:00Z"))).toBe("Good night"); // 04:59 IST
  });

  it("shows how far the imported timesheets reach and the shift", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    expect(await screen.findByText(/^Reports to /)).toBeInTheDocument();
    expect(await screen.findByText("30 Sept 2026")).toBeInTheDocument();
    expect(screen.getByText(/Later days fill in after the next import/)).toBeInTheDocument();
    expect(screen.getByText("09:30–18:30")).toBeInTheDocument();
    expect(screen.getByText("8h 00m a day · Mon–Fri")).toBeInTheDocument();
    expect(screen.queryByText("Last import")).not.toBeInTheDocument();
  });

  it("gives the admin a shortcut to import a timesheet", async () => {
    mockApi({ "/auth/me": me("ADMIN", { full_name: "System Administrator", id: 1 }), ...employeeRoutes(), "/attendance/current": current(null),
      "/attendance/employees/1/summary": summary, "/attendance/employees/1/days": { items: [], total: 0, page: 1, page_size: 10 },
      "/attendance/employees/1/trends": { granularity: "month", headcount: 1, items: [] } });
    renderApp("/");
    expect(await screen.findByRole("link", { name: /Import a timesheet/ })).toHaveAttribute("href", "/admin/attendance-import");
    expect(screen.getByText("No timesheet imported yet")).toBeInTheDocument();
  });

  it("gives accounts left out of attendance shortcuts instead of an empty dashboard", async () => {
    const { calls } = mockApi({ "/auth/me": me("ADMIN", { full_name: "System Administrator", id: 1, attendance_tracked: false }), "/attendance/current": current() });
    renderApp("/");
    expect(await screen.findByText(/Your own attendance is not tracked/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Org office days.*Who met/ })).toHaveAttribute("href", "/admin/wfo");
    expect(await screen.findByText("Timesheets imported up to")).toBeInTheDocument();
    expect(screen.queryByText("Work from office")).not.toBeInTheDocument();
    expect(calls.some((c) => c.path.startsWith("/attendance/employees/"))).toBe(false);
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
