/* eslint-disable @typescript-eslint/no-explicit-any */
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, Gauge, Upload, UserCheck, UserX, Users } from "lucide-react";
import { Fragment, useMemo, useState } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { Bar, BarChart, CartesianGrid, Cell, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";

import { useAuth } from "@/auth/AuthContext";
import { ChartCard, Legend } from "@/components/charts/ChartCard";
import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, type Column, type SortState } from "@/components/ui/DataTable";
import { Checkbox, SearchInput, Select } from "@/components/ui/Fields";
import { ExportButton } from "@/components/ui/Misc";
import { PageHeader } from "@/components/ui/PageHeader";
import { PageSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { CardGrid, SummaryCard } from "@/components/ui/SummaryCard";
import {
  ComplianceBadge,
  MODE_META,
  MonthPicker,
  OverrideDialog,
  PolicyLine,
  thisMonth,
  WeeklyProgress,
  WfoCalendar,
  WfoDonut,
  WfoKpis,
} from "@/components/wfo/WfoParts";
import { api, ApiError } from "@/lib/api";
import { formatPercent } from "@/lib/format";
import { useMemberWfo, useMyWfo, useOrgWfo, useTeamWfo } from "@/lib/queries";
import type { Department, EmployeeWfo, WfoDay, WfoRow, WfoTotals } from "@/lib/types";

function useMonth(): [string, (m: string) => void] {
  const [params, setParams] = useSearchParams();
  const month = params.get("month") || thisMonth();
  return [month, (m) => setParams((p) => { p.set("month", m); return p; }, { replace: true })];
}

/** KPIs, weekly progress, calendar heatmap and donut for one employee's month. */
function EmployeeWfoView({ data, loading, onSelectDay }: { data?: EmployeeWfo; loading: boolean; onSelectDay?: (d: WfoDay) => void }) {
  return (
    <div className="space-y-5">
      <WfoKpis summary={data?.summary} policy={data?.policy} loading={loading} />
      {data && <PolicyLine policy={data.policy} />}
      <div className="grid gap-5 xl:grid-cols-3">
        <Card className="xl:col-span-2">
          <CardHeader title="Calendar" subtitle={onSelectDay ? "Click a day to override its work mode" : "Work mode per day; hover a day for details"} />
          {data ? <WfoCalendar data={data} onSelectDay={onSelectDay} /> : <div className="m-4 h-72 animate-pulse rounded-lg bg-surface-2" />}
        </Card>
        <div className="space-y-5">
          <ChartCard title="WFO vs WFH" subtitle="Completed days this month" loading={loading} height={200}
            isEmpty={!!data && data.summary.wfo_days + data.summary.wfh_days === 0}
            legend={<Legend items={[{ label: "WFO", color: MODE_META.OFFICE.color }, { label: "WFH", color: MODE_META.WFH.color }]} />}
            table={data ? { columns: ["Mode", "Days"], rows: [["WFO", data.summary.wfo_days], ["WFH", data.summary.wfh_days]] } : undefined}>
            {data && <WfoDonut summary={data.summary} />}
          </ChartCard>
          <Card>
            <CardHeader title="Weekly WFO" subtitle={data ? `Goal: ${data.policy.min_days_per_week} office days a week` : undefined} />
            {data ? <WeeklyProgress weeks={data.weekly_wfo_breakdown} policy={data.policy} /> : <div className="m-4 h-32 animate-pulse rounded-lg bg-surface-2" />}
          </Card>
        </div>
      </div>
    </div>
  );
}

export function MyWfoPage() {
  const [month, setMonth] = useMonth();
  const q = useMyWfo(month);
  return (
    <div className="space-y-5">
      <PageHeader title="Office days" description="Your work-from-office days, weekly progress and compliance with the office policy."
        actions={<MonthPicker value={month} onChange={setMonth} />} />
      {q.isError ? <ErrorState error={q.error} onRetry={() => q.refetch()} /> : <EmployeeWfoView data={q.data} loading={q.isLoading} />}
    </div>
  );
}

export function MemberWfoPage() {
  const id = Number(useParams().id);
  const { user } = useAuth();
  const [month, setMonth] = useMonth();
  const q = useMemberWfo(id, month);
  const [day, setDay] = useState<WfoDay | null>(null);
  const isAdmin = user?.role === "ADMIN";
  if (q.error instanceof ApiError && q.error.status === 403)
    return <EmptyState title="Not in your team" description="You can only see office days for people in your reporting line." action={<Link to="/team/wfo" className="text-sm text-brand-text hover:underline">Back to team office days</Link>} />;
  const back = isAdmin ? "/admin/wfo" : "/team/wfo";
  return (
    <div className="space-y-5">
      <PageHeader
        breadcrumb={<><Link to={`${back}?month=${month}`} className="hover:underline">{isAdmin ? "Organisation office days" : "Team office days"}</Link> / {q.data?.employee.full_name ?? "…"}</>}
        title={q.data ? q.data.employee.full_name : "Office days"}
        description={q.data && `${q.data.employee.employee_code} · ${q.data.employee.department_name ?? "No department"} · reports to ${q.data.employee.manager_name ?? "—"}`}
        actions={<MonthPicker value={month} onChange={setMonth} />} />
      {q.isError && !(q.error instanceof ApiError && q.error.status === 403) ? <ErrorState error={q.error} onRetry={() => q.refetch()} />
        : <EmployeeWfoView data={q.data} loading={q.isLoading} onSelectDay={isAdmin ? setDay : undefined} />}
      {isAdmin && q.data && <OverrideDialog employeeId={id} employeeName={q.data.employee.full_name} day={day} onClose={() => setDay(null)} />}
    </div>
  );
}

function TotalsCards({ totals, loading }: { totals?: WfoTotals; loading: boolean }) {
  return (
    <CardGrid cols={4}>
      <SummaryCard label="People" icon={<Users className="size-4" />} loading={loading} value={totals?.headcount} />
      <SummaryCard label="Overall WFO %" icon={<Gauge className="size-4" />} loading={loading} value={formatPercent(totals?.wfo_percentage)}
        hint={totals && `${totals.wfo_days} WFO of ${totals.working_days} working days`} />
      <SummaryCard label="Compliant" icon={<UserCheck className="size-4" />} loading={loading} value={totals?.compliant} />
      <SummaryCard label="Non-compliant" icon={<UserX className="size-4" />} loading={loading} value={totals?.non_compliant} />
    </CardGrid>
  );
}

const WFO_LIMIT = 25;

/** WFO days per person (lowest first) against the policy line. */
function MembersChart({ rows, policyDays, link }: { rows: WfoRow[]; policyDays: number; link: (r: WfoRow) => string }) {
  const navigate = useNavigate();
  const sorted = [...rows].sort((a, b) => a.wfo_days - b.wfo_days).slice(0, WFO_LIMIT);
  const data = sorted.map((r) => ({ name: r.employee.full_name, wfo: r.wfo_days, required: r.required_days, row: r }));
  return (
    <ChartCard
      title="WFO days per person"
      subtitle={rows.length > WFO_LIMIT ? `The ${WFO_LIMIT} lowest of ${rows.length}, against the policy line` : "Lowest first, against the policy line"}
      isEmpty={!rows.length}
      height={Math.max(200, data.length * 28 + 30)}
      legend={<Legend items={[{ label: "WFO days", color: MODE_META.OFFICE.color }, { label: `Policy: ${policyDays} days`, color: "var(--text-2)", dashed: true }]} />}
      table={{ columns: ["Name", "WFO days", "Required (prorated)", "Compliance"], rows: sorted.map((r) => [r.employee.full_name, r.wfo_days, r.required_days, r.compliance === "COMPLIANT" ? "Compliant" : "Non-compliant"]) }}
    >
      <ResponsiveContainer>
        <BarChart data={data} layout="vertical" margin={{ top: 4, right: 40, bottom: 0, left: 8 }}>
          <CartesianGrid stroke="var(--chart-grid)" horizontal={false} />
          <XAxis type="number" allowDecimals={false} domain={[0, Math.max(policyDays, ...data.map((d) => d.wfo)) + 1]} stroke="var(--chart-axis)" fontSize={11} tickLine={false} axisLine={false} />
          <YAxis type="category" dataKey="name" width={130} interval={0} stroke="var(--chart-axis)" fontSize={11} tickLine={false} axisLine={false} />
          <Tooltip cursor={{ fill: "var(--surface-2)" }} content={({ active, payload }: any) => active && payload?.length ? (
            <div className="rounded-lg border border-line bg-surface px-3 py-2 text-xs shadow-lg">
              <div className="mb-1 font-medium text-ink">{payload[0].payload.name}</div>
              <div className="text-ink-2">WFO days <span className="tabular ml-2 font-medium text-ink">{payload[0].payload.wfo}</span></div>
              <div className="text-ink-2">Required <span className="tabular ml-2 font-medium text-ink">{payload[0].payload.required}</span></div>
            </div>
          ) : null} />
          <ReferenceLine x={policyDays} stroke="var(--text-2)" strokeDasharray="4 4" label={{ value: `${policyDays}`, position: "top", fill: "var(--text-2)", fontSize: 11 }} />
          <Bar dataKey="wfo" name="WFO days" radius={[0, 4, 4, 0]} maxBarSize={16} cursor="pointer" onClick={(d: any) => d?.payload?.row && navigate(link(d.payload.row))}>
            {data.map((d) => <Cell key={d.name} fill={MODE_META.OFFICE.color} />)}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </ChartCard>
  );
}

const SORTERS: Record<string, (r: WfoRow) => string | number> = {
  name: (r) => r.employee.full_name.toLowerCase(),
  wfo_days: (r) => r.wfo_days,
  wfo_percentage: (r) => r.wfo_percentage ?? -1,
  shortfall_days: (r) => r.shortfall_days,
  compliance: (r) => r.compliance,
};

function useSortedRows(rows: WfoRow[], search: string, onlyNonCompliant: boolean, sort: SortState) {
  return useMemo(() => {
    const q = search.trim().toLowerCase();
    const filtered = rows.filter((r) => (!onlyNonCompliant || r.compliance === "NON_COMPLIANT")
      && (!q || r.employee.full_name.toLowerCase().includes(q) || r.employee.employee_code.toLowerCase().includes(q)));
    const key = SORTERS[sort.sort] ?? SORTERS.name;
    return [...filtered].sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0) * (sort.order === "asc" ? 1 : -1));
  }, [rows, search, onlyNonCompliant, sort]);
}

