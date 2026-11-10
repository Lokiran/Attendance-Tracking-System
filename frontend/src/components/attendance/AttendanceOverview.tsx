import { Building2, CalendarCheck2, CalendarX2, Clock3, Gauge, Home, Plane, Timer } from "lucide-react";
import type { ReactNode } from "react";

import { Card } from "@/components/ui/Card";
import { Skeleton } from "@/components/ui/Skeleton";
import { BAND_META, bandMessage, bandOf, WFO_NEAR_DAYS, WfoBandPill, WfoTargetBar } from "@/components/wfo/WfoParts";
import { formatMinutes, formatPercent } from "@/lib/format";
import type { Summary } from "@/lib/types";

const days = (n: number) => (n === 1 ? "1 day" : `${n} days`);

function Panel({ title, icon, children, footer }: { title: string; icon: ReactNode; children: ReactNode; footer?: ReactNode }) {
  return (
    <Card className="flex flex-col">
      <div className="flex items-center gap-2 px-5 pt-4 text-sm font-semibold text-ink">
        <span className="rounded-md bg-brand-soft p-1.5 text-brand-text" aria-hidden>{icon}</span>
        {title}
      </div>
      <div className="flex-1 px-5 pb-4 pt-3">{children}</div>
      {footer && <div className="border-t border-line px-5 py-2.5 text-xs text-ink-3">{footer}</div>}
    </Card>
  );
}

function Stat({ label, value, hint, icon }: { label: string; value: ReactNode; hint?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="flex items-center gap-1.5 text-xs font-medium text-ink-2">{icon}{label}</div>
      <div className="tabular mt-1 text-xl font-semibold tracking-tight text-ink">{value}</div>
      {hint && <div className="mt-0.5 text-xs text-ink-3">{hint}</div>}
    </div>
  );
}

/** The three panels at the top of an employee's dashboard: WFO target, office hours, attendance. */
export function AttendanceOverview({ s }: { s?: Summary }) {
  if (!s) {
    return (
      <div className="grid gap-4 lg:grid-cols-3">
        {[0, 1, 2].map((i) => <Card key={i} className="p-5"><Skeleton className="h-36" /></Card>)}
      </div>
    );
  }
  const w = s.wfo;
  const band = w ? bandOf(w.wfo_days, w.required_days, w.wfo_band) : null;
  const shiftPerDay = s.expected_days ? Math.round(s.required_minutes / s.expected_days) : null;
  return (
    <div className="grid gap-4 lg:grid-cols-3">
      <Panel
        title="Work from office"
        icon={<Building2 className="size-4" />}
        footer={<>Green: {w?.required_days ?? 12}+ days · Amber: up to {WFO_NEAR_DAYS} short · Red: more than {WFO_NEAR_DAYS} short</>}
      >
        {w && band ? (
          <>
            <div className="flex items-start justify-between gap-3">
              <div>
                <span className="tabular text-4xl font-semibold tracking-tight text-ink">{w.wfo_days}</span>
                <span className="ml-1.5 text-sm text-ink-2">of {w.required_days} required days</span>
              </div>
              <WfoBandPill band={band} />
            </div>
            <div className="mt-3">
              <WfoTargetBar wfo={w.wfo_days} required={w.required_days} band={band} label={`WFO days: ${w.wfo_days} of ${w.required_days} required`} />
            </div>
            <p className="mt-2 text-sm text-ink">
              <span className="inline-block size-2 rounded-full align-middle" style={{ background: BAND_META[band].color }} aria-hidden />{" "}
              {bandMessage(w.wfo_days, w.required_days)}
            </p>
            <p className="mt-1 text-xs text-ink-3">
              WFO {formatPercent(w.wfo_percentage)} of {days(w.working_days)} worked
              {w.required_days < 12 && w.working_days > 0 ? " · requirement prorated for this period" : ""}
            </p>
          </>
        ) : (
          <p className="text-sm text-ink-3">No WFO figures for this period.</p>
        )}
      </Panel>

      <Panel
        title="Office hours"
        icon={<Clock3 className="size-4" />}
        footer={s.hours_days
          ? <>Recorded on {days(s.hours_days)}: {formatMinutes(s.net_minutes)} of {formatMinutes(s.hours_required_minutes)} required</>
          : <>This timesheet lists the days only. Hours appear when it has In / Out times or an Hours column.</>}
      >
        <div className="grid grid-cols-2 gap-4">
          <Stat
            label="Required"
            icon={<CalendarCheck2 className="size-3.5 text-ink-3" aria-hidden />}
            value={formatMinutes(s.required_minutes)}
            hint={shiftPerDay ? `${days(s.expected_days)} × ${formatMinutes(shiftPerDay)} shift` : "no scheduled days"}
          />
          <Stat
            label="Average in office"
            icon={<Timer className="size-3.5 text-ink-3" aria-hidden />}
            value={s.average_net_minutes !== null ? `${formatMinutes(s.average_net_minutes)}` : <span className="text-base text-ink-2">Not recorded</span>}
            hint={shiftPerDay ? `per day · target ${formatMinutes(shiftPerDay)}` : "per day"}
          />
        </div>
        {s.hours_days > 0 && shiftPerDay !== null && s.average_net_minutes !== null && (
          <div className="mt-4">
            <div className="mb-1 flex justify-between text-xs text-ink-3">
              <span>Average vs shift</span>
              <span className="tabular">{Math.round((s.average_net_minutes / shiftPerDay) * 100)}%</span>
            </div>
            <div className="h-2 overflow-hidden rounded-full bg-surface-3" role="progressbar" aria-label="Average office hours against the shift"
              aria-valuemin={0} aria-valuemax={shiftPerDay} aria-valuenow={Math.min(s.average_net_minutes, shiftPerDay)}>
              <div className="h-full rounded-full bg-brand" style={{ width: `${Math.min(100, (s.average_net_minutes / shiftPerDay) * 100)}%` }} />
            </div>
          </div>
        )}
      </Panel>

      <Panel
        title="Attendance"
        icon={<Gauge className="size-4" />}
        footer={<>{s.present_expected_days} of {days(s.expected_days)} scheduled in the office{s.no_timesheet_days ? ` · ${days(s.no_timesheet_days)} not in a timesheet yet` : ""}</>}
      >
        <div className="flex items-baseline gap-2">
          <span className="tabular text-4xl font-semibold tracking-tight text-ink">{formatPercent(s.attendance_percentage)}</span>
          <span className="text-sm text-ink-2">attendance rate</span>
        </div>
        <div className="mt-3 h-2 overflow-hidden rounded-full bg-surface-3" role="progressbar" aria-label="Attendance rate"
          aria-valuemin={0} aria-valuemax={100} aria-valuenow={s.attendance_percentage ?? 0}>
          <div className="h-full rounded-full bg-brand" style={{ width: `${s.attendance_percentage ?? 0}%` }} />
        </div>
        <div className="mt-4 grid grid-cols-4 gap-2">
          <Stat label="In office" icon={<CalendarCheck2 className="size-3.5" style={{ color: "var(--st-present)" }} aria-hidden />} value={s.present_days} />
          <Stat label="Absent" icon={<CalendarX2 className="size-3.5" style={{ color: "var(--st-absent)" }} aria-hidden />} value={s.absent_days} />
          <Stat label="WFH" icon={<Home className="size-3.5" style={{ color: "var(--st-wfh)" }} aria-hidden />} value={s.wfh_days} />
          <Stat label="Leave" icon={<Plane className="size-3.5" style={{ color: "var(--st-leave)" }} aria-hidden />} value={s.leave_days} />
        </div>
      </Panel>
    </div>
  );
}
