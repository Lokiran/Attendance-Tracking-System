import { AlertTriangle, CalendarX2, Clock3, Gauge, Palmtree, UserCheck, Users } from "lucide-react";
import { useState, type ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";

import { AttendanceTrendChart, EXCEPTION_SERIES, ExceptionTrendChart, RankedBarChart, TEAM_STACK, TeamDailyChart } from "@/components/charts/Charts";
import { ChartCard, Legend } from "@/components/charts/ChartCard";
import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, Pagination, type Column, type SortState } from "@/components/ui/DataTable";
import { DateRangeFilter } from "@/components/ui/DateRangeFilter";
import { Input, SearchInput, Select } from "@/components/ui/Fields";
import { ExportButton, LastUpdated, Tabs } from "@/components/ui/Misc";
import { PageHeader } from "@/components/ui/PageHeader";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { ExceptionTags, StatusBadge } from "@/components/ui/StatusBadge";
import { CardGrid, SummaryCard } from "@/components/ui/SummaryCard";
import { useRangeParams, withRange } from "@/hooks/useUrlState";
import { minDate, todayInOffice } from "@/lib/dates";
import { exceptionLabel, formatDate, formatMinutes, formatPercent, formatTime, hours, STATUS_LABEL } from "@/lib/format";
import { useEmployeeRows, useExceptions, useOverview, useTeamTrends, type ScopeParams } from "@/lib/queries";
import type { DayStatus, EmployeeRow, ExceptionRow } from "@/lib/types";

const CARD_FILTER: Record<string, DayStatus> = { present: "PRESENT", absent: "ABSENT", leave: "ON_LEAVE", incomplete: "INCOMPLETE", pending: "NOT_YET_CHECKED_IN" };

/**
 * Team analytics for a scope: a manager's own subtree (no scope), another manager's subtree
 * (manager_id) or a department (department_id). The backend restricts every query to what the
 * signed-in user may see.
 */
