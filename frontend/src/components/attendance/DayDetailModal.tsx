import { Modal } from "@/components/ui/Modal";
import { TableSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { ExceptionTags, StatusBadge } from "@/components/ui/StatusBadge";
import { formatDate, formatDateTime, formatMinutes, formatTime } from "@/lib/format";
import { useDayDetail } from "@/lib/queries";
import type { WfoSource } from "@/lib/types";

const SOURCE_LABEL: Record<WfoSource, string> = {
  MANUAL_OVERRIDE: "Set by HR (override)",
  UPLOAD_COLUMN: "Imported timesheet",
  BADGE: "Badge / swipe rows of an imported file",
  WIFI: "Office Wi-Fi",
  CHECK_IN: "Check-in (retired)",
  INFERRED: "Hours without a work mode in the timesheet",
  CALENDAR: "Calendar (holiday / weekly off)",
};

function Item({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <dt className="text-xs text-ink-3">{label}</dt>
      <dd className="mt-1 text-sm font-medium text-ink">{children}</dd>
    </div>
  );
}

/** One day: its status and the timesheet entry (or HR override) behind it. */
export function DayDetailModal({ employeeId, date, onClose }: { employeeId: number; date: string | null; onClose: () => void }) {
  const detail = useDayDetail(employeeId, date);
  const rec = detail.data?.record;
  const ts = detail.data?.timesheet;
  return (
    <Modal open={!!date} onClose={onClose} size="lg" title={date ? formatDate(date, { weekday: "long" }) : ""}
      description="What the imported timesheets say about this day">
      {detail.isError ? (
        <ErrorState error={detail.error} onRetry={() => detail.refetch()} />
      ) : detail.isLoading ? (
        <TableSkeleton rows={3} />
      ) : (
        <div className="space-y-4">
          {rec && (
            <dl className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <Item label="Status"><StatusBadge status={rec.status} /></Item>
              <Item label="Office hours"><span className="tabular">{rec.hours_known ? formatMinutes(rec.net_minutes) : rec.status === "PRESENT" ? "Not recorded" : "—"}</span></Item>
              <Item label="Shift hours"><span className="tabular">{formatMinutes(rec.required_minutes)}</span></Item>
              <Item label="Exceptions"><ExceptionTags codes={rec.exceptions} /></Item>
              {rec.holiday_name && <div className="col-span-2 text-xs text-ink-2">Holiday: {rec.holiday_name}</div>}
            </dl>
          )}
          {ts ? (
            <dl className="grid grid-cols-2 gap-3 rounded-lg border border-line p-4 sm:grid-cols-3">
              <Item label="Source">{ts.wfo_source ? SOURCE_LABEL[ts.wfo_source] : "—"}</Item>
              <Item label="File">{ts.upload_filename ?? "—"}</Item>
              <Item label="In / out">
                {ts.first_in ? <span className="tabular">{formatTime(ts.first_in)} – {formatTime(ts.last_out)}</span> : <span className="text-ink-3">Not in the file</span>}
              </Item>
              {ts.override_work_mode && (
                <div className="col-span-full text-xs text-ink-2">
                  HR set this day to <b>{ts.override_work_mode === "OFFICE" ? "Office" : ts.override_work_mode}</b>
                  {ts.overridden_by_name && <> ({ts.overridden_by_name}{ts.overridden_at && `, ${formatDateTime(ts.overridden_at)}`})</>}
                  {ts.override_note && <>: “{ts.override_note}”</>}
                </div>
              )}
              {ts.conflicts.length > 0 && <div className="col-span-full text-xs text-ink-2">Conflicting signals: {ts.conflicts.join("; ")}</div>}
            </dl>
          ) : (
            <EmptyState
              title="Not in any timesheet"
              description={rec?.status === "NO_TIMESHEET" ? "No timesheet covering this day has been imported yet." : "The imported timesheets have no entry for this person on this day."}
            />
          )}
        </div>
      )}
    </Modal>
  );
}
