import { clsx } from "clsx";
import { AlarmClock, ChevronLeft, ChevronRight, FileText, LogIn, LogOut, Timer, TrendingUp } from "lucide-react";
import { useEffect, type ReactNode } from "react";

import { Button } from "@/components/ui/Button";
import { Modal } from "@/components/ui/Modal";
import { TableSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { ExceptionTags, STATUS_META, StatusBadge } from "@/components/ui/StatusBadge";
import { addDays, isoWeekday, minDate, todayInOffice } from "@/lib/dates";
import { clockTime, duration, formatDate, formatDateTime, formatMinutes, minutesOfDay, signedMinutes, STATUS_LABEL } from "@/lib/format";
import { useDayDetail, useEmployeeDays } from "@/lib/queries";
import type { DayRecord, WfoSource } from "@/lib/types";

const SOURCE_LABEL: Record<WfoSource, string> = {
  MANUAL_OVERRIDE: "Set by HR (override)",
  UPLOAD_COLUMN: "Imported timesheet",
  BADGE: "Badge / swipe rows of an imported file",
  WIFI: "Office Wi-Fi",
  CHECK_IN: "Check-in (retired)",
  INFERRED: "Hours without a work mode in the timesheet",
  CALENDAR: "Calendar (holiday / weekly off)",
};

const toMin = (t: string) => Number(t.slice(0, 2)) * 60 + Number(t.slice(3, 5));

function Item({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-ink-3">{label}</dt>
      <dd className="mt-1 break-words text-sm font-medium text-ink">{children}</dd>
    </div>
  );
}

/** A figure with a one-line comparison underneath ("14m after shift start"). */
function Tile({ icon, label, value, note, tone }: { icon: ReactNode; label: string; value: ReactNode; note?: ReactNode; tone?: "warn" | "good" }) {
  return (
    <div className="min-w-0 rounded-lg border border-line bg-surface-2/50 p-3">
      <div className="flex items-center gap-1.5 text-xs text-ink-3"><span aria-hidden>{icon}</span>{label}</div>
      <div className="tabular mt-1 text-lg font-semibold text-ink">{value}</div>
      {note && (
        <div className="mt-0.5 flex items-start gap-1 text-xs leading-4 text-ink-2">
          {tone && <span aria-hidden className="mt-1 size-1.5 shrink-0 rounded-full" style={{ background: tone === "warn" ? "var(--serious)" : "var(--good)" }} />}
          <span>{note}</span>
        </div>
      )}
    </div>
  );
}

/** The day on a time axis: the shift window as a band, the time in the office as a bar, In / Out marked. */
function DayTimeline({ inMin, outMin, shift, lateAfter }: { inMin: number; outMin: number | null; shift: { start: number; end: number } | null; lateAfter: number | null }) {
  const end = outMin ?? inMin;
  const lo = Math.floor((Math.min(inMin, shift?.start ?? inMin) - 45) / 60) * 60;
  const hi = Math.ceil((Math.max(end, shift?.end ?? end) + 45) / 60) * 60;
  const pos = (m: number) => `${((m - lo) / (hi - lo)) * 100}%`;
  const ticks: number[] = [];
  for (let t = lo; t <= hi; t += hi - lo > 12 * 60 ? 120 : 60) ticks.push(t);
  const label = `In ${clockTime(inMin)}${outMin !== null ? `, out ${clockTime(outMin)}` : ""}${shift ? `; shift ${clockTime(shift.start)} to ${clockTime(shift.end)}` : ""}`;
  return (
    <div className="rounded-lg border border-line p-4" role="img" aria-label={label}>
      <div className="relative mb-1 h-5 text-[11px] font-medium text-ink-2">
        <span className="absolute -translate-x-1/2 whitespace-nowrap" style={{ left: pos(inMin) }}>In {clockTime(inMin)}</span>
        {outMin !== null && <span className="absolute -translate-x-1/2 whitespace-nowrap" style={{ left: pos(outMin) }}>Out {clockTime(outMin)}</span>}
      </div>
      <div className="relative h-8">
        <div className="absolute inset-x-0 top-1/2 h-px -translate-y-1/2 bg-line" />
        {shift && (
          <div className="absolute inset-y-0 rounded-md border border-dashed border-line-strong bg-surface-3/70"
            style={{ left: pos(shift.start), width: `calc(${pos(shift.end)} - ${pos(shift.start)})` }} />
        )}
        {lateAfter !== null && <div className="absolute inset-y-0 w-0 border-l-2 border-dashed" style={{ left: pos(lateAfter), borderColor: "var(--serious)" }} />}
        <div className="absolute top-1/2 h-3 -translate-y-1/2 rounded-full" style={{ left: pos(inMin), width: `calc(${pos(end)} - ${pos(inMin)})`, minWidth: 6, background: "var(--series-1)" }} />
        <div className="absolute top-1/2 size-3 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-surface" style={{ left: pos(inMin), background: "var(--series-1)" }} />
        {outMin !== null && <div className="absolute top-1/2 size-3 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-surface" style={{ left: pos(outMin), background: "var(--series-1)" }} />}
      </div>
      <div className="relative mt-1 h-4 text-[10px] text-ink-3">
        {ticks.map((t, i) => <span key={t} className={clsx("absolute whitespace-nowrap", i === 0 ? "" : i === ticks.length - 1 ? "-translate-x-full" : "-translate-x-1/2")} style={{ left: pos(t) }}>{clockTime(t).replace(":00", "").replace(/^0/, "")}</span>)}
      </div>
      <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-ink-3">
        <span className="inline-flex items-center gap-1.5"><span className="inline-block h-2 w-4 rounded-full" style={{ background: "var(--series-1)" }} />In the office</span>
        {shift && <span className="inline-flex items-center gap-1.5"><span className="inline-block h-2.5 w-4 rounded-sm border border-dashed border-line-strong bg-surface-3" />Shift {clockTime(shift.start)} – {clockTime(shift.end)}</span>}
        {lateAfter !== null && <span className="inline-flex items-center gap-1.5"><span className="inline-block h-3 w-0 border-l-2 border-dashed" style={{ borderColor: "var(--serious)" }} />Late after {clockTime(lateAfter)}</span>}
      </div>
    </div>
  );
}

/** Mon-Sun around the day, each a button that opens that day. */
function WeekAround({ employeeId, date, onNavigate }: { employeeId: number; date: string; onNavigate: (d: string) => void }) {
  const today = todayInOffice();
  const weekStart = addDays(date, 1 - isoWeekday(date));
  const weekEnd = addDays(weekStart, 6);
  const q = useEmployeeDays(weekStart <= today ? employeeId : undefined, { start: weekStart, end: minDate(weekEnd, today), page_size: 7, order: "asc" });
  const byDate = new Map<string, DayRecord>((q.data?.items ?? []).map((d) => [d.date, d]));
  const office = (q.data?.items ?? []).filter((d) => d.status === "PRESENT");
  const total = office.reduce((n, d) => n + (d.hours_known ? d.net_minutes : 0), 0);
  return (
    <div>
      <div className="mb-2 flex items-baseline justify-between gap-2">
        <h3 className="text-xs font-medium text-ink-2">That week</h3>
        <span className="text-xs text-ink-3">{office.length} office day{office.length === 1 ? "" : "s"}{total > 0 ? ` · ${formatMinutes(total)}` : ""}</span>
      </div>
      <div className="grid grid-cols-7 gap-1.5">
        {Array.from({ length: 7 }, (_, i) => addDays(weekStart, i)).map((d) => {
          const rec = byDate.get(d);
          const meta = rec ? STATUS_META[rec.status] : null;
          const off = !rec || rec.status === "WEEKEND" || rec.status === "NOT_SCHEDULED";
          const Icon = meta?.icon;
          return (
            <button
              key={d}
              type="button"
              disabled={!rec}
              onClick={() => onNavigate(d)}
              aria-current={d === date ? "date" : undefined}
              aria-label={formatDate(d, { weekday: "long" }) + (rec ? ": " + STATUS_LABEL[rec.status] : "")}
              className={clsx(
                "flex min-w-0 flex-col items-start rounded-lg border p-1.5 text-left transition-colors",
                d === date ? "border-brand ring-1 ring-brand" : "border-line",
                off ? "bg-surface-2" : "hover:bg-surface-2",
                !rec && "cursor-default opacity-50",
              )}
            >
              <span className="text-[10px] text-ink-3">{formatDate(d, { weekday: "short", day: undefined, month: undefined, year: undefined })}</span>
              <span className="tabular text-xs font-medium text-ink">{Number(d.slice(8))}</span>
              <span className="mt-0.5 flex w-full items-center gap-1">
                {Icon && <Icon className="size-3 shrink-0" style={{ color: meta!.color }} aria-hidden />}
                <span className="tabular truncate text-[10px] text-ink-2">{rec?.hours_known ? formatMinutes(rec.net_minutes) : rec && !off ? STATUS_LABEL[rec.status] : ""}</span>
              </span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

function headline(rec: DayRecord): string {
  switch (rec.status) {
    case "PRESENT": {
      if (!rec.hours_known) return "In the office (the timesheet lists the day without hours)";
      const diff = rec.net_minutes - rec.required_minutes;
      if (!rec.required_minutes) return `In the office for ${formatMinutes(rec.net_minutes)} on a day off`;
      return diff < 0
        ? `In the office for ${formatMinutes(rec.net_minutes)}, ${duration(diff)} short of the ${formatMinutes(rec.required_minutes)} shift`
        : `In the office for ${formatMinutes(rec.net_minutes)}, the full ${formatMinutes(rec.required_minutes)} shift${diff > 0 ? ` and ${duration(diff)} more` : ""}`;
    }
    case "ABSENT": return "A working day with no timesheet entry: counted as absent";
    case "HOLIDAY": return rec.holiday_name ? `Holiday: ${rec.holiday_name}` : "Holiday";
    case "WFH": return "Worked from home";
    case "LEAVE": return "On leave";
    case "WEEKEND": return "Weekly off";
    case "NO_TIMESHEET": return "No timesheet covering this day has been imported yet";
    default: return "Not a scheduled working day";
  }
}

/** One day: what happened, against the shift and the person's average, the week around it, and the timesheet
 *  entry behind it. Arrow keys (or the buttons) move to the previous / next day. */
export function DayDetailModal({ employeeId, date, onClose, onNavigate, averageMinutes }: {
  employeeId: number;
  date: string | null;
  onClose: () => void;
  onNavigate?: (date: string) => void;
  averageMinutes?: number | null;
}) {
  const detail = useDayDetail(employeeId, date);
  const rec = detail.data?.record;
  const ts = detail.data?.timesheet;
  const today = todayInOffice();
  const canNext = !!date && addDays(date, 1) <= today;

  useEffect(() => {
    if (!date || !onNavigate) return;
    const onKey = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === "INPUT" || t.tagName === "SELECT" || t.tagName === "TEXTAREA")) return;
      if (e.key === "ArrowLeft") onNavigate(addDays(date, -1));
      if (e.key === "ArrowRight" && addDays(date, 1) <= today) onNavigate(addDays(date, 1));
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [date, onNavigate, today]);

  const shift = detail.data?.shift ? { start: toMin(detail.data.shift.start_time), end: toMin(detail.data.shift.end_time) } : null;
  const lateAfter = shift ? shift.start + (detail.data?.late_grace_minutes ?? 0) : null;
  const inMin = minutesOfDay(rec?.first_in ?? ts?.first_in);
  const outMin = minutesOfDay(rec?.last_out ?? ts?.last_out);
  const working = rec && rec.required_minutes > 0;

  return (
    <Modal
      open={!!date}
      onClose={onClose}
      size="lg"
      title={date ? formatDate(date, { weekday: "long" }) : ""}
      description={rec ? headline(rec) : "What the imported timesheets say about this day"}
      footer={onNavigate && date ? (
        <div className="flex w-full items-center justify-between gap-2">
          <Button variant="ghost" size="sm" icon={<ChevronLeft className="size-4" />} onClick={() => onNavigate(addDays(date, -1))}>Previous day</Button>
          <span className="hidden text-xs text-ink-3 sm:inline">← → to move between days</span>
          <Button variant="ghost" size="sm" disabled={!canNext} onClick={() => onNavigate(addDays(date, 1))}>Next day <ChevronRight className="size-4" /></Button>
        </div>
      ) : undefined}
    >
      {detail.isError ? (
        <ErrorState error={detail.error} onRetry={() => detail.refetch()} />
      ) : detail.isLoading ? (
        <TableSkeleton rows={4} />
      ) : (
        <div className="space-y-4">
          {rec && (
            <div className="flex flex-wrap items-center gap-2">
              <StatusBadge status={rec.status} />
              {rec.exceptions.length > 0 && <ExceptionTags codes={rec.exceptions} />}
              {rec.holiday_name && rec.status !== "HOLIDAY" && <span className="text-xs text-ink-2">Holiday: {rec.holiday_name}</span>}
            </div>
          )}

          {rec && (rec.status === "PRESENT" || inMin !== null) && (
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
              <Tile icon={<LogIn className="size-3.5" />} label="Arrived" value={inMin !== null ? clockTime(inMin) : "—"}
                note={inMin !== null && shift ? (lateAfter !== null && inMin > lateAfter ? signedMinutes(inMin - shift.start, "after shift start") : inMin < shift.start ? signedMinutes(shift.start - inMin, "before shift start") : "On time") : undefined}
                tone={inMin !== null && lateAfter !== null ? (inMin > lateAfter ? "warn" : "good") : undefined} />
              <Tile icon={<LogOut className="size-3.5" />} label="Left" value={outMin !== null ? clockTime(outMin) : "—"}
                note={outMin !== null && shift ? (outMin < shift.end ? signedMinutes(shift.end - outMin, "before shift end") : signedMinutes(outMin - shift.end, "after shift end")) : undefined}
                tone={outMin !== null && shift ? (outMin < shift.end ? "warn" : "good") : undefined} />
              <Tile icon={<Timer className="size-3.5" />} label="Office hours"
                value={rec.hours_known ? formatMinutes(rec.net_minutes) : "Not recorded"}
                note={rec.hours_known && working ? `${duration(rec.net_minutes - rec.required_minutes)} ${rec.net_minutes < rec.required_minutes ? "short of" : "over"} the ${formatMinutes(rec.required_minutes)} shift` : working ? `Shift ${formatMinutes(rec.required_minutes)}` : undefined}
                tone={rec.hours_known && working ? (rec.net_minutes < rec.required_minutes ? "warn" : "good") : undefined} />
              <Tile icon={<TrendingUp className="size-3.5" />} label="Against your average"
                value={rec.hours_known && averageMinutes ? `${rec.net_minutes >= averageMinutes ? "+" : "−"}${duration(rec.net_minutes - averageMinutes)}` : "—"}
                note={averageMinutes ? `Your average: ${formatMinutes(averageMinutes)}` : "No average for this period"} />
            </div>
          )}

          {inMin !== null && <DayTimeline inMin={inMin} outMin={outMin} shift={shift} lateAfter={lateAfter} />}

          {date && <WeekAround employeeId={employeeId} date={date} onNavigate={onNavigate ?? (() => undefined)} />}

          {ts ? (
            <dl className="grid grid-cols-2 gap-3 rounded-lg border border-line p-4 sm:grid-cols-3">
              <Item label="Source">{ts.wfo_source ? SOURCE_LABEL[ts.wfo_source] : "—"}</Item>
              <Item label="File"><span className="inline-flex items-center gap-1"><FileText className="size-3.5 shrink-0 text-ink-3" aria-hidden /><span className="truncate">{ts.upload_filename ?? "—"}</span></span></Item>
              <Item label="Shift">{detail.data?.shift ? `${detail.data.shift.name} · ${clockTime(detail.data.shift.start_time)} – ${clockTime(detail.data.shift.end_time)}` : "—"}</Item>
              {ts.override_work_mode && (
                <div className="col-span-full text-xs text-ink-2">
                  HR set this day to <b>{ts.override_work_mode === "OFFICE" ? "Office" : ts.override_work_mode}</b>
                  {ts.overridden_by_name && <> ({ts.overridden_by_name}{ts.overridden_at && `, ${formatDateTime(ts.overridden_at)}`})</>}
                  {ts.override_note && <>: “{ts.override_note}”</>}
                </div>
              )}
              {ts.conflicts.length > 0 && <div className="col-span-full text-xs text-ink-2">Conflicting signals: {ts.conflicts.join("; ")}</div>}
              {rec?.exceptions.includes("LATE_ARRIVAL") && lateAfter !== null && (
                <div className="col-span-full flex items-center gap-1.5 text-xs text-ink-2">
                  <AlarmClock className="size-3.5" style={{ color: "var(--serious)" }} aria-hidden />
                  Late arrival: in after {clockTime(lateAfter)} (shift start plus {detail.data?.late_grace_minutes ?? 0} minutes' grace)
                </div>
              )}
            </dl>
          ) : (
            rec?.status !== "WEEKEND" && rec?.status !== "HOLIDAY" && (
              <EmptyState
                title="Not in any timesheet"
                description={rec?.status === "NO_TIMESHEET" ? "No timesheet covering this day has been imported yet." : "The imported timesheets have no entry for this person on this day."}
              />
            )
          )}
        </div>
      )}
    </Modal>
  );
}
