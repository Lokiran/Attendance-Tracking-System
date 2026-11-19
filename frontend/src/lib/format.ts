/** Display helpers. All timestamps are rendered in the office timezone reported by the API. */

import type { DayStatus, Role } from "./types";

let officeTz = "Asia/Kolkata";
export function setOfficeTimezone(tz: string) {
  officeTz = tz;
}
export function getOfficeTimezone() {
  return officeTz;
}

const cache = new Map<string, Intl.DateTimeFormat>();
function fmt(opts: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = officeTz + JSON.stringify(opts);
  let f = cache.get(key);
  if (!f) {
    f = new Intl.DateTimeFormat("en-IN", { timeZone: officeTz, ...opts });
    cache.set(key, f);
  }
  return f;
}

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  return fmt({ hour: "2-digit", minute: "2-digit", hour12: true }).format(new Date(iso));
}

/** Minutes since midnight in the office timezone ("2026-09-02T04:07:00Z" -> 577 for 09:37 IST). */
export function minutesOfDay(iso: string | null | undefined): number | null {
  if (!iso) return null;
  const parts = fmt({ hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).formatToParts(new Date(iso));
  const h = Number(parts.find((p) => p.type === "hour")?.value ?? 0);
  const m = Number(parts.find((p) => p.type === "minute")?.value ?? 0);
  return (h % 24) * 60 + m;
}

/** A clock time from minutes since midnight or "HH:MM[:SS]": 577 -> "09:37 am". */
export function clockTime(v: number | string | null | undefined): string {
  if (v === null || v === undefined || v === "") return "—";
  const total = typeof v === "number" ? v : Number(v.slice(0, 2)) * 60 + Number(v.slice(3, 5));
  const h = Math.floor(total / 60) % 24;
  const m = Math.round(total % 60);
  return `${String(h % 12 || 12).padStart(2, "0")}:${String(m).padStart(2, "0")} ${h < 12 ? "am" : "pm"}`;
}

/** A duration that reads naturally: 35 -> "35m", 73 -> "1h 13m". */
export function duration(min: number): string {
  const abs = Math.abs(Math.round(min));
  return abs >= 60 ? formatMinutes(abs) : `${abs}m`;
}

/** Signed difference in words: 14 -> "14m later", -46 -> "46m earlier" (with custom words). */
export function signedMinutes(diff: number, later = "later", earlier = "earlier"): string {
  if (Math.abs(diff) < 1) return "on time";
  const abs = Math.abs(Math.round(diff));
  const text = abs >= 60 ? formatMinutes(abs) : `${abs}m`;
  return `${text} ${diff > 0 ? later : earlier}`;
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  return fmt({ day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", hour12: true }).format(
    new Date(iso),
  );
}

/** Formats a calendar date string (YYYY-MM-DD) without timezone shifting. */
export function formatDate(d: string | null | undefined, opts: Intl.DateTimeFormatOptions = {}): string {
  if (!d) return "—";
  const [y, m, day] = d.split("-").map(Number);
  return new Intl.DateTimeFormat("en-IN", {
    timeZone: "UTC",
    day: "2-digit",
    month: "short",
    year: "numeric",
    ...opts,
  }).format(new Date(Date.UTC(y, m - 1, day)));
}

/** 485 -> "8h 05m" */
export function formatMinutes(min: number | null | undefined): string {
  if (min === null || min === undefined) return "—";
  const sign = min < 0 ? "-" : "";
  const abs = Math.abs(Math.round(min));
  const h = Math.floor(abs / 60);
  const m = abs % 60;
  return `${sign}${h}h ${String(m).padStart(2, "0")}m`;
}

export function hours(min: number | null | undefined, digits = 1): number | null {
  if (min === null || min === undefined) return null;
  return Number((min / 60).toFixed(digits));
}

export function formatPercent(p: number | null | undefined): string {
  return p === null || p === undefined ? "—" : `${p.toFixed(1)}%`;
}

export const STATUS_LABEL: Record<DayStatus, string> = {
  PRESENT: "Present",
  WFH: "WFH",
  ABSENT: "Absent",
  LEAVE: "Leave",
  HOLIDAY: "Holiday",
  WEEKEND: "Weekend",
  NO_TIMESHEET: "No timesheet",
  NOT_SCHEDULED: "Not scheduled",
};

export const EXCEPTION_LABEL: Record<string, string> = {
  LATE_ARRIVAL: "Late arrival",
  SHORT_HOURS: "Short hours",
  BELOW_MINIMUM: "Below minimum",
  ABSENT: "Not in the timesheet",
};

export function exceptionLabel(code: string): string {
  return EXCEPTION_LABEL[code] ?? code;
}

export function titleCase(s: string): string {
  return s
    .toLowerCase()
    .split("_")
    .map((w) => w.charAt(0).toUpperCase() + w.slice(1))
    .join(" ");
}

/** Display names for roles. ADMIN is the HR admin (one per organisation). */
export const ROLE_LABEL: Record<Role, string> = { EMPLOYEE: "Employee", MANAGER: "Manager", ADMIN: "HR admin" };