function rowColumns(link: (r: WfoRow) => string, withManager = false): Column<WfoRow>[] {
  return [
    { key: "name", header: "Name", sortable: true, cell: (r) => <Link to={link(r)} className="font-medium text-ink hover:text-brand-text hover:underline">{r.employee.full_name}<span className="block text-xs font-normal text-ink-3">{r.employee.employee_code}</span></Link> },
    ...(withManager ? [{ key: "manager", header: "Manager", cell: (r: WfoRow) => r.employee.manager_name ?? "—" }] : []),
    { key: "wfo_days", header: "WFO days", sortable: true, align: "right", cell: (r) => <span>{r.wfo_days}<span className="text-ink-3"> / {r.required_days}</span></span> },
    { key: "wfh", header: "WFH days", align: "right", cell: (r) => r.wfh_days },
    { key: "wfo_percentage", header: "WFO %", sortable: true, align: "right", cell: (r) => formatPercent(r.wfo_percentage) },
    { key: "shortfall_days", header: "Shortfall", sortable: true, align: "right", cell: (r) => r.shortfall_days || "—" },
    { key: "compliance", header: "Compliance", sortable: true, cell: (r) => <ComplianceBadge value={r.compliance} /> },
  ];
}

export function TeamWfoPage() {
  const [month, setMonth] = useMonth();
  const q = useTeamWfo(month);
  const [search, setSearch] = useState("");
  const [onlyBad, setOnlyBad] = useState(false);
  const [sort, setSort] = useState<SortState>({ sort: "wfo_days", order: "asc" });
  const rows = useSortedRows(q.data?.rows ?? [], search, onlyBad, sort);
  const link = (r: WfoRow) => `/team/wfo/${r.employee.id}?month=${month}`;
  return (
    <div className="space-y-5">
      <PageHeader title="Team office days" description="WFO days and policy compliance for you and everyone in your reporting line."
        actions={<MonthPicker value={month} onChange={setMonth} />} />
      {q.isError ? <ErrorState error={q.error} onRetry={() => q.refetch()} /> : (
        <>
          <TotalsCards totals={q.data?.totals} loading={q.isLoading} />
          {q.data && <PolicyLine policy={q.data.policy} />}
          {q.data && <MembersChart rows={q.data.rows} policyDays={Math.max(0, ...q.data.rows.map((r) => r.required_days))} link={link} />}
          <Card>
            <CardHeader title="Team members" actions={<>
              <SearchInput value={search} onChange={setSearch} placeholder="Name or employee ID" label="Search team" />
              <Checkbox label="Non-compliant only" checked={onlyBad} onChange={(e) => setOnlyBad(e.target.checked)} />
            </>} />
            <DataTable caption="Team office days" columns={rowColumns(link)} rows={rows} rowKey={(r) => r.employee.id} loading={q.isLoading}
              sort={sort} onSort={setSort} empty={<EmptyState title={onlyBad ? "Everyone is compliant" : "No one in your team"} />} />
          </Card>
        </>
      )}
    </div>
  );
}

