/* eslint-disable @typescript-eslint/no-explicit-any */
import { useMutation } from "@tanstack/react-query";
import { clsx } from "clsx";
import { AlertTriangle, Building2, CalendarCheck2, CheckCircle2, Clock3, Gauge, Home, PenLine, Target, XCircle } from "lucide-react";
import { useEffect, useState } from "react";
import { Cell, Pie, PieChart, ResponsiveContainer, Tooltip } from "recharts";

import { Button } from "@/components/ui/Button";
import { Field, Input, Select, TextArea } from "@/components/ui/Fields";
import { Modal } from "@/components/ui/Modal";
import { CardGrid, SummaryCard } from "@/components/ui/SummaryCard";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import { formatDate, formatMinutes, formatPercent } from "@/lib/format";
import { useInvalidateAttendance } from "@/lib/queries";
import type { Compliance, EmployeeWfo, WfoDay, WfoPolicy, WfoSummary, WfoWeek, WorkMode } from "@/lib/types";

/**
 * Work-mode colours reuse the validated chart series (dataviz validator: light and dark pass; the light
 * WFH teal is below 3:1 on white, so every mark also carries a text label and charts have a table view).
 */
export const MODE_META: Record<WorkMode, { label: string; short: string; color: string }> = {
  OFFICE: { label: "Work from office", short: "WFO", color: "var(--series-1)" },
  WFH: { label: "Work from home", short: "WFH", color: "var(--series-3)" },
  LEAVE: { label: "Leave", short: "Leave", color: "var(--series-2)" },
  HOLIDAY: { label: "Holiday", short: "Holiday", color: "var(--st-holiday)" },
  WEEKOFF: { label: "Weekly off", short: "Off", color: "var(--st-off)" },
};

export const SOURCE_LABEL: Record<string, string> = {
  MANUAL_OVERRIDE: "Admin override", UPLOAD_COLUMN: "Imported file", BADGE: "Badge / swipe", WIFI: "Office Wi-Fi",
  CHECK_IN: "Check-in choice", INFERRED: "Hours with no office signal", CALENDAR: "Calendar",
};

const hours = (m: number) => `${(m / 60).toFixed(m % 60 ? 1 : 0)} h`;

export function thisMonth(today = new Date()): string {
  return `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, "0")}`;
}

export function MonthPicker({ value, onChange, id = "wfo-month" }: { value: string; onChange: (m: string) => void; id?: string }) {
  return (
    <div className="flex items-center gap-2">
      <label htmlFor={id} className="text-xs text-ink-3">Month</label>
      <Input id={id} type="month" className="w-40" value={value} max={thisMonth()} onChange={(e) => e.target.value && onChange(e.target.value)} />
    </div>
  );
}

export function ComplianceBadge({ value }: { value: Compliance }) {
  const ok = value === "COMPLIANT";
  const Icon = ok ? CheckCircle2 : XCircle;
  return (
    <span className="inline-flex items-center gap-1 rounded-full border border-line px-2 py-0.5 text-xs font-medium text-ink">
      <Icon className="size-3.5" style={{ color: ok ? "var(--good)" : "var(--critical)" }} aria-hidden />
      {ok ? "Compliant" : "Non-compliant"}
    </span>
  );
}

