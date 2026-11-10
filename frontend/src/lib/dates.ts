/** Calendar-date helpers working on "YYYY-MM-DD" strings (office-local dates, no timezone math). */

import { getOfficeTimezone } from "./format";

let latestTimesheet: string | null = null;

/** Set from /auth/me: the last date any imported timesheet covers. */
export function setLatestTimesheetDate(d: string | null | undefined) {
  latestTimesheet = d ?? null;
}

/**
 * The month pages open on: the current month, or, while no timesheet covers the current month yet, the
 * month of the latest imported timesheet (so nobody lands on an empty month). Returns any date in it.
 */
export function defaultMonthDate(today: string = todayInOffice()): string {
  if (latestTimesheet && latestTimesheet < today.slice(0, 8) + "01") return latestTimesheet;
  return today;
}

export function todayInOffice(now: Date = new Date()): string {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: getOfficeTimezone(),
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).format(now);
  return parts; // en-CA yields YYYY-MM-DD
}

function toUtc(d: string): Date {
  const [y, m, day] = d.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, day));
}

function fromUtc(d: Date): string {
  return d.toISOString().slice(0, 10);
}

export function addDays(d: string, n: number): string {
  const x = toUtc(d);
  x.setUTCDate(x.getUTCDate() + n);
  return fromUtc(x);
}

export function startOfMonth(d: string): string {
  return `${d.slice(0, 7)}-01`;
}

export function endOfMonth(d: string): string {
  const x = toUtc(startOfMonth(d));
  x.setUTCMonth(x.getUTCMonth() + 1);
  x.setUTCDate(0);
  return fromUtc(x);
}

export function addMonths(d: string, n: number): string {
  const x = toUtc(startOfMonth(d));
  x.setUTCMonth(x.getUTCMonth() + n);
  return fromUtc(x);
}

export function minDate(a: string, b: string): string {
  return a < b ? a : b;
}

/** ISO weekday 1 (Mon) .. 7 (Sun) */
export function isoWeekday(d: string): number {
  const w = toUtc(d).getUTCDay();
  return w === 0 ? 7 : w;
}

export function monthLabel(d: string): string {
  return new Intl.DateTimeFormat("en-IN", { timeZone: "UTC", month: "long", year: "numeric" }).format(toUtc(d));
}

export type RangePreset = "this_month" | "last_month" | "last_7" | "last_30" | "last_90" | "custom";

export function presetRange(preset: RangePreset, today: string): { start: string; end: string } {
  switch (preset) {
    case "last_month": {
      const s = addMonths(today, -1);
      return { start: s, end: endOfMonth(s) };
    }
    case "last_7":
      return { start: addDays(today, -6), end: today };
    case "last_30":
      return { start: addDays(today, -29), end: today };
    case "last_90":
      return { start: addDays(today, -89), end: today };
    default:
      return { start: startOfMonth(today), end: today };
  }
}
