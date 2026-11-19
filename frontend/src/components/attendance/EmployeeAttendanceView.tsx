import { FileSpreadsheet, FileText, Table2 } from "lucide-react";
import { useEffect, useRef, useState } from "react";

import { DateRangeFilter } from "@/components/ui/DateRangeFilter";
import { ErrorState } from "@/components/ui/States";
import { ExportMenu } from "@/components/ui/Misc";
import { useRangeParams } from "@/hooks/useUrlState";
import { addMonths, defaultMonthDate, endOfMonth, minDate, todayInOffice } from "@/lib/dates";
import { useDayDetail, useEmployeeDays, useEmployeeMonths, useEmployeeSummary, useEmployeeTrends } from "@/lib/queries";
import type { DayStatus, MonthSummary } from "@/lib/types";

import { AttendanceCalendar } from "./AttendanceCalendar";
import { AttendanceHistoryTable } from "./AttendanceHistoryTable";
import { AttendanceOverview, type OverviewTarget } from "./AttendanceOverview";
import { DayDetailModal } from "./DayDetailModal";
import { DailyCard, MonthByMonth, WeekByWeek, type DailyView } from "./Insights";

/** Months from the first to the last one a timesheet covers (empty months at either end are left out). */
function coveredSpan(months: MonthSummary[]): MonthSummary[] {
  const first = months.findIndex((m) => m.covered);
  if (first < 0) return [];
  const last = months.length - 1 - [...months].reverse().findIndex((m) => m.covered);
  return months.slice(first, last + 1);
}

const toMin = (t: string) => Number(t.slice(0, 2)) * 60 + Number(t.slice(3, 5));
const TARGET_ID: Record<OverviewTarget, string> = { wfo: "dash-weeks", hours: "dash-daily", days: "dash-history" };

/** Summary cards, calendar, insights and history for one employee — used for "me" and for drill-down. Everything
 *  comes from the timesheets the Admin imports. The KPI panels open their detail; the day counts filter the history. */
export function EmployeeAttendanceView({ employeeId, header }: { employeeId: number; header?: React.ReactNode }) {
  const { start, end, valid, setRange, get, set } = useRangeParams();
  const today = todayInOffice();
  const [month, setMonth] = useState(end);
  const selectedDay = get("day") || null;
  const [focus, setFocus] = useState<OverviewTarget | null>(null);
  const [dailyView, setDailyView] = useState<DailyView>("hours");
  const [filter, setFilter] = useState<{ status: DayStatus | ""; exceptions: boolean }>({ status: "", exceptions: false });
  const focusTimer = useRef<number | undefined>(undefined);

  const summary = useEmployeeSummary(valid ? employeeId : undefined, start, end);
  const days = useEmployeeDays(valid ? employeeId : undefined, { start, end, page_size: 200, order: "asc" });
  const weekly = useEmployeeTrends(valid ? employeeId : undefined, start, end, "week");
  // The last twelve months up to the latest timesheet month: office days against the requirement.
  const latest = defaultMonthDate(today);
  const history = useEmployeeMonths(employeeId, addMonths(latest, -11), minDate(endOfMonth(latest), today));
  const months = coveredSpan(history.data ?? []);
  const s = summary.data;
  const items = days.data?.items ?? [];
  // Without any recorded hours in the period, the hours chart would be empty: week by week goes beside the calendar.
  const hasHours = items.some((d) => d.hours_known && d.net_minutes > 0);
  // The shift (and late-arrival grace) comes with any day's detail: take it from the first day with times.
  const sample = items.find((d) => d.first_in)?.date ?? null;
  const sampleDetail = useDayDetail(hasHours ? employeeId : undefined, sample);
  const shiftBrief = sampleDetail.data?.shift;
  const shift = shiftBrief ? { start: toMin(shiftBrief.start_time), end: toMin(shiftBrief.end_time) } : null;
  const average = s && s.hours_days > 0 ? s.average_net_minutes : null;

  useEffect(() => () => window.clearTimeout(focusTimer.current), []);

  const openDay = (d: string) => set({ day: d }, false);
  const open = (target: OverviewTarget) => {
    if (target === "hours") setDailyView("hours");
    setFocus(target);
    document.getElementById(TARGET_ID[target])?.scrollIntoView({ behavior: "smooth", block: "start" });
    window.clearTimeout(focusTimer.current);
    focusTimer.current = window.setTimeout(() => setFocus(null), 1800);
  };
  const applyFilter = (f: { status?: DayStatus | ""; exceptions?: boolean }) => {
    setFilter({ status: f.status ?? "", exceptions: !!f.exceptions });
    if (f.status || f.exceptions) open("days");
  };

  const weeks = (
    <WeekByWeek
      weeks={s?.wfo?.weekly_wfo_breakdown ?? []}
      buckets={weekly.data?.items ?? []}
      days={items}
      start={start}
      end={end}
      onSelectDay={openDay}
      highlight={focus === "wfo"}
      loading={!s || days.isLoading}
    />
  );

  return (
    <div className="space-y-5">
      {header}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <DateRangeFilter start={start} end={end} onChange={setRange} />
        <ExportMenu
          params={{ employee_id: employeeId, start, end }}
          options={[
            { label: "PDF report", hint: "Summary, monthly chart and every day", path: "/reports/employee-attendance.pdf", icon: <FileText className="size-4" /> },
            { label: "Excel workbook", hint: "Summary, by month and daily sheets", path: "/reports/employee-attendance.xlsx", icon: <FileSpreadsheet className="size-4" /> },
            { label: "CSV", hint: "Daily rows only", path: "/reports/employee-attendance.csv", icon: <Table2 className="size-4" /> },
          ]}
        />
      </div>

      {summary.isError ? (
        <ErrorState error={summary.error} onRetry={() => summary.refetch()} />
      ) : (
        <AttendanceOverview s={s} onOpen={open} active={focus} onFilter={applyFilter} filter={filter} />
      )}

      <div className="grid gap-5 xl:grid-cols-2">
        <AttendanceCalendar employeeId={employeeId} month={month} onMonthChange={setMonth} onSelectDay={openDay} />
        {hasHours || !s?.wfo ? (
          <DailyCard
            days={items}
            loading={days.isLoading}
            error={days.error}
            onRetry={() => days.refetch()}
            view={dailyView}
            onView={setDailyView}
            onSelectDay={openDay}
            shift={shift}
            graceMinutes={sampleDetail.data?.late_grace_minutes ?? 0}
            averageMinutes={average}
            start={start}
            end={end}
            highlight={focus === "hours"}
          />
        ) : (
          weeks
        )}
      </div>

      <div className={hasHours || !s?.wfo ? "grid gap-5 xl:grid-cols-2" : "grid gap-5"}>
        {(hasHours || !s?.wfo) && weeks}
        <MonthByMonth
          months={months}
          loading={history.isLoading}
          error={history.error}
          onRetry={() => history.refetch()}
          onSelect={(m) => { setRange({ start: m.start, end: m.end }); setMonth(m.end); }}
          current={{ start, end }}
        />
      </div>

      <AttendanceHistoryTable
        id="dash-history"
        className={focus === "days" ? "ring-2 ring-brand" : undefined}
        employeeId={employeeId}
        start={start}
        end={end}
        onSelectDay={openDay}
        filter={filter}
        onFilter={setFilter}
      />

      <DayDetailModal employeeId={employeeId} date={selectedDay} onClose={() => set({ day: null }, false)}
        onNavigate={openDay} averageMinutes={average} />
    </div>
  );
}
