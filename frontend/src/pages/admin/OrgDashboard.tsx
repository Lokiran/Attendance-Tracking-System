import { Network } from "lucide-react";
import { Link, useNavigate } from "react-router-dom";

import { DistributionChart, RankedBarChart } from "@/components/charts/Charts";
import { ChartCard } from "@/components/charts/ChartCard";
import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { ExportButton } from "@/components/ui/Misc";
import { withRange } from "@/hooks/useUrlState";
import { formatMinutes, formatPercent, hours } from "@/lib/format";
import { useDepartmentStats, useManagerStats, useOverview } from "@/lib/queries";
import type { DepartmentStats } from "@/lib/types";

import { TeamDashboardView } from "../TeamDashboard";

function Comparisons({ start, end, onDate }: { start: string; end: string; onDate: string }) {
  const navigate = useNavigate();
  const depts = useDepartmentStats({ start, end, on_date: onDate });
  const managers = useManagerStats({ start, end, on_date: onDate });
  // Same query (and cache entry) as the headline cards; bands are counted over everyone by the API.
  const overview = useOverview({ start, end, on_date: onDate });
  const deptRows = depts.data ?? [];
  const mgrRows = managers.data ?? [];
  const bins = overview.data?.attendance_bands ?? [];

  const deptColumns: Column<DepartmentStats>[] = [
    {
      key: "name",
      header: "Department",
      cell: (d) =>
        d.department_id ? (
          <Link className="font-medium hover:underline" to={withRange("/admin/departments/" + d.department_id + "/dashboard", start, end)}>{d.department_name}</Link>
        ) : (
          d.department_name
        ),
    },
    { key: "head", header: "Head", cell: (d) => d.head_name ?? "—" },
    { key: "hc", header: "People", align: "right", cell: (d) => d.headcount },
    { key: "present", header: "Present", align: "right", cell: (d) => d.snapshot.present },
    { key: "absent", header: "Absent / not in", align: "right", cell: (d) => d.snapshot.absent + d.snapshot.not_checked_in },
    { key: "avg", header: "Avg net / day", align: "right", cell: (d) => formatMinutes(d.period.average_net_minutes) },
    { key: "pct", header: "Attendance", align: "right", cell: (d) => formatPercent(d.period.attendance_percentage) },
  ];

  return (
    <>
      <div className="grid gap-5 xl:grid-cols-2">
        <ChartCard
          title="Department-wise attendance"
          subtitle="Attendance % in the period · click to open the department"
          loading={depts.isLoading}
          error={depts.error}
          isEmpty={!deptRows.some((d) => d.period.attendance_percentage !== null)}
          height={Math.max(180, deptRows.length * 36)}
          table={{ columns: ["Department", "Attendance", "People"], rows: deptRows.map((d) => [d.department_name, formatPercent(d.period.attendance_percentage), d.headcount]) }}
        >
          <RankedBarChart unit="%" max={100} data={deptRows.map((d) => ({ id: d.department_id, label: d.department_name, value: d.period.attendance_percentage, hint: d.headcount + " people" }))} onSelect={(id) => id && navigate(withRange("/admin/departments/" + id + "/dashboard", start, end))} />
        </ChartCard>
        <ChartCard
          title="Manager-wise attendance"
          subtitle="Attendance % of each manager's reporting tree · click to open the team"
          loading={managers.isLoading}
          error={managers.error}
          isEmpty={!mgrRows.some((m) => m.period.attendance_percentage !== null)}
          height={Math.max(180, mgrRows.length * 36)}
          table={{ columns: ["Manager", "Team size", "Attendance", "Avg net/day"], rows: mgrRows.map((m) => [m.manager_name, m.team_size, formatPercent(m.period.attendance_percentage), formatMinutes(m.period.average_net_minutes)]) }}
        >
          <RankedBarChart unit="%" max={100} data={mgrRows.map((m) => ({ id: m.manager_id, label: m.manager_name, value: m.period.attendance_percentage, hint: m.team_size + " people" }))} onSelect={(id) => navigate(withRange("/admin/managers/" + id, start, end))} />
        </ChartCard>
        <ChartCard
          title="Attendance percentage distribution"
          subtitle="Employees by attendance band in the period"
          loading={overview.isLoading}
          error={overview.error}
          isEmpty={bins.every((b) => b.count === 0)}
          table={{ columns: ["Band", "Employees"], rows: bins.map((b) => [b.label, b.count]) }}
        >
          <DistributionChart bins={bins} />
        </ChartCard>
        <ChartCard
          title="Office hours comparison"
          subtitle="Average net hours per attended day, by department"
          loading={depts.isLoading}
          isEmpty={!deptRows.some((d) => d.period.average_net_minutes)}
          height={Math.max(180, deptRows.length * 36)}
          table={{ columns: ["Department", "Avg net/day", "Net total"], rows: deptRows.map((d) => [d.department_name, formatMinutes(d.period.average_net_minutes), formatMinutes(d.period.net_minutes)]) }}
        >
          <RankedBarChart unit="h" data={deptRows.map((d) => ({ id: d.department_id, label: d.department_name, value: hours(d.period.average_net_minutes, 2) }))} onSelect={(id) => id && navigate(withRange("/admin/departments/" + id + "/dashboard", start, end))} />
        </ChartCard>
      </div>
      <Card>
        <CardHeader
          title="Departments"
          subtitle="Status on the selected date and statistics for the period"
          actions={
            <>
              <Link to="/admin/hierarchy" className="inline-flex items-center gap-1 text-xs font-medium text-brand-text hover:underline">
                <Network className="size-3.5" aria-hidden /> Organisation hierarchy
              </Link>
              <ExportButton path="/reports/department-attendance.csv" params={{ start, end }} size="sm" />
            </>
          }
        />
        <DataTable caption="Department statistics" columns={deptColumns} rows={depts.data} rowKey={(d) => String(d.department_id)} loading={depts.isFetching} error={depts.error} onRetry={() => depts.refetch()} />
      </Card>
    </>
  );
}

export function OrgDashboard() {
  return (
    <TeamDashboardView
      scope={{}}
      title="Organisation overview"
      description="All active employees whose attendance is tracked."
      extra={(ctx) => <Comparisons {...ctx} />}
    />
  );
}
