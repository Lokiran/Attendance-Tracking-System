import { clsx } from "clsx";
import { ArrowUpRight, Building2, CalendarCheck2, CalendarX2, Clock3, Gauge, Home, Plane, PartyPopper, AlarmClock } from "lucide-react";
import type { ReactNode } from "react";

import { Card } from "@/components/ui/Card";
import { Skeleton } from "@/components/ui/Skeleton";
import { bandMessage, bandOf, WFO_NEAR_DAYS, WfoBandPill, WfoTargetBar } from "@/components/wfo/WfoParts";
import { formatMinutes, formatPercent } from "@/lib/format";
import type { DayStatus, Summary } from "@/lib/types";

const days = (n: number) => (n === 1 ? "1 day" : `${n} days`);

/** What a click on a panel opens. */
export type OverviewTarget = "wfo" | "hours" | "days";

/**
 * One KPI panel. Every panel has the same rows at the same heights - title, big figure, bar, two lines of context,
 * and a footer of small figures pinned to the bottom - so the three panels line up across the row. The top part is
 * one button (it opens the matching detail below); footer figures can be buttons of their own (filters).
 */
function Panel({ title, icon, badge, value, unit, bar, caption, footer, onOpen, active, openLabel, cols = 4 }: {
  title: string;
  icon: ReactNode;
  badge?: ReactNode;
  value: ReactNode;
  unit?: ReactNode;
  bar?: ReactNode;
  caption?: ReactNode;
  footer: ReactNode;
  onOpen?: () => void;
  active?: boolean;
  openLabel: string;
  cols?: 3 | 4;
}) {
  const top = (
    <>
      <div className="flex h-7 items-center justify-between gap-3">
        <h2 className="flex items-center gap-2 text-sm font-medium text-ink-2">
          <span className="text-ink-3" aria-hidden>{icon}</span>
          {title}
        </h2>
        <span className="flex items-center gap-2">
          {badge}
          {onOpen && (
            <ArrowUpRight aria-hidden className="size-4 text-ink-3 opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100" />
          )}
        </span>
      </div>
      <div className="mt-3 flex h-11 items-baseline gap-x-2 overflow-hidden">
        <span className="tabular whitespace-nowrap text-4xl font-semibold tracking-tight text-ink">{value}</span>
        {unit && <span className="truncate text-sm text-ink-2">{unit}</span>}
      </div>
      <div className="mt-3 h-2.5">{bar ?? <div className="h-2.5 rounded-full bg-surface-3" aria-hidden />}</div>
      <p className="mt-3 line-clamp-2 min-h-10 text-sm leading-5 text-ink-2">{caption}</p>
    </>
  );
  return (
    <Card className={clsx("flex flex-col transition-shadow", active && "ring-2 ring-brand")}>
      {onOpen ? (
        <button
          type="button"
          onClick={onOpen}
          aria-label={openLabel}
          aria-expanded={active}
          className="group rounded-t-xl px-5 pb-4 pt-5 text-left transition-colors hover:bg-surface-2/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand"
        >
          {top}
        </button>
      ) : (
        <div className="px-5 pb-4 pt-5">{top}</div>
      )}
      <div className={clsx("mt-auto grid gap-1 border-t border-line px-3 py-2.5", cols === 3 ? "grid-cols-3" : "grid-cols-4")}>{footer}</div>
    </Card>
  );
}

function Bar({ value, max, label, color = "var(--brand)" }: { value: number; max: number; label: string; color?: string }) {
  const pct = max > 0 ? Math.min(100, (value / max) * 100) : 0;
  return (
    <div className="h-2.5 overflow-hidden rounded-full bg-surface-3" role="progressbar" aria-label={label}
      aria-valuemin={0} aria-valuemax={max} aria-valuenow={Math.min(value, max)}>
      <div className="h-full rounded-full transition-[width]" style={{ width: `${pct}%`, background: color }} />
    </div>
  );
}

/** A small figure in a panel footer; a button when it filters the history. */
function Stat({ label, value, icon, color, onClick, pressed, title, span = 1 }: {
  label: string;
  value: ReactNode;
  icon?: ReactNode;
  color?: string;
  onClick?: () => void;
  pressed?: boolean;
  title?: string;
  span?: 1 | 2;
}) {
  const body = (
    <>
      <span className="tabular block truncate text-base font-semibold text-ink">{value}</span>
      <span className="flex items-center gap-1 whitespace-nowrap text-xs text-ink-3">
        {icon && <span style={{ color }} aria-hidden>{icon}</span>}
        {label}
      </span>
    </>
  );
  const cls = clsx("min-w-0 rounded-md px-2 py-1 text-left", span === 2 && "col-span-2");
  if (!onClick) return <div className={cls} title={title}>{body}</div>;
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={pressed}
      title={title}
      className={clsx(cls, "transition-colors hover:bg-surface-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand",
        pressed && "bg-brand-soft ring-1 ring-brand")}
    >
      {body}
    </button>
  );
}

/**
 * The three panels at the top of an employee's dashboard: WFO target, office hours, attendance.
 * Clicking a panel opens its detail (`onOpen`); the day counts filter the history (`onFilter`).
 */
