import { Building2, CalendarX2, Clock3, Gauge, Home, Plane, CalendarCheck2 } from "lucide-react";
import type { ReactNode } from "react";

import { Card } from "@/components/ui/Card";
import { Skeleton } from "@/components/ui/Skeleton";
import { BAND_META, bandMessage, bandOf, WFO_NEAR_DAYS, WfoBandPill, WfoTargetBar } from "@/components/wfo/WfoParts";
import { formatMinutes, formatPercent } from "@/lib/format";
import type { Summary } from "@/lib/types";

const days = (n: number) => (n === 1 ? "1 day" : `${n} days`);

/** One panel: title (with an optional badge), a big figure, a bar, one line of context. Every panel uses the same
 *  layout so the row reads at a glance. */
function Panel({ title, icon, badge, value, unit, bar, caption, children }: {
  title: string;
  icon: ReactNode;
  badge?: ReactNode;
  value: ReactNode;
  unit?: ReactNode;
  bar?: ReactNode;
  caption?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <Card className="flex flex-col p-5">
      <div className="flex items-center justify-between gap-3">
        <h2 className="flex items-center gap-2 text-sm font-medium text-ink-2">
          <span className="text-ink-3" aria-hidden>{icon}</span>
          {title}
        </h2>
        {badge}
      </div>
      <div className="mt-3 flex flex-wrap items-baseline gap-x-2">
        <span className="tabular text-4xl font-semibold tracking-tight text-ink">{value}</span>
        {unit && <span className="text-sm text-ink-2">{unit}</span>}
      </div>
      {bar && <div className="mt-3">{bar}</div>}
      {caption && <p className="mt-2.5 text-sm text-ink-2">{caption}</p>}
      {children && <div className="mt-auto pt-4">{children}</div>}
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

function Count({ label, value, icon, color }: { label: string; value: number; icon: ReactNode; color: string }) {
  return (
    <div className="min-w-0">
      <div className="tabular text-lg font-semibold text-ink">{value}</div>
      <div className="flex items-center gap-1 text-xs text-ink-3">
        <span style={{ color }} aria-hidden>{icon}</span>
        {label}
      </div>
    </div>
  );
}

function BandLegend({ required }: { required: number }) {
  const items = [
    ["MET", `${required}+ days`],
    ["NEAR", `up to ${WFO_NEAR_DAYS} short`],
    ["BELOW", `more than ${WFO_NEAR_DAYS} short`],
  ] as const;
  return (
    <ul className="flex flex-wrap gap-x-3 gap-y-1 border-t border-line pt-3 text-xs text-ink-3" aria-label="WFO colours">
      {items.map(([b, text]) => (
        <li key={b} className="inline-flex items-center gap-1.5">
          <span className="size-2 rounded-full" style={{ background: BAND_META[b].color }} aria-hidden />
          {text}
        </li>
      ))}
    </ul>
  );
}

/** The three panels at the top of an employee's dashboard: WFO target, office hours, attendance. */
export function AttendanceOverview({ s }: { s?: Summary }) {
  if (!s) {
    return (
      <div className="grid gap-4 lg:grid-cols-3">
        {[0, 1, 2].map((i) => <Card key={i} className="p-5"><Skeleton className="h-40" /></Card>)}
      </div>
    );
  }
  const w = s.wfo;
  const band = w ? bandOf(w.wfo_days, w.required_days, w.wfo_band) : null;
  const shiftPerDay = s.expected_days ? Math.round(s.required_minutes / s.expected_days) : null;
  const avg = s.hours_days > 0 ? s.average_net_minutes : null;

  return (
    <div className="grid gap-4 lg:grid-cols-3">
      {w && band ? (
        <Panel
          title="Work from office"
          icon={<Building2 className="size-4" />}
          badge={<WfoBandPill band={band} />}
          value={w.wfo_days}
          unit={`of ${w.required_days} days required`}
          bar={<WfoTargetBar wfo={w.wfo_days} required={w.required_days} band={band} label={`WFO days: ${w.wfo_days} of ${w.required_days} required`} />}
          caption={<>
            {bandMessage(w.wfo_days, w.required_days)}
            {w.required_days < 12 && w.working_days > 0 && <span className="text-ink-3"> · prorated for this period</span>}
          </>}
        >
          <BandLegend required={w.required_days} />
        </Panel>
      ) : (
        <Panel title="Work from office" icon={<Building2 className="size-4" />} value="—" caption="No WFO figures for this period." />
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
      >
        <div className="flex items-baseline justify-between gap-3 border-t border-line pt-3 text-sm">
          <span className="text-ink-2">Required this period</span>
          <span className="tabular font-semibold text-ink">{formatMinutes(s.required_minutes)}</span>
        </div>
        {shiftPerDay !== null && <p className="mt-0.5 text-right text-xs text-ink-3">{days(s.expected_days)} × {formatMinutes(shiftPerDay)}</p>}
      </Panel>

      <Panel
        title="Attendance"
        icon={<Gauge className="size-4" />}
        value={formatPercent(s.attendance_percentage)}
        unit={`${s.present_expected_days} of ${days(s.expected_days)}`}
        bar={<Bar value={s.attendance_percentage ?? 0} max={100} label="Attendance rate" />}
        caption={s.no_timesheet_days ? <span className="text-ink-3">{days(s.no_timesheet_days)} not in a timesheet yet</span> : undefined}
      >
        <div className="grid grid-cols-4 gap-2 border-t border-line pt-3">
          <Count label="Office" value={s.present_days} color="var(--st-present)" icon={<CalendarCheck2 className="size-3.5" />} />
          <Count label="Absent" value={s.absent_days} color="var(--st-absent)" icon={<CalendarX2 className="size-3.5" />} />
          <Count label="WFH" value={s.wfh_days} color="var(--st-wfh)" icon={<Home className="size-3.5" />} />
          <Count label="Leave" value={s.leave_days} color="var(--st-leave)" icon={<Plane className="size-3.5" />} />
        </div>
      </Panel>
    </div>
  );
}
