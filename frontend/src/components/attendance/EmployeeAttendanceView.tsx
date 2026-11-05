import { CalendarCheck2, CalendarX2, Clock3, Gauge, Palmtree, Timer } from "lucide-react";
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
import { CorrectionFormModal } from "./RequestForms";

function dayCount(n: number) {
  return n === 1 ? "1 day" : `${n} days`;
}

/** "2h 15m short of 24h 00m target" — plain language instead of a signed variance. */
function targetHint(s: Summary) {
  if (!s.required_minutes) return "no target in this period";
  const v = s.variance_minutes;
  if (v === 0) return `right on the ${formatMinutes(s.required_minutes)} target`;
  return `${formatMinutes(Math.abs(v))} ${v < 0 ? "short of" : "over"} ${formatMinutes(s.required_minutes)} target`;
}

/** Summary cards, calendar, charts and history for one employee — used for "me" and for drill-down. */
export function EmployeeAttendanceView({ employeeId, isSelf, header }: { employeeId: number; isSelf: boolean; header?: React.ReactNode }) {
  const { start, end, valid, setRange, get, set } = useRangeParams();
  const today = todayInOffice();
  const [month, setMonth] = useState(end);
  const selectedDay = get("day") || null;
  const [correction, setCorrection] = useState<{ date: string; sessionId: number | null } | null>(null);

  const summary = useEmployeeSummary(valid ? employeeId : undefined, start, end);
  const days = useEmployeeDays(valid ? employeeId : undefined, { start, end, page_size: 200, order: "asc" });
  const weekly = useEmployeeTrends(valid ? employeeId : undefined, start, end, "week");
  const sixMonthStart = addMonths(today, -5);
  const monthly = useEmployeeTrends(employeeId, sixMonthStart, today, "month");
  const s = summary.data;

  const distribution: { status: DayStatus; count: number }[] = s
    ? [
        { status: "PRESENT", count: s.present_days },
        { status: "ABSENT", count: s.absent_days },
        { status: "ON_LEAVE", count: s.leave_days },
        { status: "INCOMPLETE", count: s.incomplete_days },
        { status: "HOLIDAY", count: s.holiday_days },
        { status: "WEEKEND", count: s.weekend_days },
      ]
    : [];

  const openCorrection = isSelf ? (date: string, sessionId: number | null) => setCorrection({ date, sessionId }) : undefined;

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
            hint={s && (s.incomplete_days ? `plus ${dayCount(s.incomplete_days)} missing a check-out` : "without approved leave")} />
          <SummaryCard label="Leave taken" icon={<Palmtree className="size-4" />} loading={!s} value={s?.leave_days} hint="approved leave days" />
          <SummaryCard label="Hours worked" icon={<Clock3 className="size-4" />} loading={!s} value={formatMinutes(s?.net_minutes)} hint={s && targetHint(s)} />
          <SummaryCard label="Avg per day" icon={<Timer className="size-4" />} loading={!s} value={formatMinutes(s?.average_net_minutes)}
            hint={s && (s.attended_days_for_average ? `across ${dayCount(s.attended_days_for_average)} worked` : "no completed days yet")} />
          <SummaryCard label="Attendance rate" icon={<Gauge className="size-4" />} loading={!s} value={formatPercent(s?.attendance_percentage)}
            hint={s && (s.expected_days ? `${s.present_expected_days} of ${dayCount(s.expected_days)} scheduled` : "no scheduled days yet")} />
        </CardGrid>
      )}

      <div className="grid gap-5 xl:grid-cols-2">
        <AttendanceCalendar employeeId={employeeId} month={month} onMonthChange={setMonth} onSelectDay={(d) => set({ day: d }, false)} />
        <ChartCard
          title="Hours worked per day"
          subtitle={formatDate(start) + " – " + formatDate(end) + " · click a bar for details"}
          loading={days.isLoading}
          error={days.error}
          onRetry={() => days.refetch()}
          isEmpty={!days.data?.items.some((d) => d.net_minutes > 0)}
          table={{ columns: ["Date", "Status", "Net", "Required"], rows: (days.data?.items ?? []).map((d) => [d.date, STATUS_LABEL[d.status], formatMinutes(d.net_minutes), formatMinutes(d.required_minutes)]) }}
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
          subtitle="Per week in the period"
          loading={weekly.isLoading}
          error={weekly.error}
          isEmpty={!weekly.data?.items.length}
          legend={<Legend items={[{ label: "Actual (net)", color: "var(--series-1)" }, { label: "Required", color: "var(--series-2)" }]} />}
          table={{ columns: ["Week", "Actual", "Required"], rows: (weekly.data?.items ?? []).map((b) => [b.label, (hours(b.net_minutes) ?? 0) + " h", (hours(b.required_minutes) ?? 0) + " h"]) }}
        >
          <ActualVsRequiredChart items={weekly.data?.items ?? []} />
        </ChartCard>
      </div>

      <AttendanceHistoryTable employeeId={employeeId} start={start} end={end} onSelectDay={(d) => set({ day: d }, false)} />

      <DayDetailModal
        employeeId={employeeId}
        date={selectedDay}
        onClose={() => set({ day: null }, false)}
        onRequestCorrection={
          openCorrection &&
          ((d, sid) => {
            set({ day: null }, false);
            openCorrection(d, sid);
          })
        }
      />
      {isSelf && (
        <CorrectionFormModal open={!!correction} onClose={() => setCorrection(null)} date={correction?.date ?? null} sessionId={correction?.sessionId ?? null} />
      )}
    </div>
  );
}