export function WfoKpis({ summary: s, policy, loading }: { summary?: WfoSummary; policy?: WfoPolicy; loading?: boolean }) {
  return (
    <CardGrid cols={6}>
      <SummaryCard label="WFO days" icon={<Building2 className="size-4" />} loading={loading} value={s?.wfo_days}
        hint={s && policy && `${s.office_days_below_minimum ? `+${s.office_days_below_minimum} under ${hours(policy.min_office_minutes)} · ` : ""}needs ${s.required_days}`} />
      <SummaryCard label="WFH days" icon={<Home className="size-4" />} loading={loading} value={s?.wfh_days}
        hint={s && (s.leave_days ? `${s.leave_days} leave day(s) excluded` : "days worked from home")} />
      <SummaryCard label="WFO %" icon={<Gauge className="size-4" />} loading={loading} value={formatPercent(s?.wfo_percentage)}
        hint={s && `of ${s.working_days} working day(s)`} />
      <SummaryCard label="Compliance" icon={<Target className="size-4" />} loading={loading}
        value={s ? <ComplianceBadge value={s.compliance} /> : null}
        hint={policy && `${policy.min_days_per_week}/week · ${policy.min_days_per_month}/month`} />
      <SummaryCard label="Shortfall" icon={<AlertTriangle className="size-4" />} loading={loading} value={s?.shortfall_days}
        hint={s && (s.shortfall_days ? "office day(s) short" : "nothing short")} />
      <SummaryCard label="Min. office time" icon={<Clock3 className="size-4" />} loading={loading}
        value={policy ? hours(policy.min_office_minutes) : undefined} hint="for a day to count as WFO" />
    </CardGrid>
  );
}

export function WeeklyProgress({ weeks, policy }: { weeks: WfoWeek[]; policy: WfoPolicy }) {
  const shown = weeks.filter((w) => w.working_days > 0 || w.status === "IN_PROGRESS" || w.status === "UPCOMING");
  if (!shown.length) return <p className="px-5 py-6 text-sm text-ink-3">No working days in this month yet.</p>;
  return (
    <ul className="divide-y divide-line">
      {shown.map((w) => {
        if (w.status === "UPCOMING")
          return (
            <li key={w.week_start} className="flex items-center justify-between px-5 py-3 text-sm text-ink-3">
              <span>{w.label}</span><span className="text-xs">Not started</span>
            </li>
          );
        const pct = w.required ? Math.min(100, Math.round((w.wfo_days / w.required) * 100)) : 0;
        const label = w.status === "IN_PROGRESS" ? "In progress" : w.status === "MET" ? (w.required ? "Met" : "Nothing required") : `${w.shortfall} short`;
        return (
          <li key={w.week_start} className="px-5 py-3">
            <div className="flex items-center justify-between gap-3 text-sm">
              <span className="text-ink">{w.label}</span>
              <span className="tabular text-ink-2">
                <span className="font-semibold text-ink">{w.wfo_days}</span> of {w.required} day{w.required === 1 ? "" : "s"}
                <span className={clsx("ml-2 text-xs", w.status === "SHORT" ? "text-critical-text" : "text-ink-3")}>· {label}</span>
              </span>
            </div>
            <div className="mt-1.5 h-2 overflow-hidden rounded-full bg-surface-3" role="progressbar" aria-label={`${w.label}: ${w.wfo_days} of ${w.required} WFO days`}
              aria-valuemin={0} aria-valuemax={w.required} aria-valuenow={Math.min(w.wfo_days, w.required)}>
              <div className="h-full rounded-full" style={{ width: pct + "%", background: MODE_META.OFFICE.color }} />
            </div>
            {w.working_days < 5 && w.required < policy.min_days_per_week && (
              <p className="mt-1 text-[11px] text-ink-3">{w.working_days} working day(s) this week, so {w.required} required (prorated)</p>
            )}
          </li>
        );
      })}
    </ul>
  );
}

const WEEKDAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

