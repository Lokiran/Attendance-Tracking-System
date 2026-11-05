/** Display helpers. All timestamps are rendered in the office timezone reported by the API. */

import type { DayStatus } from "./types";

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
  ABSENT: "Absent",
  ON_LEAVE: "On leave",
  HOLIDAY: "Holiday",
  WEEKEND: "Weekend",
  INCOMPLETE: "Incomplete",
  NOT_YET_CHECKED_IN: "Not checked in",
  NOT_SCHEDULED: "Not scheduled",
};

export const EXCEPTION_LABEL: Record<string, string> = {
  MISSING_CHECKOUT: "Missing check-out",
  LATE_ARRIVAL: "Late arrival",
  SHORT_HOURS: "Short hours",
  BELOW_MINIMUM: "Below minimum",
  ABSENT: "Unexplained absence",
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
