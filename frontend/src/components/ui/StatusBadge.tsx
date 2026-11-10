import { clsx } from "clsx";
import {
  AlertTriangle,
  CalendarOff,
  CheckCircle2,
  CircleDashed,
  Home,
  MinusCircle,
  PartyPopper,
  Plane,
  XCircle,
  type LucideIcon,
} from "lucide-react";

import { exceptionLabel, STATUS_LABEL } from "@/lib/format";
import type { DayStatus } from "@/lib/types";

/** Colour token, icon and label for each day status: colour is never the only signal. WFH and leave use the
 *  same colours as the office-days (WFO) pages. */
export const STATUS_META: Record<DayStatus, { color: string; icon: LucideIcon }> = {
  PRESENT: { color: "var(--st-present)", icon: CheckCircle2 },
  WFH: { color: "var(--st-wfh)", icon: Home },
  ABSENT: { color: "var(--st-absent)", icon: XCircle },
  LEAVE: { color: "var(--st-leave)", icon: Plane },
  HOLIDAY: { color: "var(--st-holiday)", icon: PartyPopper },
  WEEKEND: { color: "var(--st-off)", icon: CalendarOff },
  NO_TIMESHEET: { color: "var(--st-pending)", icon: CircleDashed },
  NOT_SCHEDULED: { color: "var(--st-off)", icon: MinusCircle },
};

export function StatusBadge({ status, className }: { status: DayStatus | null; className?: string }) {
  if (!status) return <span className="text-xs text-ink-3">—</span>;
  const { color, icon: Icon } = STATUS_META[status];
  return (
    <span
      className={clsx(
        "inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border border-line bg-surface-2 px-2 py-0.5 text-xs font-medium text-ink",
        className,
      )}
    >
      <Icon className="size-3.5" style={{ color }} aria-hidden />
      {STATUS_LABEL[status]}
    </span>
  );
}

export function ExceptionTags({ codes }: { codes: string[] }) {
  if (!codes.length) return <span className="text-xs text-ink-3">—</span>;
  return (
    <span className="flex flex-wrap gap-1">
      {codes.map((c) => (
        <span
          key={c}
          className="inline-flex items-center gap-1 rounded border border-line px-1.5 py-0.5 text-[11px] font-medium text-ink-2"
        >
          <AlertTriangle className="size-3" style={{ color: "var(--serious)" }} aria-hidden />
          {exceptionLabel(c)}
        </span>
      ))}
    </span>
  );
}
