import { CalendarCheck2, CalendarX2, Clock3, Gauge, Home, Timer } from "lucide-react";
import { useState } from "react";

import { ActualVsRequiredChart, AttendanceTrendChart, DailyHoursChart, StatusDistributionChart } from "@/components/charts/Charts";
import { ChartCard, Legend } from "@/components/charts/ChartCard";
import { CardGrid, SummaryCard } from "@/components/ui/SummaryCard";
import { DateRangeFilter } from "@/components/ui/DateRangeFilter";
import { ErrorState } from "@/components/ui/States";
import { ExportButton } from "@/components/ui/Misc";
import { useRangeParams } from "@/hooks/useUrlState";
import { addMonths, todayInOffice } from "@/lib/dates";
import { formatDate, formatMinutes, formatPercent, hours, STATUS_LABEL } from "@/lib/format";
import { useEmployeeDays, useEmployeeSummary, useEmployeeTrends } from "@/lib/queries";
import type { DayStatus, Summary } from "@/lib/types";

import { AttendanceCalendar } from "./AttendanceCalendar";
import { AttendanceHistoryTable } from "./AttendanceHistoryTable";
import { DayDetailModal } from "./DayDetailModal";

function dayCount(n: number) {
  return n === 1 ? "1 day" : `${n} days`;
}

/** "2h 15m short of the shift hours" — over the days whose hours the timesheet recorded only. */
function shiftHint(s: Summary) {
  if (!s.hours_days) return s.present_days ? "the timesheet has no hours for these days" : "no office days in this period";
  const v = s.variance_minutes;
  const days = `on ${dayCount(s.hours_days)} with hours`;
  if (v === 0) return `exactly the shift hours ${days}`;
  return `${formatMinutes(Math.abs(v))} ${v < 0 ? "short of" : "over"} the shift hours ${days}`;
}

/** Summary cards, calendar, charts and history for one employee — used for "me" and for drill-down. Everything
 *  comes from the timesheets the Admin imports. */
