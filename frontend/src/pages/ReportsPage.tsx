import { useQuery } from "@tanstack/react-query";
import { FileSpreadsheet } from "lucide-react";
import { useState } from "react";

import { useAuth } from "@/auth/AuthContext";
import { Card } from "@/components/ui/Card";
import { DateRangeFilter } from "@/components/ui/DateRangeFilter";
import { Field, Input, Select } from "@/components/ui/Fields";
import { ExportButton } from "@/components/ui/Misc";
import { PageHeader } from "@/components/ui/PageHeader";
import { api } from "@/lib/api";
import { presetRange, todayInOffice } from "@/lib/dates";
import type { Department, TeamMember } from "@/lib/types";

type ReportKind = "employee" | "team" | "department" | "monthly" | "exceptions";

const REPORTS: { kind: ReportKind; title: string; description: string; adminOnly?: boolean }[] = [
  { kind: "team", title: "Team attendance", description: "One row per employee: expected, present, absent, worked vs shift hours, variance and attendance %." },
  { kind: "employee", title: "Employee attendance", description: "Daily records for one employee: status, first check-in, last check-out, net hours, exceptions." },
  { kind: "monthly", title: "Monthly attendance", description: "Per-employee summary for a calendar month." },
  { kind: "exceptions", title: "Attendance exceptions", description: "Missing check-outs, late arrivals, short hours and absences." },
  { kind: "department", title: "Department attendance", description: "Department-level comparison for the organisation.", adminOnly: true },
];

export function ReportsPage() {
  const { user } = useAuth();
  const isAdmin = user?.role === "ADMIN";
  const today = todayInOffice();
  const [range, setRange] = useState(presetRange("this_month", today));
  const [month, setMonth] = useState(today.slice(0, 7));
  const [departmentId, setDepartmentId] = useState("");
  const [employeeId, setEmployeeId] = useState("");
  const members = useQuery({ queryKey: ["team-members", "all"], queryFn: () => api.get<TeamMember[]>("/team/members", { include_inactive: true }) });
  const departments = useQuery({ queryKey: ["departments"], queryFn: () => api.get<Department[]>("/departments"), enabled: isAdmin });
  const scope = { department_id: departmentId || undefined };
  const [year, mon] = month.split("-").map(Number);

  return (
    <div className="space-y-5">
      <PageHeader title="Reports" description="CSV exports use the same access rules as the dashboards: you only export people in your scope." />
      <Card className="flex flex-wrap items-end gap-4 p-4">
        <DateRangeFilter start={range.start} end={range.end} onChange={setRange} />
        {isAdmin && (
          <Field label="Department" htmlFor="r-dept">
            <Select id="r-dept" className="w-48" value={departmentId} onChange={(e) => setDepartmentId(e.target.value)}>
              <option value="">All departments</option>
              {departments.data?.map((d) => (
                <option key={d.id} value={d.id}>{d.name}</option>
              ))}
            </Select>
          </Field>
        )}
      </Card>
      <div className="grid gap-4 md:grid-cols-2">
        {REPORTS.filter((r) => !r.adminOnly || isAdmin).map((r) => (
          <Card key={r.kind} className="flex flex-col gap-3 p-5">
            <div className="flex items-start gap-3">
              <FileSpreadsheet className="mt-0.5 size-5 text-ink-3" aria-hidden />
              <div>
                <h2 className="text-sm font-semibold text-ink">{r.title}</h2>
                <p className="mt-0.5 text-xs text-ink-2">{r.description}</p>
              </div>
            </div>
            {r.kind === "employee" && (
              <Field label="Employee" htmlFor="r-emp">
                <Select id="r-emp" value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}>
                  <option value="">Select an employee…</option>
                  {members.data?.map((m) => (
                    <option key={m.id} value={m.id}>{m.full_name} ({m.employee_code})</option>
                  ))}
                </Select>
              </Field>
            )}
            {r.kind === "monthly" && (
              <Field label="Month" htmlFor="r-month">
                <Input id="r-month" type="month" value={month} max={today.slice(0, 7)} onChange={(e) => e.target.value && setMonth(e.target.value)} />
              </Field>
            )}
            <div className="mt-auto">
              {r.kind === "team" && <ExportButton path="/reports/team-attendance.csv" params={{ ...range, ...scope }} />}
              {r.kind === "employee" && (employeeId ? <ExportButton path="/reports/employee-attendance.csv" params={{ ...range, employee_id: employeeId }} /> : <p className="text-xs text-ink-3">Choose an employee to export.</p>)}
              {r.kind === "monthly" && <ExportButton path="/reports/monthly-attendance.csv" params={{ year, month: mon, ...scope }} />}
              {r.kind === "exceptions" && <ExportButton path="/reports/exceptions.csv" params={{ ...range, ...scope }} />}
              {r.kind === "department" && <ExportButton path="/reports/department-attendance.csv" params={range} />}
            </div>
          </Card>
        ))}
      </div>
    </div>
  );
}
