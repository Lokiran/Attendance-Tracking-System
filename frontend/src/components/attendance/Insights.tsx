import { clsx } from "clsx";
import { AlarmClock, CheckCircle2, ChevronRight, Clock3, XCircle } from "lucide-react";
import type { ReactNode } from "react";

import { DailyHoursChart, InOutChart, OfficeDaysByMonthChart } from "@/components/charts/Charts";
import { ChartCard, Legend } from "@/components/charts/ChartCard";
import { Card, CardHeader } from "@/components/ui/Card";
import { Skeleton } from "@/components/ui/Skeleton";
import { EmptyState } from "@/components/ui/States";
import { STATUS_META } from "@/components/ui/StatusBadge";
import { WfoDaysCell } from "@/components/wfo/WfoParts";
import { addDays } from "@/lib/dates";
import { clockTime, formatDate, formatMinutes, formatPercent, minutesOfDay, STATUS_LABEL } from "@/lib/format";
import type { DayRecord, MonthSummary, TrendBucket, WfoBand, WfoWeek } from "@/lib/types";

const BAND_TEXT: Record<WfoBand, string> = { MET: "On target", NEAR: "Close", BELOW: "Below target" };
// Monday to Friday: a weekend day only matters when it was worked, and then it shows in the hours.
const WEEKDAY = ["M", "T", "W", "T", "F"];

