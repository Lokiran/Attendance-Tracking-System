import { CalendarRange, Clock3, FileSpreadsheet, Moon, Sun, Sunrise, Sunset } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";

import { Card } from "@/components/ui/Card";
import { Skeleton } from "@/components/ui/Skeleton";
import { ErrorState } from "@/components/ui/States";
import { formatDate, formatMinutes, getOfficeTimezone } from "@/lib/format";
import { useCurrentStatus } from "@/lib/queries";
import type { Profile } from "@/lib/types";

export type DayPart = "morning" | "afternoon" | "evening" | "night";

/** Part of the day by the office clock, so it matches the attendance day: morning 05–12, afternoon 12–17,
 *  evening 17–21, night 21–05. */
export function dayPart(now: Date = new Date()): DayPart {
  const hour = Number(new Intl.DateTimeFormat("en-GB", { timeZone: getOfficeTimezone(), hour: "numeric", hourCycle: "h23" }).format(now)) % 24;
  if (hour >= 5 && hour < 12) return "morning";
  if (hour >= 12 && hour < 17) return "afternoon";
  if (hour >= 17 && hour < 21) return "evening";
  return "night";
}

export function greeting(now: Date = new Date()): string {
  return `Good ${dayPart(now)}`;
}

const DAY_PART_ICON = { morning: Sunrise, afternoon: Sun, evening: Sunset, night: Moon } as const;

/** The current time, refreshed every minute, so the greeting changes while the page stays open. */
function useNow(intervalMs = 60_000): Date {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = window.setInterval(() => setNow(new Date()), intervalMs);
    return () => window.clearInterval(id);
  }, [intervalMs]);
  return now;
}

const WEEKDAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

/** [1,2,3,4,5] -> "Mon–Fri"; anything not consecutive is listed. */
function workingDays(days: number[]): string {
  const sorted = [...days].sort((a, b) => a - b);
  const consecutive = sorted.length > 2 && sorted.every((d, i) => i === 0 || d === sorted[i - 1] + 1);
  return consecutive ? `${WEEKDAYS[sorted[0] - 1]}–${WEEKDAYS[sorted[sorted.length - 1] - 1]}` : sorted.map((d) => WEEKDAYS[d - 1]).join(", ");
}

function Fact({ icon, label, value, hint }: { icon: ReactNode; label: string; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="flex min-w-0 items-start gap-3 rounded-lg bg-white/[0.06] px-4 py-3 ring-1 ring-white/10">
      <span className="mt-0.5 text-on-secondary-muted" aria-hidden>{icon}</span>
      <div className="min-w-0">
        <div className="text-xs text-on-secondary-muted">{label}</div>
        <div className="tabular text-base font-semibold text-on-secondary">{value}</div>
        {hint && <div className="text-xs text-on-secondary-muted">{hint}</div>}
      </div>
    </div>
  );
}

/** Greeting, shift and how far the imported timesheets reach. Attendance is not recorded in the app:
 *  the Admin imports each month's timesheet. */
export function TodayCard({ user }: { user: Profile }) {
  const status = useCurrentStatus();
  const now = useNow();
  if (status.isError) return <Card><ErrorState error={status.error} onRetry={() => status.refetch()} /></Card>;
  if (!status.data) return <Card className="p-6"><Skeleton className="h-32" /></Card>;

  const s = status.data;
  const name = user.attendance_tracked ? user.full_name.split(" ")[0] : user.full_name;
  const part = dayPart(now);
  const PartIcon = DAY_PART_ICON[part];
  const isAdmin = user.role === "ADMIN";

  const mine = user.attendance_tracked && (s.my_timesheet_through || !s.timesheet_through);
  const through = mine ? s.my_timesheet_through : s.timesheet_through;
  const throughHint = !through
    ? "No timesheet imported yet"
    : mine
      ? "Later days fill in after the next import"
      : user.attendance_tracked ? "You are not in an imported timesheet yet" : "Latest date any timesheet covers";

  const details = [user.employee_code, user.department?.name, user.manager && `Reports to ${user.manager.full_name}`].filter(Boolean);

  return (
    <Card className="overflow-hidden border-0 tone-secondary">
      <div className="grid gap-5 p-5 sm:p-6 lg:grid-cols-[1fr_auto] lg:items-center">
        <div className="min-w-0">
          <p className="flex items-center gap-1.5 text-sm text-on-secondary-muted">
            <PartIcon className="size-4" aria-hidden />
            {formatDate(s.today, { weekday: "long", year: undefined })}
          </p>
          <h1 className="mt-1 text-2xl font-semibold tracking-tight text-on-secondary sm:text-3xl">
            Good {part}, {name}
          </h1>
          {details.length > 0 && (
            <p className="mt-1.5 text-sm text-on-secondary-muted">
              {details.map((d, i) => (
                <span key={i}>
                  {i > 0 && <span aria-hidden className="mx-2">·</span>}
                  <span className={i === 0 ? "tabular" : undefined}>{d}</span>
                </span>
              ))}
            </p>
          )}
        </div>

        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap lg:justify-end">
          <Fact
            icon={<CalendarRange className="size-4" />}
            label={mine ? "Your timesheets up to" : "Timesheets imported up to"}
            value={through ? formatDate(through) : "—"}
            hint={throughHint}
          />
          <Fact
            icon={<Clock3 className="size-4" />}
            label={s.schedule ? s.schedule.name : "Shift"}
            value={s.schedule ? `${s.schedule.start_time.slice(0, 5)}–${s.schedule.end_time.slice(0, 5)}` : "No schedule"}
            hint={s.schedule ? `${formatMinutes(s.schedule.required_minutes)} a day · ${workingDays(s.schedule.working_days)}` : undefined}
          />
          {isAdmin && (
            <Link
              to="/admin/attendance-import"
              className="inline-flex h-11 items-center justify-center gap-2 self-center rounded-lg bg-brand px-4 text-sm font-semibold text-on-brand hover:bg-brand-hover"
            >
              <FileSpreadsheet className="size-4" aria-hidden /> Import a timesheet
            </Link>
          )}
        </div>
      </div>
    </Card>
  );
}
