import { FileSpreadsheet, FileText, Table2 } from "lucide-react";
import { useState } from "react";

import { ActualVsRequiredChart, DailyHoursChart, OfficeDaysByMonthChart, StatusDistributionChart } from "@/components/charts/Charts";
import { ChartCard, Legend } from "@/components/charts/ChartCard";
import { Card, CardHeader } from "@/components/ui/Card";
import { DateRangeFilter } from "@/components/ui/DateRangeFilter";
import { ErrorState } from "@/components/ui/States";
import { ExportMenu } from "@/components/ui/Misc";
import { WeeklyProgress } from "@/components/wfo/WfoParts";
import { useRangeParams } from "@/hooks/useUrlState";
import { addMonths, defaultMonthDate, endOfMonth, minDate, todayInOffice } from "@/lib/dates";
import { formatDate, formatMinutes, formatPercent, hours, STATUS_LABEL } from "@/lib/format";
import { useEmployeeDays, useEmployeeMonths, useEmployeeSummary, useEmployeeTrends } from "@/lib/queries";
import type { DayStatus, MonthSummary } from "@/lib/types";

const BAND_TEXT = { MET: "On target", NEAR: "Close", BELOW: "Below target" } as const;

/** Months from the first to the last one a timesheet covers (empty months at either end are left out). */
function coveredSpan(months: MonthSummary[]): MonthSummary[] {
  const first = months.findIndex((m) => m.covered);
  if (first < 0) return [];
  const last = months.length - 1 - [...months].reverse().findIndex((m) => m.covered);
  return months.slice(first, last + 1);
}

import { AttendanceCalendar } from "./AttendanceCalendar";
import { AttendanceHistoryTable } from "./AttendanceHistoryTable";
import { AttendanceOverview } from "./AttendanceOverview";
import { DayDetailModal } from "./DayDetailModal";

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
  // The last twelve months up to the latest timesheet month: office days against the requirement.
  const latest = defaultMonthDate(today);
  const history = useEmployeeMonths(employeeId, addMonths(latest, -11), minDate(endOfMonth(latest), today));
  const months = coveredSpan(history.data ?? []);
  const s = summary.data;
  // Without any recorded hours in the period, the hours chart would be empty: show weekly WFO progress instead.
  const hasHours = !!days.data?.items.some((d) => d.hours_known && d.net_minutes > 0);

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
        <AttendanceOverview s={s} />
      )}

      <div className="grid gap-5 xl:grid-cols-2">
        <AttendanceCalendar employeeId={employeeId} month={month} onMonthChange={setMonth} onSelectDay={(d) => set({ day: d }, false)} />
        {hasHours || !s?.wfo ? (
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
        ) : (
          <Card>
            <CardHeader title="WFO progress by week" subtitle="Office days each week against the weekly requirement · from the timesheet" />
            <WeeklyProgress weeks={s.wfo.weekly_wfo_breakdown} />
          </Card>
        )}
      </div>

      <div className={hasHours ? "grid gap-5 lg:grid-cols-3" : "grid gap-5 lg:grid-cols-2"}>
        <ChartCard
          title="Office days by month"
          subtitle={`Dashed line: ${Math.max(0, ...months.filter((m) => m.covered && !m.partial).map((m) => m.required_days)) || 12} days required · click a month to open it`}
          loading={history.isLoading}
          error={history.error}
          onRetry={() => history.refetch()}
          isEmpty={months.length === 0}
          emptyText="No timesheet has been imported for this person yet."
          legend={<Legend items={[{ label: "On target", color: "var(--good)" }, { label: "Close", color: "var(--warning)" }, { label: "Below target", color: "var(--critical)" }]} />}
          table={{
            columns: ["Month", "Office days", "Required", "Status", "Attendance", "Average office hours"],
            rows: months.map((m) => [m.label, m.covered ? m.office_days : "—", m.covered ? m.required_days : "—", m.covered ? BAND_TEXT[m.wfo_band] : "No timesheet",
              formatPercent(m.attendance_percentage), m.average_net_minutes === null ? "Not recorded" : formatMinutes(m.average_net_minutes)]),
          }}
        >
          <OfficeDaysByMonthChart months={months} onSelect={(m) => { setRange({ start: m.start, end: m.end }); setMonth(m.end); }} />
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
        {hasHours && (
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
        )}
      </div>

      <AttendanceHistoryTable employeeId={employeeId} start={start} end={end} onSelectDay={(d) => set({ day: d }, false)} />

      <DayDetailModal employeeId={employeeId} date={selectedDay} onClose={() => set({ day: null }, false)} />
    </div>
  );
}