/** A small segmented control (tabs) for switching the view inside a card. */
export function Segmented<T extends string>({ value, onChange, options, label }: {
  value: T;
  onChange: (v: T) => void;
  options: { value: T; label: string }[];
  label: string;
}) {
  return (
    <div role="tablist" aria-label={label} className="inline-flex rounded-lg border border-line bg-surface-2 p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="tab"
          aria-selected={value === o.value}
          onClick={() => onChange(o.value)}
          className={clsx(
            "rounded-md px-2.5 py-1 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand",
            value === o.value ? "bg-surface text-ink shadow-sm" : "text-ink-2 hover:text-ink",
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

export type DailyView = "hours" | "inout";

/** Beside the calendar: office hours per day, or arrival and leaving times against the shift. */
export function DailyCard({ days, loading, error, onRetry, view, onView, onSelectDay, shift, graceMinutes, averageMinutes, start, end, highlight }: {
  days: DayRecord[];
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  view: DailyView;
  onView: (v: DailyView) => void;
  onSelectDay: (d: string) => void;
  shift?: { start: number; end: number } | null;
  graceMinutes?: number;
  averageMinutes?: number | null;
  start: string;
  end: string;
  highlight?: boolean;
}) {
  const withTimes = days.filter((d) => d.first_in);
  const ins = withTimes.map((d) => minutesOfDay(d.first_in)!).sort((a, b) => a - b);
  const median = ins.length ? ins[Math.floor(ins.length / 2)] : null;
  const hoursRows = days.filter((d) => d.scheduled_workday || d.net_minutes > 0);
  return (
    <ChartCard
      id="dash-daily"
      className={clsx(highlight && "ring-2 ring-brand")}
      title={view === "hours" ? "Office hours per day" : "Arrival and leaving"}
      subtitle={view === "hours"
        ? <>{formatDate(start)} – {formatDate(end)}{averageMinutes ? <> · average {formatMinutes(averageMinutes)} a day</> : null} · click a bar for the day</>
        : <>{withTimes.length} days with times{median !== null ? <> · usually in by {clockTime(median)}</> : null} · click a bar for the day</>}
      actions={<Segmented label="Daily view" value={view} onChange={onView} options={[{ value: "hours", label: "Hours" }, { value: "inout", label: "In & out" }]} />}
      loading={loading}
      error={error}
      onRetry={onRetry}
      isEmpty={view === "hours" ? !days.some((d) => d.net_minutes > 0) : withTimes.length === 0}
      emptyText={view === "hours" ? "The timesheets for this period have no hours (only the days people were in)." : "The timesheets for this period have no In / Out times."}
      legend={view === "hours"
        ? <Legend items={[{ label: "Office hours", color: "var(--series-1)" }, { label: "Shift", color: "var(--text-3)", dashed: true }]} />
        : <Legend items={[{ label: "In to out", color: "var(--series-1)" }, { label: "Shift window", color: "var(--surface-3)" }, ...(shift ? [{ label: "Late after", color: "var(--serious)", dashed: true }] : [])]} />}
      table={view === "hours"
        ? { columns: ["Date", "Status", "Office hours", "Shift"], rows: hoursRows.map((d) => [formatDate(d.date, { weekday: "short" }), STATUS_LABEL[d.status], d.hours_known ? formatMinutes(d.net_minutes) : "Not recorded", formatMinutes(d.required_minutes)]) }
        : { columns: ["Date", "In", "Out", "Office hours"], rows: withTimes.map((d) => [formatDate(d.date, { weekday: "short" }), clockTime(minutesOfDay(d.first_in)), clockTime(minutesOfDay(d.last_out)), d.hours_known ? formatMinutes(d.net_minutes) : "—"]) }}
      height={290}
    >
      {view === "hours"
        ? <DailyHoursChart days={days} onSelectDay={onSelectDay} averageMinutes={averageMinutes} />
        : <InOutChart days={days} shift={shift} graceMinutes={graceMinutes} onSelectDay={onSelectDay} />}
    </ChartCard>
  );
}

function WeekStatus({ w }: { w: WfoWeek }) {
  const common = "inline-flex items-center gap-1 whitespace-nowrap text-xs font-medium";
  if (w.status === "MET") return <span className={clsx(common, "text-ink")}><CheckCircle2 className="size-3.5" style={{ color: "var(--good)" }} aria-hidden />Met</span>;
  if (w.status === "SHORT") return <span className={clsx(common, "text-ink")}><XCircle className="size-3.5" style={{ color: "var(--critical)" }} aria-hidden />{w.shortfall} short</span>;
  return <span className={clsx(common, "text-ink-3")}><Clock3 className="size-3.5" aria-hidden />{w.status === "IN_PROGRESS" ? "In progress" : "Upcoming"}</span>;
}

/** Five small day cells (Mon-Fri): the week at a glance in the calendar's status colours. */
function WeekStrip({ weekStart, byDate, start, end, onSelectDay }: {
  weekStart: string;
  byDate: Map<string, DayRecord>;
  start: string;
  end: string;
  onSelectDay: (d: string) => void;
}) {
  return (
    <div className="flex gap-1" role="group" aria-label={"Days of the week of " + formatDate(weekStart)}>
      {WEEKDAY.map((letter, i) => {
        const d = addDays(weekStart, i);
        const rec = byDate.get(d);
        const inRange = d >= start && d <= end;
        const off = !rec || rec.status === "WEEKEND" || rec.status === "NOT_SCHEDULED";
        const meta = rec ? STATUS_META[rec.status] : null;
        const label = formatDate(d, { weekday: "short" }) + (rec ? ": " + STATUS_LABEL[rec.status] + (rec.hours_known ? ", " + formatMinutes(rec.net_minutes) : "") : "");
        return (
          <button
            key={d}
            type="button"
            disabled={!rec || !inRange}
            onClick={() => onSelectDay(d)}
            title={label}
            aria-label={label}
            className={clsx(
              "flex size-7 flex-col items-center justify-center rounded-md border text-[10px] font-medium leading-none transition-transform",
              rec && inRange ? "hover:scale-110 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand" : "cursor-default",
              !inRange && "opacity-35",
              off ? "border-line bg-surface-2 text-ink-3" : "border-transparent text-ink",
            )}
            style={!off && meta ? { background: `color-mix(in srgb, ${meta.color} 22%, transparent)`, borderColor: `color-mix(in srgb, ${meta.color} 45%, transparent)` } : undefined}
          >
            <span aria-hidden>{letter}</span>
            {!off && meta && <meta.icon className="mt-0.5 size-2.5" style={{ color: meta.color }} aria-hidden />}
          </button>
        );
      })}
    </div>
  );
}

/** Week by week: the days, office days against the weekly requirement, and hours against the shift. */
export function WeekByWeek({ weeks, buckets, days, start, end, onSelectDay, highlight, loading }: {
  weeks: WfoWeek[];
  buckets: TrendBucket[];
  days: DayRecord[];
  start: string;
  end: string;
  onSelectDay: (d: string) => void;
  highlight?: boolean;
  loading?: boolean;
}) {
  const byDate = new Map(days.map((d) => [d.date, d]));
  const bucketFor = (w: WfoWeek) => buckets.find((b) => b.start >= w.week_start && b.start <= w.week_end);
  const anyHours = buckets.some((b) => b.attended > 0);
  return (
    <Card id="dash-weeks" className={clsx("flex min-w-0 scroll-mt-24 flex-col", highlight && "ring-2 ring-brand")}>
      <CardHeader
        title="Week by week"
        subtitle="Each week's days, office days against the weekly requirement, and office hours · click a day"
        actions={<Legend items={[{ label: "Present", color: "var(--st-present)" }, { label: "Absent", color: "var(--st-absent)" }, { label: "Holiday", color: "var(--st-holiday)" }]} />}
      />
      {loading ? (
        <div className="space-y-3 p-5">{[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-10" />)}</div>
      ) : weeks.length === 0 ? (
        <EmptyState title="No weeks in this period" description="Pick a longer date range." />
      ) : (
        <div role="table" aria-label="Weeks in the period" className="divide-y divide-line">
          <div role="row" className="hidden grid-cols-[7rem_9.75rem_2.75rem_minmax(0,1fr)] items-center gap-3 px-5 py-2 text-[11px] font-medium uppercase tracking-wide text-ink-3 md:grid">
            <span role="columnheader">Week</span>
            <span role="columnheader">Days</span>
            <span role="columnheader">Office</span>
            <span role="columnheader">{anyHours ? "Office hours" : "Hours"}</span>
          </div>
          {weeks.map((w) => {
            const b = bucketFor(w);
            const pct = b && b.hours_required_minutes > 0 ? Math.min(100, (b.net_minutes / b.hours_required_minutes) * 100) : 0;
            return (
              <div key={w.week_start} role="row"
                className="grid grid-cols-2 items-center gap-x-3 gap-y-2 px-5 py-3 md:grid-cols-[7rem_9.75rem_2.75rem_minmax(0,1fr)]">
                <div role="cell" className="min-w-0">
                  <div className="whitespace-nowrap text-[13px] font-medium text-ink">{w.label}</div>
                  <WeekStatus w={w} />
                </div>
                <div role="cell" className="justify-self-end md:justify-self-start">
                  <WeekStrip weekStart={w.week_start} byDate={byDate} start={start} end={end} onSelectDay={onSelectDay} />
                </div>
                <div role="cell" className="text-sm">
                  <span className="tabular font-semibold text-ink">{w.wfo_days}</span>
                  <span className="tabular text-ink-3">/{w.required}</span>
                  <span className="sr-only"> office days required</span>
                </div>
                <div role="cell" className="min-w-0">
                  {b && b.attended > 0 ? (
                    <>
                      <div className="flex items-baseline justify-between gap-2 text-xs">
                        <span className="tabular font-medium text-ink">{formatMinutes(b.net_minutes)}</span>
                        <span className="tabular text-ink-3">of {formatMinutes(b.hours_required_minutes)}</span>
                      </div>
                      <div className="mt-1 h-1.5 overflow-hidden rounded-full bg-surface-3" aria-hidden>
                        <div className="h-full rounded-full" style={{ width: `${pct}%`, background: "var(--series-1)" }} />
                      </div>
                      {b.late > 0 && (
                        <div className="mt-1 flex items-center gap-1 text-[11px] text-ink-3">
                          <AlarmClock className="size-3" style={{ color: "var(--serious)" }} aria-hidden />{b.late} late
                        </div>
                      )}
                    </>
                  ) : (
                    <span className="text-xs text-ink-3">{b && b.present > 0 ? "Not recorded" : "—"}</span>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </Card>
  );
}

/** Month by month: office days against the requirement (chart), then each month's figures; click to open it. */
export function MonthByMonth({ months, loading, error, onRetry, onSelect, current }: {
  months: MonthSummary[];
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  onSelect: (m: MonthSummary) => void;
  current?: { start: string; end: string };
}) {
  const required = Math.max(0, ...months.filter((m) => m.covered && !m.partial).map((m) => m.required_days)) || 12;
  const rows: ReactNode = (
    <ul className="mt-2 divide-y divide-line border-t border-line">
      {[...months].reverse().filter((m) => m.covered).map((m) => {
        const selected = current && current.start === m.start && current.end === m.end;
        return (
          <li key={m.label}>
            <button
              type="button"
              onClick={() => onSelect(m)}
              aria-current={selected ? "true" : undefined}
              className={clsx("grid w-full grid-cols-[4.5rem_1fr_auto_auto_1rem] items-center gap-3 px-3 py-2 text-left text-sm transition-colors hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand",
                selected && "bg-brand-soft")}
            >
              <span className="font-medium text-ink">{m.label}</span>
              <span><WfoDaysCell wfo={m.office_days} required={m.required_days} band={m.wfo_band} /></span>
              <span className="tabular text-xs text-ink-2" title="Attendance">{formatPercent(m.attendance_percentage)}</span>
              <span className="tabular w-16 text-right text-xs text-ink-2" title="Average office hours">{m.average_net_minutes === null ? "—" : formatMinutes(m.average_net_minutes)}</span>
              <ChevronRight className="size-4 text-ink-3" aria-hidden />
            </button>
          </li>
        );
      })}
    </ul>
  );
  return (
    <ChartCard
      title="Month by month"
      subtitle={`Office days against the ${required} required · click a month to open it`}
      loading={loading}
      error={error}
      onRetry={onRetry}
      isEmpty={months.length === 0}
      emptyText="No timesheet has been imported for this person yet."
      legend={<Legend items={[{ label: "On target", color: "var(--good)" }, { label: "Close", color: "var(--warning)" }, { label: "Below target", color: "var(--critical)" }, { label: "Required", color: "var(--text-2)", dashed: true }]} />}
      table={{
        columns: ["Month", "Office days", "Required", "Status", "Attendance", "Average office hours"],
        rows: months.map((m) => [m.label, m.covered ? m.office_days : "—", m.covered ? m.required_days : "—", m.covered ? BAND_TEXT[m.wfo_band] : "No timesheet",
          formatPercent(m.attendance_percentage), m.average_net_minutes === null ? "Not recorded" : formatMinutes(m.average_net_minutes)]),
      }}
      height={200}
      footer={rows}
    >
      <OfficeDaysByMonthChart months={months} onSelect={onSelect} />
    </ChartCard>
  );
}
