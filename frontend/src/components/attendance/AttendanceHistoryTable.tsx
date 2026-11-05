import { useState } from "react";

import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, Pagination, type Column } from "@/components/ui/DataTable";
import { Checkbox, Select } from "@/components/ui/Fields";
import { ExceptionTags, StatusBadge } from "@/components/ui/StatusBadge";
import { formatDate, formatMinutes, formatTime, STATUS_LABEL, titleCase } from "@/lib/format";
import { useEmployeeDays } from "@/lib/queries";
import type { DayRecord, DayStatus } from "@/lib/types";

const COLUMNS: Column<DayRecord>[] = [
  { key: "date", header: "Date", cell: (r) => <span className="whitespace-nowrap">{formatDate(r.date, { weekday: "short" })}</span> },
  { key: "in", header: "First check-in", cell: (r) => <span className="tabular">{formatTime(r.first_check_in)}</span> },
  { key: "out", header: "Last check-out", cell: (r) => <span className="tabular">{formatTime(r.last_check_out)}</span> },
  { key: "net", header: "Net hours", align: "right", cell: (r) => formatMinutes(r.net_minutes) },
  { key: "status", header: "Status", cell: (r) => <StatusBadge status={r.status} live={r.has_active_session} /> },
  {
    key: "notes",
    header: "Exceptions / notes",
    cell: (r) => (
      <div className="space-y-1">
        <ExceptionTags codes={r.exceptions} />
        {(r.holiday_name || r.leave_type || r.session_count > 1) && (
          <div className="text-[11px] text-ink-3">
            {[r.holiday_name, r.leave_type && titleCase(r.leave_type) + " leave", r.session_count > 1 && r.session_count + " sessions"].filter(Boolean).join(" · ")}
          </div>
        )}
      </div>
    ),
  },
];

export function AttendanceHistoryTable({ employeeId, start, end, onSelectDay, actions }: {
  employeeId: number;
  start: string;
  end: string;
  onSelectDay: (date: string) => void;
  actions?: React.ReactNode;
}) {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<DayStatus | "">("");
  const [exceptionsOnly, setExceptionsOnly] = useState(false);
  const pageSize = 10;
  const q = useEmployeeDays(employeeId, {
    start,
    end,
    page,
    page_size: pageSize,
    status: status || undefined,
    exceptions_only: exceptionsOnly || undefined,
  });
  return (
    <Card>
      <CardHeader
        title="Attendance history"
        subtitle={formatDate(start) + " – " + formatDate(end)}
        actions={
          <>
            <label className="sr-only" htmlFor="hist-status">
              Filter by status
            </label>
            <Select
              id="hist-status"
              className="w-40"
              value={status}
              onChange={(e) => {
                setStatus(e.target.value as DayStatus | "");
                setPage(1);
              }}
            >
              <option value="">All statuses</option>
              {(Object.keys(STATUS_LABEL) as DayStatus[]).map((s) => (
                <option key={s} value={s}>
                  {STATUS_LABEL[s]}
                </option>
              ))}
            </Select>
            <Checkbox
              label="Exceptions only"
              checked={exceptionsOnly}
              onChange={(e) => {
                setExceptionsOnly(e.target.checked);
                setPage(1);
              }}
            />
            {actions}
          </>
        }
      />
      <DataTable
        caption="Daily attendance records"
        columns={COLUMNS}
        rows={q.data?.items}
        rowKey={(r) => r.date}
        loading={q.isFetching}
        error={q.error}
        onRetry={() => q.refetch()}
        onRowClick={(r) => onSelectDay(r.date)}
      />
      {q.data && <Pagination page={page} pageSize={pageSize} total={q.data.total} onPage={setPage} />}
    </Card>
  );
}
