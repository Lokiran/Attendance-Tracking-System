import { clsx } from "clsx";
import { ChevronLeft, ChevronRight } from "lucide-react";

import { Card, CardHeader } from "@/components/ui/Card";
import { Skeleton } from "@/components/ui/Skeleton";
import { ErrorState } from "@/components/ui/States";
import { STATUS_META } from "@/components/ui/StatusBadge";
import { addDays, addMonths, endOfMonth, isoWeekday, minDate, monthLabel, startOfMonth, todayInOffice } from "@/lib/dates";
import { formatDate, formatMinutes, STATUS_LABEL } from "@/lib/format";
import { useEmployeeDays } from "@/lib/queries";
import type { DayRecord, DayStatus } from "@/lib/types";

const WEEKDAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
const LEGEND: DayStatus[] = ["PRESENT", "WFH", "ABSENT", "LEAVE", "HOLIDAY", "NO_TIMESHEET"];

export function AttendanceCalendar({ employeeId, month, onMonthChange, onSelectDay }: {
  employeeId: number;
  month: string; // any date in the month
  onMonthChange: (month: string) => void;
  onSelectDay: (date: string) => void;
}) {
  const today = todayInOffice();
  const first = startOfMonth(month);
  const last = endOfMonth(month);
  const end = minDate(last, today);
  const future = first > today;
  const days = useEmployeeDays(employeeId, { start: first, end: future ? first : end, page_size: 31, order: "asc" });
  const byDate = new Map<string, DayRecord>((future ? [] : days.data?.items ?? []).map((d) => [d.date, d]));

  const lead = isoWeekday(first) - 1;
  const cells: (string | null)[] = Array.from({ length: lead }, () => null);
  for (let d = first; d <= last; d = addDays(d, 1)) cells.push(d);

  return (
    <Card>
      <CardHeader
        title="Attendance calendar"
        subtitle="From the imported timesheets · select a day for details"
        actions={
          <div className="flex items-center gap-1">
            <button className="rounded-md p-1.5 text-ink-2 hover:bg-surface-2" onClick={() => onMonthChange(addMonths(first, -1))} aria-label="Previous month">
              <ChevronLeft className="size-4" />
            </button>
            <span className="min-w-32 text-center text-sm font-medium text-ink" aria-live="polite">
              {monthLabel(first)}
            </span>
            <button
              className="rounded-md p-1.5 text-ink-2 hover:bg-surface-2 disabled:opacity-40"
              onClick={() => onMonthChange(addMonths(first, 1))}
              disabled={addMonths(first, 1) > today}
              aria-label="Next month"
            >
              <ChevronRight className="size-4" />
            </button>
          </div>
        }
      />
      {days.isError ? (
        <ErrorState error={days.error} onRetry={() => days.refetch()} />
      ) : (
        <div className="p-3 sm:p-4">
          <div role="grid" aria-label={"Attendance for " + monthLabel(first)} className="grid grid-cols-7 gap-1">
            {WEEKDAYS.map((w) => (
              <div key={w} role="columnheader" className="pb-1 text-center text-[11px] font-medium text-ink-3">
                {w}
              </div>
            ))}
            {cells.map((d, i) => {
              if (!d) return <div key={"e" + i} />;
              const rec = byDate.get(d);
              const meta = rec ? STATUS_META[rec.status] : null;
              const Icon = meta?.icon;
              const isFuture = d > today;
              const label = formatDate(d) + (rec ? ": " + STATUS_LABEL[rec.status] + (rec.hours_known ? ", " + formatMinutes(rec.net_minutes) : "") : isFuture ? ": upcoming" : "");
              return (
                <button
                  key={d}
                  role="gridcell"
                  type="button"
                  disabled={isFuture || !rec}
                  onClick={() => onSelectDay(d)}
                  aria-label={label}
                  className={clsx(
                    "relative flex aspect-square min-h-11 flex-col items-start justify-between rounded-lg border p-1.5 text-left transition-colors sm:aspect-[4/3]",
                    d === today ? "border-brand" : "border-line",
                    rec && !isFuture ? "hover:bg-surface-2" : "cursor-default opacity-60",
                  )}
                >
                  <span className="tabular text-xs font-medium text-ink">{Number(d.slice(8))}</span>
                  {days.isLoading && !isFuture ? (
                    <Skeleton className="h-3 w-full" />
                  ) : (
                    rec && (
                      <span className="flex w-full items-center gap-1">
                        {Icon && <Icon className="size-3.5 shrink-0" style={{ color: meta!.color }} aria-hidden />}
                        <span className="tabular hidden truncate text-[10px] text-ink-2 md:inline">
                          {rec.hours_known ? formatMinutes(rec.net_minutes) : STATUS_LABEL[rec.status]}
                        </span>
                      </span>
                    )
                  )}
                  {rec && rec.exceptions.length > 0 && <span aria-hidden className="absolute right-1.5 top-1.5 size-1.5 rounded-full" style={{ background: "var(--serious)" }} />}
                </button>
              );
            })}
          </div>
          <ul className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-ink-2">
            {LEGEND.map((s) => {
              const Icon = STATUS_META[s].icon;
              return (
                <li key={s} className="inline-flex items-center gap-1">
                  <Icon className="size-3" style={{ color: STATUS_META[s].color }} aria-hidden /> {STATUS_LABEL[s]}
                </li>
              );
            })}
            <li className="inline-flex items-center gap-1">
              <span aria-hidden className="size-1.5 rounded-full" style={{ background: "var(--serious)" }} /> Has exceptions
            </li>
          </ul>
        </div>
      )}
    </Card>
  );
}
