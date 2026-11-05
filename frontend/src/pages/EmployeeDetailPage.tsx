import { useQuery } from "@tanstack/react-query";
import { CalendarPlus } from "lucide-react";
import { useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";

import { useAuth } from "@/auth/AuthContext";
import { EmployeeAttendanceView } from "@/components/attendance/EmployeeAttendanceView";
import { LeaveFormModal } from "@/components/attendance/RequestForms";
import { Button } from "@/components/ui/Button";
import { Avatar } from "@/components/ui/Misc";
import { PageHeader } from "@/components/ui/PageHeader";
import { PageSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { api } from "@/lib/api";
import { titleCase } from "@/lib/format";
import type { TeamMember } from "@/lib/types";

/** An employee's attendance seen by their manager chain or an admin (scope enforced by the API). */
export function EmployeeDetailPage() {
  const { id } = useParams();
  const employeeId = Number(id);
  const { user } = useAuth();
  const [params] = useSearchParams();
  const [leave, setLeave] = useState(false);
  const members = useQuery({
    queryKey: ["team-members", "all"],
    queryFn: () => api.get<TeamMember[]>("/team/members", { include_inactive: true }),
  });
  if (members.isError) return <ErrorState error={members.error} onRetry={() => members.refetch()} />;
  if (!members.data) return <PageSkeleton />;
  const emp = members.data.find((m) => m.id === employeeId);
  if (!emp || !user)
    return <EmptyState title="Employee not found" description="This person is not in your reporting scope." action={<Link to="/team" className="text-sm text-brand-text hover:underline">Back to team</Link>} />;

  const back = "/team?" + new URLSearchParams({ start: params.get("start") ?? "", end: params.get("end") ?? "" }).toString();
  return (
    <>
      <EmployeeAttendanceView
        employeeId={employeeId}
        isSelf={false}
        header={
          <PageHeader
            breadcrumb={
              <>
                <Link to={user.role === "ADMIN" ? "/admin" : back} className="hover:underline">
                  {user.role === "ADMIN" ? "Organisation" : "Team"}
                </Link>{" "}
                / {emp.department_name ?? "Unassigned"} / {emp.full_name}
              </>
            }
            title={
              <span className="flex items-center gap-3">
                <Avatar name={emp.full_name} />
                <span>
                  {emp.full_name}
                  <span className="block text-sm font-normal text-ink-2">
                    {emp.employee_code} · {emp.job_title ?? titleCase(emp.role)} · reports to {emp.manager_name ?? "—"}
                    {!emp.is_active && " · inactive"}
                  </span>
                </span>
              </span>
            }
            actions={
              <Button variant="secondary" icon={<CalendarPlus className="size-4" />} onClick={() => setLeave(true)}>
                Record leave
              </Button>
            }
          />
        }
      />
      <LeaveFormModal open={leave} onClose={() => setLeave(false)} employeeId={emp.id} employeeName={emp.full_name} />
    </>
  );
}
