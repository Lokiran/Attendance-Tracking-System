import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { vi } from "vitest";

import { App, createQueryClient } from "@/App";
import type { MeResponse, Profile, Role } from "@/lib/types";

type Handler = unknown | ((url: URL, init: RequestInit) => unknown);
export interface Call {
  method: string;
  path: string;
  body: unknown;
  headers: Record<string, string>;
}

/** Routes fetch() by "METHOD /path" (or "/path" for GET). Unmatched requests return 404. */
export function mockApi(routes: Record<string, Handler>) {
  const calls: Call[] = [];
  const spy = vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init = {}) => {
    const url = new URL(String(input), "http://localhost");
    const method = (init.method ?? "GET").toUpperCase();
    const path = url.pathname.replace(/^\/api/, "");
    const body = init.body instanceof FormData
      ? Object.fromEntries([...init.body.entries()].map(([k, v]) => [k, v instanceof File ? v.name : v]))
      : init.body ? JSON.parse(String(init.body)) : undefined;
    calls.push({ method, path, body, headers: (init.headers ?? {}) as Record<string, string> });
    const handler = routes[`${method} ${path}`] ?? (method === "GET" ? routes[path] : undefined);
    if (handler === undefined) {
      return new Response(JSON.stringify({ error: { code: "not_found", message: "Not mocked: " + method + " " + path } }), { status: 404 });
    }
    const value = typeof handler === "function" ? (handler as (u: URL, i: RequestInit) => unknown)(url, init) : handler;
    if (value instanceof Response) return value;
    return new Response(JSON.stringify(value), { status: 200, headers: { "Content-Type": "application/json" } });
  });
  return { calls, spy };
}

export function apiError(status: number, message: string, code = "error") {
  return new Response(JSON.stringify({ error: { code, message } }), { status, headers: { "Content-Type": "application/json" } });
}

export function profile(role: Role = "EMPLOYEE", overrides: Partial<Profile> = {}): Profile {
  return {
    id: 5,
    employee_code: "EMP001",
    full_name: "Rahul Reddy",
    email: "rahul.reddy@nexa.example",
    role,
    job_title: "ML Engineer",
    department: { id: 1, name: "AI Engineering" },
    manager: { id: 3, full_name: "Priya Sharma", employee_code: "MGR001" },
    date_joined: "2025-01-01",
    is_active: true,
    attendance_tracked: true,
    ...overrides,
  };
}

export function me(role: Role = "EMPLOYEE", overrides: Partial<Profile> = {}): MeResponse {
  return { user: profile(role, overrides), csrf_token: "csrf-123", office_timezone: "Asia/Kolkata", has_reports: role !== "EMPLOYEE" };
}

export function renderApp(path = "/") {
  const client = createQueryClient();
  client.setDefaultOptions({ queries: { retry: false, staleTime: Infinity, refetchInterval: false } });
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App client={client} />
    </MemoryRouter>,
  );
}

export const emptyPage = { items: [], total: 0, page: 1, page_size: 20 };