/** Month heatmap: each day filled with its work mode's colour and labelled (colour is never the only cue). */
export function WfoCalendar({ data, onSelectDay }: { data: EmployeeWfo; onSelectDay?: (d: WfoDay) => void }) {
  const first = new Date(data.start + "T00:00:00");
  const lead = (first.getDay() + 6) % 7;
  return (
    <div className="p-4">
      <div className="grid grid-cols-7 gap-1.5 text-center text-[11px] font-medium text-ink-3">
        {WEEKDAYS.map((d) => <div key={d}>{d}</div>)}
      </div>
      <div className="mt-1.5 grid grid-cols-7 gap-1.5">
        {Array.from({ length: lead }).map((_, i) => <div key={"pad" + i} />)}
        {data.days.map((d) => {
          const meta = d.work_mode ? MODE_META[d.work_mode] : null;
          const filled = meta && !d.future;
          const below = d.work_mode === "OFFICE" && !d.is_wfo_day && !d.in_progress;
          const title = [formatDate(d.date, { weekday: "long" }), meta?.label ?? (d.future ? "Upcoming" : d.scheduled ? "No attendance" : ""),
            d.wfo_source ? `Source: ${SOURCE_LABEL[d.wfo_source]}` : "", d.office_minutes ? `Office ${formatMinutes(d.office_minutes)}` : "",
            d.wfh_minutes ? `WFH ${formatMinutes(d.wfh_minutes)}` : "", below ? "Below the minimum office time" : "",
            ...d.conflicts, d.override_note ? `Note: ${d.override_note}` : ""].filter(Boolean).join("\n");
          const content = (
            <>
              <span className="flex items-center justify-between text-xs font-medium text-ink">
                {Number(d.date.slice(8))}
                <span className="flex gap-0.5">
                  {d.conflicts.length > 0 && <AlertTriangle className="size-3" style={{ color: "var(--st-incomplete)" }} aria-label="Conflicting signals" />}
                  {d.overridden && <PenLine className="size-3 text-ink-2" aria-label="Overridden by an admin" />}
                </span>
              </span>
              <span className="mt-auto truncate text-left text-[11px] text-ink-2">
                {d.in_progress ? (meta ? meta.short + " · today" : "Today") : filled ? meta.short + (below ? " <min" : "") : d.future ? "" : d.scheduled ? "—" : ""}
              </span>
            </>
          );
          const style = filled
            ? { background: `color-mix(in srgb, ${meta.color} ${below ? 10 : 24}%, var(--surface))`, borderColor: meta.color, borderStyle: below ? "dashed" : "solid" }
            : undefined;
          const cls = clsx("flex h-14 flex-col rounded-lg border p-1.5 text-left", !filled && "border-line", d.in_progress && "ring-2 ring-brand", d.future && "opacity-50");
          return onSelectDay && !d.future ? (
            <button key={d.date} type="button" title={title} aria-label={title} className={clsx(cls, "hover:brightness-95")} style={style as any} onClick={() => onSelectDay(d)}>
              {content}
            </button>
          ) : (
            <div key={d.date} title={title} aria-label={title} className={cls} style={style as any}>{content}</div>
          );
        })}
      </div>
      <ul className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-ink-2" aria-label="Legend">
        {(["OFFICE", "WFH", "LEAVE", "HOLIDAY", "WEEKOFF"] as WorkMode[]).map((m) => (
          <li key={m} className="flex items-center gap-1.5"><span className="inline-block size-2.5 rounded-sm" style={{ background: MODE_META[m].color }} aria-hidden />{MODE_META[m].label}</li>
        ))}
        <li className="flex items-center gap-1.5"><span className="inline-block size-2.5 rounded-sm border border-dashed" style={{ borderColor: MODE_META.OFFICE.color }} aria-hidden />Office, under the minimum time</li>
      </ul>
    </div>
  );
}

/** Two-slice donut, WFO vs WFH, with the WFO share in the middle and labelled slices. */
export function WfoDonut({ summary }: { summary: WfoSummary }) {
  const data = [
    { key: "OFFICE", name: "WFO", value: summary.wfo_days, color: MODE_META.OFFICE.color },
    { key: "WFH", name: "WFH", value: summary.wfh_days, color: MODE_META.WFH.color },
  ];
  const total = summary.wfo_days + summary.wfh_days;
  const share = total ? Math.round((summary.wfo_days / total) * 100) : 0;
  return (
    <div className="relative h-full">
      <ResponsiveContainer>
        <PieChart>
          <Pie data={data} dataKey="value" nameKey="name" innerRadius="62%" outerRadius="88%" paddingAngle={total && summary.wfo_days && summary.wfh_days ? 2 : 0}
            stroke="var(--surface)" strokeWidth={2} isAnimationActive={false} startAngle={90} endAngle={-270}>
            {data.map((d) => <Cell key={d.key} fill={d.color} />)}
          </Pie>
          <Tooltip content={({ active, payload }: any) => active && payload?.length ? (
            <div className="rounded-lg border border-line bg-surface px-3 py-2 text-xs shadow-lg">
              <span className="font-medium text-ink">{payload[0].name}</span>
              <span className="tabular ml-3 text-ink-2">{payload[0].value} day(s)</span>
            </div>
          ) : null} />
        </PieChart>
      </ResponsiveContainer>
      <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center">
        <span className="tabular text-2xl font-semibold text-ink">{total ? share + "%" : "—"}</span>
        <span className="text-[11px] text-ink-3">in office</span>
      </div>
    </div>
  );
}