export function AttendanceOverview({ s, onOpen, active, onFilter, filter }: {
  s?: Summary;
  onOpen?: (target: OverviewTarget) => void;
  active?: OverviewTarget | null;
  onFilter?: (f: { status?: DayStatus | ""; exceptions?: boolean }) => void;
  filter?: { status: DayStatus | ""; exceptions: boolean };
}) {
  if (!s) {
    return (
      <div className="grid gap-4 lg:grid-cols-3">
        {[0, 1, 2].map((i) => <Card key={i} className="p-5"><Skeleton className="h-48" /></Card>)}
      </div>
    );
  }
  const w = s.wfo;
  const band = w ? bandOf(w.wfo_days, w.required_days, w.wfo_band) : null;
  const shiftPerDay = s.expected_days ? Math.round(s.required_minutes / s.expected_days) : null;
  const avg = s.hours_days > 0 ? s.average_net_minutes : null;
  const toggle = (status: DayStatus) => onFilter?.({ status: filter?.status === status ? "" : status, exceptions: false });
  const filterProps = (status: DayStatus) => onFilter ? { onClick: () => toggle(status), pressed: filter?.status === status } : {};

  return (
    <div className="grid gap-4 lg:grid-cols-3">
      {w && band ? (
        <Panel
          title="Work from office"
          icon={<Building2 className="size-4" />}
          badge={<span title={`On target: ${w.required_days}+ days · Close: up to ${WFO_NEAR_DAYS} short · Below target: more than ${WFO_NEAR_DAYS} short`}><WfoBandPill band={band} /></span>}
          value={w.wfo_days}
          unit={`of ${w.required_days} days required`}
          bar={<WfoTargetBar wfo={w.wfo_days} required={w.required_days} band={band} label={`WFO days: ${w.wfo_days} of ${w.required_days} required`} />}
          caption={<>
            {bandMessage(w.wfo_days, w.required_days)}
            {w.required_days < 12 && w.working_days > 0 && <span className="text-ink-3"> · prorated for this period</span>}
          </>}
          onOpen={onOpen && (() => onOpen("wfo"))}
          active={active === "wfo"}
          openLabel="Work from office: show week by week"
          cols={3}
          footer={<>
            <Stat label="Working days" value={w.working_days} title="Scheduled working days in the period (not holidays, leave or weekly offs)" />
            <Stat label="WFO share" value={formatPercent(w.wfo_percentage)} title="Office days out of working days" />
            <Stat label={w.shortfall_days > 0 ? "Short by" : "Required"} value={w.shortfall_days > 0 ? days(w.shortfall_days) : w.required_days} />
          </>}
        />
      ) : (
        <Panel title="Work from office" icon={<Building2 className="size-4" />} value="—" caption="No WFO figures for this period."
          openLabel="Work from office" footer={<Stat label="Office days" value={s.present_days} />} />
      )}

      <Panel
        title="Average office hours"
        icon={<Clock3 className="size-4" />}
        value={avg !== null ? formatMinutes(avg) : <span className="text-2xl font-medium text-ink-2">Not recorded</span>}
        unit={avg !== null ? (shiftPerDay ? `per day · shift ${formatMinutes(shiftPerDay)}` : "per day") : undefined}
        bar={avg !== null && shiftPerDay ? <Bar value={avg} max={shiftPerDay} label="Average office hours against the shift" /> : undefined}
        caption={avg !== null
          ? <>Recorded on {days(s.hours_days)}: {formatMinutes(s.net_minutes)} of {formatMinutes(s.hours_required_minutes)}</>
          : <span className="text-ink-3">Not in this timesheet. Hours show when it has In / Out times.</span>}
        onOpen={onOpen && avg !== null ? () => onOpen("hours") : undefined}
        active={active === "hours"}
        openLabel="Average office hours: show hours per day"
        cols={3}
        footer={<>
          <Stat label={shiftPerDay !== null ? `${days(s.expected_days)} × ${formatMinutes(shiftPerDay)}` : "Required this period"}
            value={formatMinutes(s.required_minutes)} title="Required this period" />
          <Stat label="Late days" value={s.late_days} icon={<AlarmClock className="size-3.5" />} color="var(--serious)"
            {...(onFilter ? { onClick: () => onFilter({ status: "", exceptions: !filter?.exceptions }), pressed: !!filter?.exceptions } : {})}
            title="Days with an exception (late arrival, short hours): click to list them" />
          <Stat label={s.variance_minutes < 0 ? "Below shift" : "Above shift"} value={avg !== null ? formatMinutes(Math.abs(s.variance_minutes)) : "—"}
            title="Recorded office time against the shift hours of the same days" />
        </>}
      />

      <Panel
        title="Attendance"
        icon={<Gauge className="size-4" />}
        value={formatPercent(s.attendance_percentage)}
        unit={`${s.present_days} of ${days(s.counted_days ?? s.expected_days)}`}
        bar={<Bar value={s.attendance_percentage ?? 0} max={100} label="Attendance rate" />}
        caption={s.no_timesheet_days
          ? <span className="text-ink-3">{days(s.no_timesheet_days)} not in a timesheet yet</span>
          : <span className="text-ink-3">Click a count below to list those days</span>}
        onOpen={onOpen && (() => onOpen("days"))}
        active={active === "days"}
        openLabel="Attendance: show the attendance history"
        footer={<>
          <Stat label="Office" value={s.present_days} color="var(--st-present)" icon={<CalendarCheck2 className="size-3.5" />} {...filterProps("PRESENT")} />
          <Stat label="Absent" value={s.absent_days} color="var(--st-absent)" icon={<CalendarX2 className="size-3.5" />} {...filterProps("ABSENT")} />
          <Stat label="Holiday" value={s.holiday_days} color="var(--st-holiday)" icon={<PartyPopper className="size-3.5" />} {...filterProps("HOLIDAY")} />
          {/* Leave only comes from imported files: shown when there is any, WFH otherwise. */}
          {s.leave_days > 0 && !s.wfh_days
            ? <Stat label="Leave" value={s.leave_days} color="var(--st-leave)" icon={<Plane className="size-3.5" />} {...filterProps("LEAVE")} />
            : <Stat label="WFH" value={s.wfh_days} color="var(--st-wfh)" icon={<Home className="size-3.5" />} {...filterProps("WFH")} />}
        </>}
      />
    </div>
  );
}