export function EmployeeAttendanceView({ employeeId, header }: { employeeId: number; header?: React.ReactNode }) {
  const { start, end, valid, setRange, get, set } = useRangeParams();
  const today = todayInOffice();
  const [month, setMonth] = useState(end);
  const selectedDay = get("day") || null;

  const summary = useEmployeeSummary(valid ? employeeId : undefined, start, end);
  const days = useEmployeeDays(valid ? employeeId : undefined, { start, end, page_size: 200, order: "asc" });
  const weekly = useEmployeeTrends(valid ? employeeId : undefined, start, end, "week");
  const sixMonthStart = addMonths(today, -5);
  const monthly = useEmployeeTrends(employeeId, sixMonthStart, today, "month");
  const s = summary.data;

  const distribution: { status: DayStatus; count: number }[] = s
    ? [
        { status: "PRESENT", count: s.present_days },
        { status: "WFH", count: s.wfh_days },
        { status: "ABSENT", count: s.absent_days },
        { status: "LEAVE", count: s.leave_days },
        { status: "HOLIDAY", count: s.holiday_days },
        { status: "NO_TIMESHEET", count: s.no_timesheet_days },
      ]
    : [];

  return (
    <div className="space-y-5">
      {header}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <DateRangeFilter start={start} end={end} onChange={setRange} />
        <ExportButton path="/reports/employee-attendance.csv" params={{ employee_id: employeeId, start, end }} />
      </div>

      {summary.isError ? (
        <ErrorState error={summary.error} onRetry={() => summary.refetch()} />
      ) : (
        <CardGrid cols={6}>
          <SummaryCard label="Days present" icon={<CalendarCheck2 className="size-4" />} loading={!s} value={s?.present_days}
            hint={s && (s.extra_present_days ? `incl. ${dayCount(s.extra_present_days)} outside schedule` : `of ${dayCount(s.expected_days)} scheduled`)} />
          <SummaryCard label="Days absent" icon={<CalendarX2 className="size-4" />} loading={!s} value={s?.absent_days}
            hint={s && (s.no_timesheet_days ? `${dayCount(s.no_timesheet_days)} not in a timesheet yet` : "scheduled days not in the timesheet")} />
          <SummaryCard label="WFH days" icon={<Home className="size-4" />} loading={!s} value={s?.wfh_days}
            hint={s && (s.leave_days ? `plus ${dayCount(s.leave_days)} on leave` : "working days not in the office")} />
          <SummaryCard label="Office hours" icon={<Clock3 className="size-4" />} loading={!s}
            value={s && (s.hours_days ? formatMinutes(s.net_minutes) : s.present_days ? "Not recorded" : "—")} hint={s && shiftHint(s)} />
          <SummaryCard label="Avg per day" icon={<Timer className="size-4" />} loading={!s} value={formatMinutes(s?.average_net_minutes)}
            hint={s && (s.hours_days ? `across ${dayCount(s.hours_days)} with hours` : "only for days the timesheet gives hours")} />
          <SummaryCard label="Attendance rate" icon={<Gauge className="size-4" />} loading={!s} value={formatPercent(s?.attendance_percentage)}
            hint={s && (s.expected_days ? `${s.present_expected_days} of ${dayCount(s.expected_days)} scheduled` : "no scheduled days yet")} />
        </CardGrid>
      )}

      <div className="grid gap-5 xl:grid-cols-2">
        <AttendanceCalendar employeeId={employeeId} month={month} onMonthChange={setMonth} onSelectDay={(d) => set({ day: d }, false)} />
        <ChartCard
          title="Office hours per day"
          subtitle={formatDate(start) + " – " + formatDate(end) + " · from the timesheet · click a bar for details"}
          loading={days.isLoading}
          error={days.error}
          onRetry={() => days.refetch()}
          isEmpty={!days.data?.items.some((d) => d.net_minutes > 0)}
          emptyText="The timesheets for this period have no hours (only the days people were in)."
          table={{ columns: ["Date", "Status", "Office hours", "Required"], rows: (days.data?.items ?? []).map((d) => [d.date, STATUS_LABEL[d.status], d.hours_known ? formatMinutes(d.net_minutes) : "Not recorded", formatMinutes(d.required_minutes)]) }}
          height={300}
        >
          <DailyHoursChart days={days.data?.items ?? []} onSelectDay={(d) => set({ day: d }, false)} />
        </ChartCard>
      </div>

      <div className="grid gap-5 lg:grid-cols-3">
        <ChartCard
          title="Monthly attendance trend"
          subtitle="Attendance % over the last 6 months"
          loading={monthly.isLoading}
          error={monthly.error}
          isEmpty={!monthly.data?.items.some((b) => b.expected > 0)}
          table={{ columns: ["Month", "Attendance", "Present/expected"], rows: (monthly.data?.items ?? []).map((b) => [b.label, formatPercent(b.attendance_percentage), `${b.present_expected}/${b.expected}`]) }}
        >
          <AttendanceTrendChart items={monthly.data?.items ?? []} />
        </ChartCard>
        <ChartCard
          title="Attendance distribution"
          subtitle="Days by status in the period · click to filter history"
          loading={!s}
          isEmpty={distribution.every((d) => d.count === 0)}
          table={{ columns: ["Status", "Days"], rows: distribution.map((d) => [STATUS_LABEL[d.status], d.count]) }}
        >
          <StatusDistributionChart data={distribution} />
        </ChartCard>
        <ChartCard
          title="Actual vs required hours"
          subtitle="Per week, on days the timesheet gives hours"
          loading={weekly.isLoading}
          error={weekly.error}
          isEmpty={!weekly.data?.items.some((b) => b.attended > 0)}
          emptyText="No hours in the timesheets for this period."
          legend={<Legend items={[{ label: "Actual (office)", color: "var(--series-1)" }, { label: "Required", color: "var(--series-2)" }]} />}
          table={{ columns: ["Week", "Actual", "Required"], rows: (weekly.data?.items ?? []).map((b) => [b.label, (hours(b.net_minutes) ?? 0) + " h", (hours(b.hours_required_minutes) ?? 0) + " h"]) }}
        >
          <ActualVsRequiredChart items={weekly.data?.items ?? []} />
        </ChartCard>
      </div>

      <AttendanceHistoryTable employeeId={employeeId} start={start} end={end} onSelectDay={(d) => set({ day: d }, false)} />

      <DayDetailModal employeeId={employeeId} date={selectedDay} onClose={() => set({ day: null }, false)} />
    </div>
  );
}