export function TeamDashboardView({ scope, title, description, breadcrumb, extra }: {
  scope: ScopeParams;
  title: string;
  description?: ReactNode;
  breadcrumb?: ReactNode;
  extra?: (ctx: { start: string; end: string; onDate: string }) => ReactNode;
}) {
  const navigate = useNavigate();
  const { start, end, valid, setRange, get, set } = useRangeParams();
  const today = todayInOffice();
  const onDate = get("on_date", minDate(end, today));
  const status = get("status") as DayStatus | "";
  const search = get("q");
  const page = Number(get("page", "1")) || 1;
  const sort: SortState = { sort: get("sort", "name"), order: (get("order", "asc") as "asc" | "desc") };
  const [granularity, setGranularity] = useState<"week" | "month">("week");
  const [excType, setExcType] = useState("");
  const [excPage, setExcPage] = useState(1);

  const base = { ...scope, start, end };
  const overview = useOverview({ ...base, on_date: onDate });
  const rows = useEmployeeRows({ ...base, on_date: onDate, search, status, sort: sort.sort, order: sort.order, page, page_size: 10 });
  const all = useEmployeeRows({ ...base, on_date: onDate, sort: "attendance_percentage", order: "desc", page_size: 200 });
  const daily = useTeamTrends({ ...base, granularity: "day" });
  const trend = useTeamTrends({ ...base, granularity });
  const exceptions = useExceptions({ ...base, exception_type: excType, page: excPage, page_size: 8 });
  const o = overview.data;
  const snap = o?.snapshot;
  const isToday = onDate === today;

  const employeeLink = (id: number) => withRange("/people/" + id, start, end);
  const statusCard = (key: keyof typeof CARD_FILTER) => ({
    active: status === CARD_FILTER[key],
    onClick: () => set({ status: status === CARD_FILTER[key] ? null : CARD_FILTER[key] }),
  });

  const columns: Column<EmployeeRow>[] = [
    {
      key: "name",
      header: "Employee",
      sortable: true,
      cell: (r) => (
        <Link to={employeeLink(r.employee_id)} className="font-medium text-ink hover:text-brand-text hover:underline" onClick={(e) => e.stopPropagation()}>
          {r.full_name}
          {r.job_title && <span className="block text-xs font-normal text-ink-3">{r.job_title}</span>}
        </Link>
      ),
    },
    { key: "code", header: "Employee ID", sortable: true, cell: (r) => <span className="tabular">{r.employee_code}</span> },
    { key: "department", header: "Department", sortable: true, cell: (r) => r.department_name ?? "—" },
    { key: "check_in", header: "Check-in", sortable: true, cell: (r) => <span className="tabular">{formatTime(r.day.first_check_in)}</span> },
    { key: "out", header: "Check-out", cell: (r) => <span className="tabular">{r.day.has_active_session ? "In office" : formatTime(r.day.last_check_out)}</span> },
    { key: "net_minutes", header: "Net (period)", sortable: true, align: "right", cell: (r) => formatMinutes(r.period.net_minutes) },
    { key: "attendance_percentage", header: "Attendance", sortable: true, align: "right", cell: (r) => formatPercent(r.period.attendance_percentage) },
    { key: "status", header: "Status", sortable: true, cell: (r) => <StatusBadge status={r.day.status} live={r.day.has_active_session} /> },
    { key: "exceptions", header: "Exceptions", sortable: true, cell: (r) => (r.exception_count ? <span className="text-xs text-ink-2">{r.exception_count} in period</span> : <span className="text-xs text-ink-3">—</span>) },
    { key: "actions", header: "", cell: (r) => <Link to={employeeLink(r.employee_id)} className="text-xs font-medium text-brand-text hover:underline" onClick={(e) => e.stopPropagation()}>View</Link> },
  ];

  const excColumns: Column<ExceptionRow>[] = [
    { key: "date", header: "Date", cell: (r) => formatDate(r.date, { weekday: "short" }) },
    { key: "emp", header: "Employee", cell: (r) => <Link className="font-medium hover:underline" to={withRange("/people/" + r.employee_id, start, end, { day: r.date })}>{r.full_name}</Link> },
    { key: "status", header: "Status", cell: (r) => <StatusBadge status={r.status} /> },
    { key: "exc", header: "Exceptions", cell: (r) => <ExceptionTags codes={r.exceptions} /> },
    { key: "in", header: "In / out", cell: (r) => <span className="tabular text-xs">{formatTime(r.first_check_in)} – {formatTime(r.last_check_out)}</span> },
    { key: "net", header: "Net", align: "right", cell: (r) => formatMinutes(r.net_minutes) },
  ];

  const ranked = all.data?.items ?? [];
  const exportParams = { ...scope, start, end };

  return (
    <div className="space-y-5">
      <PageHeader
        title={title}
        description={description}
        breadcrumb={breadcrumb}
        actions={<LastUpdated updatedAt={overview.dataUpdatedAt || undefined} fetching={overview.isFetching} onRefresh={() => { overview.refetch(); rows.refetch(); all.refetch(); daily.refetch(); trend.refetch(); exceptions.refetch(); }} />}
      />
      <Card className="flex flex-wrap items-center gap-3 px-4 py-3">
        <DateRangeFilter start={start} end={end} onChange={setRange} />
        <div className="flex items-center gap-2">
          <label htmlFor="on-date" className="text-xs text-ink-3">Status on</label>
          <Input id="on-date" type="date" className="w-40" value={onDate} max={today} onChange={(e) => e.target.value && set({ on_date: e.target.value === today ? null : e.target.value })} />
        </div>
      </Card>

      {overview.isError ? (
        <Card><ErrorState error={overview.error} onRetry={() => overview.refetch()} /></Card>
      ) : (
        <CardGrid cols={7}>
          <SummaryCard label="Team size" icon={<Users className="size-4" />} loading={!o} value={o?.headcount} hint="active, tracked people" />
          <SummaryCard label={isToday ? "Present today" : "Present"} icon={<UserCheck className="size-4" />} loading={!o} value={snap?.present} hint={snap && `${snap.checked_in_now} in office now`} {...statusCard("present")} />
          <SummaryCard label={isToday ? "Absent today" : "Absent"} icon={<CalendarX2 className="size-4" />} loading={!o} value={snap && snap.absent + snap.not_checked_in} hint={snap && (snap.not_checked_in ? `${snap.not_checked_in} not checked in yet` : `${snap.off} off / not scheduled`)} {...statusCard(isToday && snap?.not_checked_in ? "pending" : "absent")} />
          <SummaryCard label="On approved leave" icon={<Palmtree className="size-4" />} loading={!o} value={snap?.on_leave} {...statusCard("leave")} />
          <SummaryCard label="Incomplete" icon={<AlertTriangle className="size-4" />} loading={!o} value={snap?.incomplete} hint={o && `${o.period.unresolved_sessions} unresolved session(s) in period`} {...statusCard("incomplete")} />
          <SummaryCard label="Avg net / day" icon={<Clock3 className="size-4" />} loading={!o} value={formatMinutes(o?.period.average_net_minutes)} hint="per attended day in period" />
          <SummaryCard label="Team attendance" icon={<Gauge className="size-4" />} loading={!o} value={formatPercent(o?.period.attendance_percentage)} hint={o && <Link className="hover:underline" to="/approvals">{o.pending_corrections} correction(s), {o.pending_leave} leave pending</Link>} />
        </CardGrid>
      )}
      {!valid && <p role="alert" className="text-sm text-critical-text">Choose an end date on or after the start date.</p>}

      {extra?.({ start, end, onDate })}

      <div className="grid gap-5 xl:grid-cols-2">
        <ChartCard
          title="Daily team attendance"
          subtitle="People per status each day · click a day to inspect it"
          loading={daily.isLoading}
          error={daily.error}
          onRetry={() => daily.refetch()}
          isEmpty={!daily.data?.items.some((b) => b.expected || b.present)}
          legend={<Legend items={TEAM_STACK.map((s) => ({ label: s.label, color: s.color }))} />}
          table={{ columns: ["Date", ...TEAM_STACK.map((s) => s.label)], rows: (daily.data?.items ?? []).map((b) => [b.start, b.present, b.on_leave, b.incomplete, b.absent]) }}
        >
          <TeamDailyChart items={daily.data?.items ?? []} onSelectDate={(d) => set({ on_date: d === today ? null : d })} />
        </ChartCard>
        <ChartCard
          title={granularity === "week" ? "Weekly attendance trend" : "Monthly attendance trend"}
          subtitle="Attendance % of expected days"
          actions={<Tabs label="Trend granularity" value={granularity} onChange={setGranularity} options={[{ value: "week", label: "Weekly" }, { value: "month", label: "Monthly" }]} />}
          loading={trend.isLoading}
          error={trend.error}
          isEmpty={!trend.data?.items.some((b) => b.expected)}
          table={{ columns: ["Period", "Attendance", "Present/expected", "Avg net/day"], rows: (trend.data?.items ?? []).map((b) => [b.label, formatPercent(b.attendance_percentage), `${b.present_expected}/${b.expected}`, formatMinutes(b.average_net_minutes)]) }}
        >
          <AttendanceTrendChart items={trend.data?.items ?? []} onSelect={(b) => setRange({ start: b.start, end: minDate(b.end, today) })} />
        </ChartCard>
        <ChartCard
          title="Employee-wise office hours"
          subtitle="Average net hours per attended day · click to open"
          loading={all.isLoading}
          error={all.error}
          isEmpty={!ranked.some((r) => r.period.average_net_minutes)}
          height={Math.max(200, ranked.length * 30)}
          table={{ columns: ["Employee", "Avg net/day", "Net total"], rows: ranked.map((r) => [r.full_name, formatMinutes(r.period.average_net_minutes), formatMinutes(r.period.net_minutes)]) }}
        >
          <RankedBarChart
            unit="h"
            data={[...ranked].sort((a, b) => (b.period.average_net_minutes ?? 0) - (a.period.average_net_minutes ?? 0)).map((r) => ({ id: r.employee_id, label: r.full_name, value: hours(r.period.average_net_minutes, 2) }))}
            onSelect={(id) => navigate(employeeLink(Number(id)))}
          />
        </ChartCard>
        <ChartCard
          title="Attendance percentage comparison"
          subtitle="Share of expected days attended · click to open"
          loading={all.isLoading}
          error={all.error}
          isEmpty={!ranked.some((r) => r.period.attendance_percentage !== null)}
          height={Math.max(200, ranked.length * 30)}
          table={{ columns: ["Employee", "Attendance", "Present/expected"], rows: ranked.map((r) => [r.full_name, formatPercent(r.period.attendance_percentage), `${r.period.present_expected_days}/${r.period.expected_days}`]) }}
        >
          <RankedBarChart unit="%" max={100} data={ranked.map((r) => ({ id: r.employee_id, label: r.full_name, value: r.period.attendance_percentage }))} onSelect={(id) => navigate(employeeLink(Number(id)))} />
        </ChartCard>
      </div>

      <Card>
        <CardHeader
          title={"Team attendance · " + formatDate(onDate, { weekday: "short" })}
          subtitle={"Status, check-in and check-out on the selected date; hours and attendance over " + formatDate(start) + " – " + formatDate(end)}
          actions={
            <>
              <SearchInput value={search} onChange={(v) => set({ q: v })} placeholder="Name or employee ID" label="Search employees" />
              <label htmlFor="team-status" className="sr-only">Filter by status</label>
              <Select id="team-status" className="w-44" value={status} onChange={(e) => set({ status: e.target.value })}>
                <option value="">All statuses</option>
                {(Object.keys(STATUS_LABEL) as DayStatus[]).map((s) => (
                  <option key={s} value={s}>{STATUS_LABEL[s]}</option>
                ))}
              </Select>
              <ExportButton path="/reports/team-attendance.csv" params={exportParams} />
            </>
          }
        />
        <DataTable
          caption="Team attendance"
          columns={columns}
          rows={rows.data?.items}
          rowKey={(r) => r.employee_id}
          loading={rows.isFetching}
          error={rows.error}
          onRetry={() => rows.refetch()}
          sort={sort}
          onSort={(s) => set({ sort: s.sort, order: s.order })}
          onRowClick={(r) => navigate(employeeLink(r.employee_id))}
          empty={<EmptyState title={search || status ? "No employees match these filters" : "No team members in this scope"} description={search || status ? "Clear the search or status filter." : "People who report to this manager will appear here."} />}
        />
        {rows.data && <Pagination page={page} pageSize={10} total={rows.data.total} onPage={(p) => set({ page: p }, false)} />}
      </Card>

      <div className="grid gap-5 xl:grid-cols-3">
        <ChartCard
          title="Attendance exceptions"
          subtitle="Weekly count by type"
          loading={trend.isLoading}
          isEmpty={!trend.data?.items.some((b) => b.late || b.missing_checkout || b.short_hours)}
          legend={<Legend items={EXCEPTION_SERIES.map((s) => ({ label: s.label, color: s.color }))} />}
          table={{ columns: ["Period", ...EXCEPTION_SERIES.map((s) => s.label)], rows: (trend.data?.items ?? []).map((b) => [b.label, b.missing_checkout, b.late, b.short_hours]) }}
        >
          <ExceptionTrendChart items={trend.data?.items ?? []} />
        </ChartCard>
        <Card className="xl:col-span-2">
          <CardHeader
            title="Exceptions & missing check-outs"
            subtitle={exceptions.data ? Object.entries(exceptions.data.counts).map(([k, v]) => `${exceptionLabel(k)}: ${v}`).join(" · ") || "None in period" : undefined}
            actions={
              <>
                <label htmlFor="exc-type" className="sr-only">Exception type</label>
                <Select id="exc-type" className="w-44" value={excType} onChange={(e) => { setExcType(e.target.value); setExcPage(1); }}>
                  <option value="">All exceptions</option>
                  {["MISSING_CHECKOUT", "LATE_ARRIVAL", "SHORT_HOURS", "ABSENT", "BELOW_MINIMUM"].map((x) => (
                    <option key={x} value={x}>{exceptionLabel(x)}</option>
                  ))}
                </Select>
                <ExportButton path="/reports/exceptions.csv" params={exportParams} size="sm" />
              </>
            }
          />
          <DataTable caption="Attendance exceptions" columns={excColumns} rows={exceptions.data?.items} rowKey={(r) => r.employee_id + r.date} loading={exceptions.isFetching} error={exceptions.error} onRetry={() => exceptions.refetch()} empty={<EmptyState title="No exceptions in this period" />} />
          {exceptions.data && <Pagination page={excPage} pageSize={8} total={exceptions.data.total} onPage={setExcPage} />}
        </Card>
      </div>
    </div>
  );
}

export function TeamDashboard() {
  return <TeamDashboardView scope={{}} title="Team attendance" description="Everyone in your reporting line (direct and indirect reports)." />;
}