export function OrgWfoPage() {
  const [month, setMonth] = useMonth();
  const [params, setParams] = useSearchParams();
  const managerId = params.get("manager_id") ?? "";
  const departmentId = params.get("department_id") ?? "";
  const setFilter = (key: string, v: string) => setParams((p) => { if (v) p.set(key, v); else p.delete(key); return p; }, { replace: true });
  const q = useOrgWfo({ month, manager_id: managerId, department_id: departmentId });
  const managers = useQuery({ queryKey: ["admin", "managers"], queryFn: () => api.get<{ id: number; full_name: string }[]>("/managers") });
  const departments = useQuery({ queryKey: ["departments"], queryFn: () => api.get<Department[]>("/departments") });
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [onlyBad, setOnlyBad] = useState(false);
  const link = (r: WfoRow) => `/admin/wfo/${r.employee.id}?month=${month}`;
  const toggle = (k: string) => setOpen((s) => { const n = new Set(s); if (n.has(k)) n.delete(k); else n.add(k); return n; });
  return (
    <div className="space-y-5">
      <PageHeader title="Organisation office days" description="WFO compliance across the organisation, grouped by manager."
        actions={<>
          <Link to="/admin/attendance-import" className="inline-flex h-9 items-center gap-2 rounded-lg border border-line-strong bg-surface px-3.5 text-sm font-medium text-ink hover:bg-surface-2"><Upload className="size-4" aria-hidden />Import attendance</Link>
          <ExportButton path="/org/wfo/export.xlsx" params={{ month, manager_id: managerId || undefined, department_id: departmentId || undefined }} label="Export Excel" />
        </>} />
      <Card className="flex flex-wrap items-center gap-4 px-4 py-3">
        <MonthPicker value={month} onChange={setMonth} />
        <div className="flex items-center gap-2">
          <label htmlFor="wfo-manager" className="text-xs text-ink-3">Manager</label>
          <Select id="wfo-manager" className="w-48" value={managerId} onChange={(e) => setFilter("manager_id", e.target.value)}>
            <option value="">Everyone</option>
            {managers.data?.map((m) => <option key={m.id} value={m.id}>{m.full_name}</option>)}
          </Select>
        </div>
        <div className="flex items-center gap-2">
          <label htmlFor="wfo-dept" className="text-xs text-ink-3">Department</label>
          <Select id="wfo-dept" className="w-48" value={departmentId} onChange={(e) => setFilter("department_id", e.target.value)}>
            <option value="">All departments</option>
            {departments.data?.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </Select>
        </div>
        <Checkbox label="Non-compliant only" checked={onlyBad} onChange={(e) => setOnlyBad(e.target.checked)} />
      </Card>
      {q.isError ? <ErrorState error={q.error} onRetry={() => q.refetch()} /> : (
        <>
          <TotalsCards totals={q.data?.totals} loading={q.isLoading} />
          {q.data && <PolicyLine policy={q.data.policy} />}
          <Card>
            <CardHeader title="By manager" subtitle="Grouped by each person's direct manager. Expand a manager to see their people." />
            {!q.data ? <PageSkeleton /> : q.data.groups.length === 0 ? <EmptyState title="No one matches these filters" /> : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[720px] border-collapse text-sm">
                  <caption className="sr-only">WFO by manager</caption>
                  <thead>
                    <tr className="border-b border-line bg-secondary-soft text-left text-xs text-ink-2">
                      <th scope="col" className="px-4 py-2.5 font-medium">Manager / employee</th>
                      <th scope="col" className="px-4 py-2.5 text-right font-medium">People</th>
                      <th scope="col" className="px-4 py-2.5 text-right font-medium">WFO days</th>
                      <th scope="col" className="px-4 py-2.5 text-right font-medium">WFO %</th>
                      <th scope="col" className="px-4 py-2.5 font-medium">Compliance</th>
                    </tr>
                  </thead>
                  <tbody>
                    {q.data.groups.map((g) => {
                      const key = String(g.manager_id ?? "none");
                      const expanded = open.has(key);
                      const members = onlyBad ? g.rows.filter((r) => r.compliance === "NON_COMPLIANT") : g.rows;
                      if (onlyBad && !members.length) return null;
                      return (
                        <Fragment key={key}>
                          <tr className="border-b border-line">
                            <td className="px-4 py-2.5">
                              <button type="button" onClick={() => toggle(key)} aria-expanded={expanded} className="inline-flex items-center gap-1.5 font-medium text-ink hover:text-brand-text">
                                {expanded ? <ChevronDown className="size-4" aria-hidden /> : <ChevronRight className="size-4" aria-hidden />}
                                {g.manager_name}
                              </button>
                              {g.department_name && <span className="ml-2 text-xs text-ink-3">{g.department_name}</span>}
                            </td>
                            <td className="tabular px-4 py-2.5 text-right">{g.totals.headcount}</td>
                            <td className="tabular px-4 py-2.5 text-right">{g.totals.wfo_days}</td>
                            <td className="tabular px-4 py-2.5 text-right">{formatPercent(g.totals.wfo_percentage)}</td>
                            <td className="px-4 py-2.5 text-xs text-ink-2">
                              <span className="text-ink">{g.totals.compliant}</span> compliant · <span className={g.totals.non_compliant ? "font-medium text-critical-text" : ""}>{g.totals.non_compliant}</span> not
                            </td>
                          </tr>
                          {expanded && members.map((r) => (
                            <tr key={r.employee.id} className="border-b border-line bg-surface-2/50">
                              <td className="py-2 pl-11 pr-4"><Link to={link(r)} className="text-ink hover:text-brand-text hover:underline">{r.employee.full_name}</Link><span className="ml-2 text-xs text-ink-3">{r.employee.employee_code}</span></td>
                              <td className="px-4 py-2 text-right text-xs text-ink-3">{r.employee.department_name ?? ""}</td>
                              <td className="tabular px-4 py-2 text-right">{r.wfo_days}<span className="text-ink-3"> / {r.required_days}</span></td>
                              <td className="tabular px-4 py-2 text-right">{formatPercent(r.wfo_percentage)}</td>
                              <td className="px-4 py-2"><ComplianceBadge value={r.compliance} /></td>
                            </tr>
                          ))}
                        </Fragment>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </Card>
          {q.data && (
            <MembersChart rows={q.data.groups.flatMap((g) => g.rows)} policyDays={Math.max(0, ...q.data.groups.flatMap((g) => g.rows.map((r) => r.required_days)))} link={link} />
          )}
        </>
      )}
    </div>
  );
}
