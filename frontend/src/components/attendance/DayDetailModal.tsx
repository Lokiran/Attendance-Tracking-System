import { Button } from "@/components/ui/Button";
import { Modal } from "@/components/ui/Modal";
import { TableSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { ExceptionTags, StatusBadge } from "@/components/ui/StatusBadge";
import { formatDate, formatMinutes, formatTime, titleCase } from "@/lib/format";
import { useDayDetail } from "@/lib/queries";
import type { Session } from "@/lib/types";

function SessionRow({ s }: { s: Session }) {
  return (
    <tr className="border-b border-line last:border-0">
      <td className="tabular py-2 pr-3">{formatTime(s.check_in_at)}</td>
      <td className="tabular py-2 pr-3">{s.check_out_at ? formatTime(s.check_out_at) : "—"}</td>
      <td className="tabular py-2 pr-3">{s.break_minutes ? s.break_minutes + "m" : "—"}</td>
      <td className="tabular py-2 pr-3">{formatMinutes(s.net_minutes)}</td>
      <td className="py-2 pr-3 text-xs text-ink-2">
        {titleCase(s.status)}
        {s.source !== "WEB" && " · " + titleCase(s.source)}
      </td>
    </tr>
  );
}

export function DayDetailModal({ employeeId, date, onClose, onRequestCorrection }: {
  employeeId: number;
  date: string | null;
  onClose: () => void;
  /** Provided only when the viewer is the employee: corrections are self-service requests. */
  onRequestCorrection?: (date: string, sessionId: number | null) => void;
}) {
  const detail = useDayDetail(employeeId, date);
  const rec = detail.data?.record;
  return (
    <Modal
      open={!!date}
      onClose={onClose}
      size="lg"
      title={date ? formatDate(date, { weekday: "long" }) : ""}
      description="Sessions recorded for this workday (office time)"
      footer={
        onRequestCorrection && date ? (
          <Button variant="secondary" onClick={() => onRequestCorrection(date, null)}>
            Request a correction for this day
          </Button>
        ) : undefined
      }
    >
      {detail.isError ? (
        <ErrorState error={detail.error} onRetry={() => detail.refetch()} />
      ) : detail.isLoading ? (
        <TableSkeleton rows={3} />
      ) : (
        <div className="space-y-4">
          {rec && (
            <dl className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <div>
                <dt className="text-xs text-ink-3">Status</dt>
                <dd className="mt-1">
                  <StatusBadge status={rec.status} />
                </dd>
              </div>
              <div>
                <dt className="text-xs text-ink-3">Net time</dt>
                <dd className="tabular mt-1 text-sm font-medium">{formatMinutes(rec.net_minutes)}</dd>
              </div>
              <div>
                <dt className="text-xs text-ink-3">Required</dt>
                <dd className="tabular mt-1 text-sm font-medium">{formatMinutes(rec.required_minutes)}</dd>
              </div>
              <div>
                <dt className="text-xs text-ink-3">Exceptions</dt>
                <dd className="mt-1">
                  <ExceptionTags codes={rec.exceptions} />
                </dd>
              </div>
              {rec.holiday_name && <div className="col-span-2 text-xs text-ink-2">Holiday: {rec.holiday_name}</div>}
            </dl>
          )}
          {detail.data?.sessions.length ? (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[480px] text-sm">
                <thead>
                  <tr className="border-b border-line text-left text-xs text-ink-3">
                    <th className="py-2 pr-3 font-medium">Check-in</th>
                    <th className="py-2 pr-3 font-medium">Check-out</th>
                    <th className="py-2 pr-3 font-medium">Break</th>
                    <th className="py-2 pr-3 font-medium">Net</th>
                    <th className="py-2 pr-3 font-medium">State</th>
                  </tr>
                </thead>
                <tbody>
                  {detail.data.sessions.map((s) => (
                    <SessionRow key={s.id} s={s} />
                  ))}
                </tbody>
              </table>
              {onRequestCorrection && date && (
                <div className="mt-2 flex flex-wrap gap-2">
                  {detail.data.sessions
                    .filter((s) => s.status !== "OPEN" || rec?.exceptions.includes("MISSING_CHECKOUT"))
                    .map((s) => (
                      <Button key={s.id} size="sm" variant="ghost" onClick={() => onRequestCorrection(date, s.id)}>
                        Fix session from {formatTime(s.check_in_at)}
                      </Button>
                    ))}
                </div>
              )}
            </div>
          ) : (
            <EmptyState title="No sessions recorded" description="No check-in was recorded on this day." />
          )}
          {!!detail.data?.voided_sessions.length && (
            <details className="rounded-lg border border-line p-3 text-sm">
              <summary className="cursor-pointer text-xs font-medium text-ink-2">
                Superseded sessions ({detail.data.voided_sessions.length}) — kept for audit history
              </summary>
              <table className="mt-2 w-full text-sm">
                <tbody>
                  {detail.data.voided_sessions.map((s) => (
                    <SessionRow key={s.id} s={s} />
                  ))}
                </tbody>
              </table>
            </details>
          )}
        </div>
      )}
    </Modal>
  );
}
