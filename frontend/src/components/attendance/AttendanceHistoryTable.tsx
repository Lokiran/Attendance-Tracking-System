import { clsx } from "clsx";
import { useState } from "react";

import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, Pagination, type Column } from "@/components/ui/DataTable";
import { Checkbox, Select } from "@/components/ui/Fields";
import { ExceptionTags, StatusBadge } from "@/components/ui/StatusBadge";
import { formatDate, formatMinutes, formatTime, STATUS_LABEL } from "@/lib/format";
import { useEmployeeDays } from "@/lib/queries";
import type { DayRecord, DayStatus } from "@/lib/types";

const COLUMNS: Column<DayRecord>[] = [
  { key: "date", header: "Date", cell: (r) => <span className="whitespace-nowrap">{formatDate(r.date, { weekday: "short" })}</span> },
  { key: "in", header: "In", cell: (r) => <span className="tabular">{formatTime(r.first_in)}</span> },
  { key: "out", header: "Out", cell: (r) => <span className="tabular">{formatTime(r.last_out)}</span> },
  {
    key: "net",
    header: "Office hours",
    align: "right",
    cell: (r) => (r.hours_known ? formatMinutes(r.net_minutes) : r.status === "PRESENT" ? <span className="text-xs text-ink-3">Not recorded</span> : "—"),
  },
  { key: "status", header: "Status", cell: (r) => <StatusBadge status={r.status} /> },
  {
    key: "notes",
    header: "Exceptions / notes",
    cell: (r) => (
      <div className="space-y-1">
        <ExceptionTags codes={r.exceptions} />
        {r.holiday_name && <div className="text-[11px] text-ink-3">{r.holiday_name}</div>}
      </div>
    ),
  },
];

export function AttendanceHistoryTable({ employeeId, start, end, onSelectDay, actions, filter, onFilter, id, className }: {
  employeeId: number;
  start: string;
  end: string;
  onSelectDay: (date: string) => void;
  actions?: React.ReactNode;
  /** Controlled filters (the dashboard's KPI counts set them); uncontrolled when left out. */
  filter?: { status: DayStatus | ""; exceptions: boolean };
  onFilter?: (f: { status: DayStatus | ""; exceptions: boolean }) => void;
  id?: string;
  className?: string;
}) {
  const [page, setPage] = useState(1);
  const [ownStatus, setOwnStatus] = useState<DayStatus | "">("");
  const [ownExceptions, setOwnExceptions] = useState(false);
  const status = filter ? filter.status : ownStatus;
  const exceptionsOnly = filter ? filter.exceptions : ownExceptions;
  const setStatus = (s: DayStatus | "") => (onFilter ? onFilter({ status: s, exceptions: exceptionsOnly }) : setOwnStatus(s));
  const setExceptionsOnly = (v: boolean) => (onFilter ? onFilter({ status, exceptions: v }) : setOwnExceptions(v));
  const [lastFilter, setLastFilter] = useState(`${status}|${exceptionsOnly}`);
  if (lastFilter !== `${status}|${exceptionsOnly}`) {
    setLastFilter(`${status}|${exceptionsOnly}`);
    setPage(1); // a filter set from outside starts again on page 1
  }
  const pageSize = 10;
  const q = useEmployeeDays(employeeId, {
    start,
    end,
    page,
    page_size: pageSize,
    status: status || undefined,
    exceptions_only: exceptionsOnly || undefined,
  });
  const filtered = status || exceptionsOnly;
  return (
    <Card id={id} className={clsx("scroll-mt-24", className)}>
      <CardHeader
        title="Attendance history"
        subtitle={<>
          {formatDate(start) + " – " + formatDate(end)}
          {filtered && q.data && <> · showing {q.data.total} {status ? STATUS_LABEL[status].toLowerCase() : ""} day{q.data.total === 1 ? "" : "s"}{exceptionsOnly ? " with exceptions" : ""}</>}
          {filtered && (
            <button type="button" className="ml-2 font-medium text-brand-text hover:underline" onClick={() => onFilter ? onFilter({ status: "", exceptions: false }) : (setOwnStatus(""), setOwnExceptions(false))}>
              Clear filter
            </button>
          )}
        </>}
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
