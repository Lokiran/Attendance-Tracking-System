import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import { DataTable } from "@/components/ui/DataTable";
import { DateRangeFilter } from "@/components/ui/DateRangeFilter";
import { ErrorState } from "@/components/ui/States";
import { ApiError } from "@/lib/api";
import { addDays, endOfMonth, presetRange } from "@/lib/dates";
import { formatMinutes } from "@/lib/format";

import { current, employeeRoutes, overview, row } from "./fixtures";
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
});

describe("employee dashboard", () => {
  it("loads profile and summary metrics from the API", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    expect(await screen.findByRole("heading", { name: /Good (morning|afternoon|evening), Rahul/ })).toBeInTheDocument();
    expect(screen.getByText("EMP001")).toBeInTheDocument();
    expect(screen.getAllByText("Priya Sharma").length).toBeGreaterThan(0);
    expect(await screen.findByText("88.9%")).toBeInTheDocument();
    expect(screen.getByText(formatMinutes(7800))).toBeInTheDocument();
    expect(screen.getByText(formatMinutes(487))).toBeInTheDocument();
  });

  it("checks in with the CSRF header and refreshes status", async () => {
    let active = false;
    const { calls } = mockApi({
      "/auth/me": me(),
      ...employeeRoutes(),
      "/attendance/current": () => current(active),
      "POST /attendance/check-in": () => {
        active = true;
        return current(true).active_session;
      },
    });
    renderApp("/");
    const user = userEvent.setup();
    const button = await screen.findByRole("button", { name: "Check in" });
    await user.click(button);
    expect(await screen.findByRole("button", { name: "Check out" })).toBeInTheDocument();
    const post = calls.find((c) => c.method === "POST" && c.path === "/attendance/check-in");
    expect(post?.headers["X-CSRF-Token"]).toBe("csrf-123");
    expect(await screen.findByText(/^Checked in at \d/)).toBeInTheDocument(); // success toast
  });

  it("confirms check-out and sends the break minutes", async () => {
    const { calls } = mockApi({
      "/auth/me": me(),
      ...employeeRoutes(true),
      "POST /attendance/check-out": { ...current(true).active_session, status: "COMPLETED", check_out_at: "2026-10-27T13:00:00Z", net_minutes: 495, break_minutes: 45 },
    });
    renderApp("/");
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Check out" }));
    const dialog = await screen.findByRole("dialog");
    const input = within(dialog).getByLabelText(/Unpaid break/);
    await user.clear(input);
    await user.type(input, "45");
    await user.click(within(dialog).getByRole("button", { name: "Check out" }));
    await waitFor(() => expect(calls.find((c) => c.path === "/attendance/check-out")?.body).toEqual({ break_minutes: 45 }));
  });

  it("surfaces a duplicate check-in error from the server", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes(), "POST /attendance/check-in": apiError(409, "You are already checked in", "already_checked_in") });
    renderApp("/");
    await userEvent.setup().click(await screen.findByRole("button", { name: "Check in" }));
    expect(await screen.findByText("You are already checked in")).toBeInTheDocument();
  });

  it("renders the attendance history table with statuses", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    const table = await screen.findByRole("table", { name: "Daily attendance records" });
    expect(within(table).getAllByText("Present").length).toBeGreaterThan(0);
    expect(within(table).getByText("Absent")).toBeInTheDocument();
    expect(within(table).getByText("Unexplained absence")).toBeInTheDocument();
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
    expect(screen.getAllByText("90.0%").length).toBeGreaterThan(0);
    await userEvent.setup().type(screen.getByLabelText("Search employees"), "sne");
    await waitFor(() => expect(within(screen.getByRole("table", { name: "Team attendance" })).queryByText("Rahul Reddy")).not.toBeInTheDocument());
    expect(calls.some((c) => c.path === "/dashboard/employees")).toBe(true);
  });

  it("drills down to an employee when a summary card is clicked (status filter)", async () => {
    const { calls } = mockApi({
      "/auth/me": me("MANAGER", { full_name: "Priya Sharma", id: 3 }),
      "/dashboard/overview": overview,
      "/dashboard/employees": { items: [row(5, "Rahul Reddy")], total: 1, page: 1, page_size: 10 },
      "/dashboard/trends": { granularity: "day", headcount: 3, items: [] },
      "/dashboard/exceptions": { ...emptyPage, counts: {} },
    });
    renderApp("/team");
    await userEvent.setup().click(await screen.findByRole("button", { name: /Present today/ }));
    await waitFor(() => expect(calls.some((c) => c.path === "/dashboard/employees" && c.method === "GET")).toBe(true));
    expect(screen.getByRole("button", { name: /Present today/ })).toHaveAttribute("aria-pressed", "true");
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
    const { greeting } = await import("@/components/attendance/AttendanceActionCard");
    expect(greeting(new Date("2026-11-04T03:00:00Z"))).toBe("Good morning"); // 08:30 IST
    expect(greeting(new Date("2026-11-04T09:00:00Z"))).toBe("Good afternoon"); // 14:30 IST
    expect(greeting(new Date("2026-11-04T13:00:00Z"))).toBe("Good evening"); // 18:30 IST
  });

  it("shows worked time including the running session, a target bar and plain-language summaries", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes(true) });
    renderApp("/");
    expect(await screen.findByText("Reports to")).toBeInTheDocument();
    // checked in 04:00Z, server time 06:30Z -> 2h 30m so far, against an 8h target
    expect(await screen.findByText("2h 30m")).toBeInTheDocument();
    expect(screen.getByText(/of 8h 00m target · includes current session/)).toBeInTheDocument();
    expect(screen.getByRole("progressbar", { name: /today's target/ })).toHaveAttribute("aria-valuenow", "31");
    expect(await screen.findByText("14h 00m short of 144h 00m target")).toBeInTheDocument();
    expect(screen.getByText("Hours worked")).toBeInTheDocument();
  });

  it("opens the leave form from the quick actions", async () => {
    mockApi({ "/auth/me": me(), ...employeeRoutes() });
    renderApp("/");
    await userEvent.setup().click(await screen.findByRole("button", { name: "Request leave" }));
    expect(await screen.findByRole("dialog", { name: "Request leave" })).toBeInTheDocument();
  });
});
