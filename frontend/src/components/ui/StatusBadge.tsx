import { clsx } from "clsx";
import {
  AlertTriangle,
  CalendarOff,
  CheckCircle2,
  CircleDashed,
  Clock,
  MinusCircle,
  Palmtree,
  PartyPopper,
  XCircle,
  type LucideIcon,
} from "lucide-react";

import { exceptionLabel, STATUS_LABEL, titleCase } from "@/lib/format";
import type { DayStatus, RequestStatus } from "@/lib/types";

/** Colour token, icon and label for each day status: colour is never the only signal. */
export const STATUS_META: Record<DayStatus, { color: string; icon: LucideIcon }> = {
  PRESENT: { color: "var(--st-present)", icon: CheckCircle2 },
  ABSENT: { color: "var(--st-absent)", icon: XCircle },
  ON_LEAVE: { color: "var(--st-leave)", icon: Palmtree },
  HOLIDAY: { color: "var(--st-holiday)", icon: PartyPopper },
  WEEKEND: { color: "var(--st-off)", icon: CalendarOff },
  INCOMPLETE: { color: "var(--st-incomplete)", icon: AlertTriangle },
  NOT_YET_CHECKED_IN: { color: "var(--st-pending)", icon: CircleDashed },
  NOT_SCHEDULED: { color: "var(--st-off)", icon: MinusCircle },
};

export function StatusBadge({ status, live, className }: { status: DayStatus | null; live?: boolean; className?: string }) {
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
      {live && <span className="text-ink-3">· in office</span>}
    </span>
  );
}

const REQUEST_META: Record<RequestStatus, { color: string; icon: LucideIcon }> = {
  PENDING: { color: "var(--st-incomplete)", icon: Clock },
  APPROVED: { color: "var(--good)", icon: CheckCircle2 },
  REJECTED: { color: "var(--critical)", icon: XCircle },
  CANCELLED: { color: "var(--st-pending)", icon: MinusCircle },
};

export function RequestBadge({ status }: { status: RequestStatus }) {
  const { color, icon: Icon } = REQUEST_META[status];
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full border border-line bg-surface-2 px-2 py-0.5 text-xs font-medium text-ink">
      <Icon className="size-3.5" style={{ color }} aria-hidden />
      {titleCase(status)}
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
