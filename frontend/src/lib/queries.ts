import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { api, ApiError, type Params } from "./api";
import { DASHBOARD_REFRESH_MS } from "./config";
import type {
  CurrentStatus,
  DayDetail,
  DayRecord,
  DepartmentStats,
  EmployeeRow,
  EmployeeWfo,
  ExceptionsResponse,
  ManagerStats,
  OrgWfo,
  Overview,
  Page,
  Session,
  Summary,
  TeamWfo,
  Trends,
  WorkLocation,
} from "./types";

/** Query-key roots; invalidating a root refreshes every query beneath it. */
export const qk = {
  me: ["me"] as const,
  current: ["attendance", "current"] as const,
  attendance: ["attendance"] as const,
  dashboard: ["dashboard"] as const,
  corrections: ["corrections"] as const,
  admin: ["admin"] as const,
  wfo: ["wfo"] as const,
};

export type ScopeParams = {
  manager_id?: number | null;
  department_id?: number | null;
};

const live = { refetchInterval: DASHBOARD_REFRESH_MS, placeholderData: keepPreviousData } as const;

export function useCurrentStatus() {
  return useQuery({
    queryKey: qk.current,
    queryFn: () => api.get<CurrentStatus>("/attendance/current"),
    refetchInterval: DASHBOARD_REFRESH_MS,
  });
}

export function useEmployeeSummary(id: number | undefined, start: string, end: string) {
  return useQuery({
    queryKey: [...qk.attendance, "summary", id, start, end],
    queryFn: () => api.get<Summary>(`/attendance/employees/${id}/summary`, { start, end }),
    enabled: !!id,
    placeholderData: keepPreviousData,
  });
}

export function useEmployeeDays(
  id: number | undefined,
  params: { start: string; end: string; page?: number; page_size?: number; status?: string; order?: string; exceptions_only?: boolean },
) {
  return useQuery({
    queryKey: [...qk.attendance, "days", id, params],
    queryFn: () => api.get<Page<DayRecord>>(`/attendance/employees/${id}/days`, params as Params),
    enabled: !!id,
    placeholderData: keepPreviousData,
  });
}

export function useEmployeeTrends(id: number | undefined, start: string, end: string, granularity: string) {
  return useQuery({
    queryKey: [...qk.attendance, "trends", id, start, end, granularity],
    queryFn: () => api.get<Trends>(`/attendance/employees/${id}/trends`, { start, end, granularity }),
    enabled: !!id,
    placeholderData: keepPreviousData,
  });
}

export function useDayDetail(id: number | undefined, day: string | null) {
  return useQuery({
    queryKey: [...qk.attendance, "day", id, day],
    queryFn: () => api.get<DayDetail>(`/attendance/employees/${id}/days/${day}`),
    enabled: !!id && !!day,
  });
}

export function useOverview(params: ScopeParams & { start: string; end: string; on_date?: string }) {
  return useQuery({
    queryKey: [...qk.dashboard, "overview", params],
    queryFn: () => api.get<Overview>("/dashboard/overview", params as Params),
    ...live,
  });
}

export function useEmployeeRows(params: ScopeParams & Params & { start: string; end: string }) {
  return useQuery({
    queryKey: [...qk.dashboard, "employees", params],
    queryFn: () => api.get<Page<EmployeeRow>>("/dashboard/employees", params),
    ...live,
  });
}

export function useTeamTrends(params: ScopeParams & { start: string; end: string; granularity: string }) {
  return useQuery({
    queryKey: [...qk.dashboard, "trends", params],
    queryFn: () => api.get<Trends>("/dashboard/trends", params as Params),
    ...live,
  });
}

export function useExceptions(params: ScopeParams & Params & { start: string; end: string }) {
  return useQuery({
    queryKey: [...qk.dashboard, "exceptions", params],
    queryFn: () => api.get<ExceptionsResponse>("/dashboard/exceptions", params),
    ...live,
  });
}

export function useDepartmentStats(params: { start: string; end: string; on_date?: string }) {
  return useQuery({
    queryKey: [...qk.dashboard, "departments", params],
    queryFn: () => api.get<DepartmentStats[]>("/dashboard/departments", params as Params),
    ...live,
  });
}

export function useManagerStats(params: { start: string; end: string; on_date?: string; department_id?: number | null }) {
  return useQuery({
    queryKey: [...qk.dashboard, "managers", params],
    queryFn: () => api.get<ManagerStats[]>("/dashboard/managers", params as Params),
    ...live,
  });
}

/** Refreshes everything attendance-derived after a write. */
export function useInvalidateAttendance() {
  const qc = useQueryClient();
  return () =>
    Promise.all([
      qc.invalidateQueries({ queryKey: qk.attendance }),
      qc.invalidateQueries({ queryKey: qk.dashboard }),
      qc.invalidateQueries({ queryKey: qk.corrections }),
      qc.invalidateQueries({ queryKey: qk.wfo }),
    ]);
}

export function useCheckIn() {
  const invalidate = useInvalidateAttendance();
  return useMutation({
    mutationFn: (workLocation?: WorkLocation | null) =>
      api.post<Session>("/attendance/check-in", workLocation ? { work_location: workLocation } : {}),
    onSettled: invalidate,
  });
}

export function useCheckOut() {
  const invalidate = useInvalidateAttendance();
  return useMutation({
    mutationFn: (breakMinutes: number) => api.post<Session>("/attendance/check-out", { break_minutes: breakMinutes }),
    onSettled: invalidate,
  });
}

// ------------------------------------------------------------------ WFO

export function useMyWfo(month: string) {
  return useQuery({ queryKey: [...qk.wfo, "me", month], queryFn: () => api.get<EmployeeWfo>("/me/wfo", { month }) });
}

export function useMemberWfo(employeeId: number | undefined, month: string) {
  return useQuery({
    queryKey: [...qk.wfo, "member", employeeId, month],
    queryFn: () => api.get<EmployeeWfo>(`/team/wfo/employees/${employeeId}`, { month }),
    enabled: !!employeeId,
    retry: (count, e) => !(e instanceof ApiError && e.status === 403) && count < 2,
  });
}

export function useTeamWfo(month: string) {
  return useQuery({ queryKey: [...qk.wfo, "team", month], queryFn: () => api.get<TeamWfo>("/team/wfo", { month }) });
}

export function useOrgWfo(params: { month: string; manager_id?: string; department_id?: string }) {
  return useQuery({
    queryKey: [...qk.wfo, "org", params],
    queryFn: () => api.get<OrgWfo>("/org/wfo", { month: params.month, manager_id: params.manager_id || undefined,
      department_id: params.department_id || undefined }),
  });
}
