import { FileSpreadsheet } from "lucide-react";
import type { ReactNode } from "react";
import { Link } from "react-router-dom";

import { Card } from "@/components/ui/Card";
import { Skeleton } from "@/components/ui/Skeleton";
import { ErrorState } from "@/components/ui/States";
import { formatDate, formatDateTime, formatMinutes, getOfficeTimezone } from "@/lib/format";
import { useCurrentStatus } from "@/lib/queries";
import type { Profile } from "@/lib/types";

/** "Good morning" etc. by the office clock, so it matches the attendance day. */
export function greeting(now: Date = new Date()): string {
  const hour = Number(new Intl.DateTimeFormat("en-GB", { timeZone: getOfficeTimezone(), hour: "numeric", hour12: false }).format(now));
  if (hour < 12) return "Good morning";
  if (hour < 17) return "Good afternoon";
  return "Good evening";
}

function Tile({ label, value, hint }: { label: string; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="px-5 py-4 sm:px-6">
      <dt className="text-xs font-medium text-ink-3">{label}</dt>
      <dd className="tabular mt-1 text-xl font-semibold tracking-tight text-ink">{value}</dd>
      {hint && <dd className="mt-0.5 text-xs text-ink-3">{hint}</dd>}
    </div>
  );
}

/** Greeting, shift and how far the imported timesheets reach. Attendance is not recorded in the app:
 *  the Admin imports each month's timesheet. */
export function TodayCard({ user }: { user: Profile }) {
  const status = useCurrentStatus();
  if (status.isError) return <Card><ErrorState error={status.error} onRetry={() => status.refetch()} /></Card>;
  if (!status.data) return <Card className="p-6"><Skeleton className="h-36" /></Card>;

  const s = status.data;
  const name = user.attendance_tracked ? user.full_name.split(" ")[0] : user.full_name;
  const shift = s.schedule ? `${s.schedule.name} ${s.schedule.start_time.slice(0, 5)}–${s.schedule.end_time.slice(0, 5)}` : "No working schedule today";
  const isAdmin = user.role === "ADMIN";

  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-start justify-between gap-4 tone-secondary p-5 sm:p-6">
        <div className="min-w-0">
          <p className="text-sm text-on-secondary-muted">
            {formatDate(s.today, { weekday: "long", year: undefined })} · {shift}
          </p>
          <h1 className="mt-1 text-2xl font-semibold tracking-tight text-on-secondary">
            {greeting()}, {name}
          </h1>
          <p className="mt-1.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-on-secondary-muted">
            <span className="tabular">{user.employee_code}</span>
            <span aria-hidden>·</span>
            <span>{user.department?.name ?? "No department"}</span>
            {user.manager && (
              <>
                <span aria-hidden>·</span>
                <span>
                  Reports to <span className="text-on-secondary">{user.manager.full_name}</span>
                </span>
              </>
            )}
          </p>
        </div>
        {isAdmin && (
          <Link
            to="/admin/attendance-import"
            className="inline-flex h-10 items-center gap-2 rounded-lg bg-brand px-4 text-sm font-semibold text-on-brand hover:bg-brand-hover"
          >
            <FileSpreadsheet className="size-4" aria-hidden /> Import a timesheet
          </Link>
        )}
      </div>

      <dl className="grid grid-cols-1 divide-y divide-line border-t border-line bg-surface-2/60 sm:grid-cols-3 sm:divide-x sm:divide-y-0">
        {user.attendance_tracked && (s.my_timesheet_through || !s.timesheet_through) ? (
          <Tile
            label="Your timesheets up to"
            value={s.my_timesheet_through ? formatDate(s.my_timesheet_through) : "—"}
            hint={s.my_timesheet_through ? "Later days show as “No timesheet” until the next import" : "No timesheet has been imported yet"}
          />
        ) : (
          <Tile
            label="Timesheets imported up to"
            value={s.timesheet_through ? formatDate(s.timesheet_through) : "—"}
            hint={s.timesheet_through ? (user.attendance_tracked ? "You are not in an imported timesheet yet" : "Latest date any imported timesheet covers") : "No timesheet has been imported yet"}
          />
        )}
        <Tile label="Last import" value={s.last_import_at ? formatDateTime(s.last_import_at) : "—"} hint="Imported by HR from the monthly timesheet" />
        <Tile
          label="Shift hours per day"
          value={s.schedule ? formatMinutes(s.schedule.required_minutes) : "—"}
          hint={s.schedule ? `Working days: ${s.schedule.working_days.map((d) => ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"][d - 1]).join(", ")}` : undefined}
        />
      </dl>
    </Card>
  );
}