const MODES: WorkMode[] = ["OFFICE", "WFH", "LEAVE", "HOLIDAY", "WEEKOFF"];

/** Admin: set or remove a day's work mode (audited by the backend). */
export function OverrideDialog({ employeeId, employeeName, day, onClose }: {
  employeeId: number;
  employeeName: string;
  day: WfoDay | null;
  onClose: () => void;
}) {
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const [mode, setMode] = useState<WorkMode>("OFFICE");
  const [note, setNote] = useState("");
  useEffect(() => {
    if (day) {
      setMode(day.work_mode ?? "OFFICE");
      setNote(day.override_note ?? "");
    }
  }, [day]);
  const url = day ? `/admin/attendance/${employeeId}/${day.date}/work-mode` : "";
  const save = useMutation({
    mutationFn: () => api.put(url, { work_mode: mode, note: note.trim() || null }),
    onSuccess: () => { toast.success("Work mode overridden"); invalidate(); onClose(); },
    onError: (e) => toast.error(e),
  });
  const clear = useMutation({
    mutationFn: () => api.del(url),
    onSuccess: () => { toast.success("Override removed"); invalidate(); onClose(); },
    onError: (e) => toast.error(e),
  });
  return (
    <Modal open={!!day} onClose={onClose} size="sm" title={day ? `${employeeName} · ${formatDate(day.date, { weekday: "short" })}` : ""}
      description="Overrides win over every other signal and are recorded in the audit log (who, when, old and new value)."
      footer={
        <>
          {day?.overridden && <Button variant="ghost" loading={clear.isPending} onClick={() => clear.mutate()}>Remove override</Button>}
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button loading={save.isPending} onClick={() => save.mutate()}>Save override</Button>
        </>
      }>
      {day && (
        <div className="space-y-4">
          <p className="text-xs text-ink-2">
            Currently <span className="font-medium text-ink">{day.work_mode ? MODE_META[day.work_mode].label : "no work mode"}</span>
            {day.wfo_source && <> · from {SOURCE_LABEL[day.wfo_source].toLowerCase()}</>}
          </p>
          {day.conflicts.length > 0 && (
            <ul className="rounded-lg border border-line bg-surface-2 p-2 text-xs text-ink-2">
              {day.conflicts.map((c) => <li key={c} className="flex gap-1.5"><AlertTriangle className="mt-0.5 size-3 shrink-0" style={{ color: "var(--st-incomplete)" }} aria-hidden />{c}</li>)}
            </ul>
          )}
          <Field label="Work mode" htmlFor="ov-mode">
            <Select id="ov-mode" value={mode} onChange={(e) => setMode(e.target.value as WorkMode)}>
              {MODES.map((m) => <option key={m} value={m}>{MODE_META[m].label}</option>)}
            </Select>
          </Field>
          <Field label="Note (optional)" htmlFor="ov-note">
            <TextArea id="ov-note" value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} placeholder="e.g. Client visit; badge reader was down" />
          </Field>
        </div>
      )}
    </Modal>
  );
}

export function PolicyLine({ policy }: { policy: WfoPolicy }) {
  return (
    <p className="flex items-center gap-1.5 text-xs text-ink-3">
      <CalendarCheck2 className="size-3.5" aria-hidden />
      Policy: {policy.min_days_per_week} office days a week, {policy.min_days_per_month} a month, at least {hours(policy.min_office_minutes)} in the office for a day to count.
    </p>
  );
}
