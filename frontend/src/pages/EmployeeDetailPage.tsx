import { useQuery } from "@tanstack/react-query";
import { Link, useParams, useSearchParams } from "react-router-dom";

import { useAuth } from "@/auth/AuthContext";
import { EmployeeAttendanceView } from "@/components/attendance/EmployeeAttendanceView";
import { Avatar } from "@/components/ui/Misc";
import { PageHeader } from "@/components/ui/PageHeader";
import { PageSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { api, ApiError } from "@/lib/api";
import { ROLE_LABEL } from "@/lib/format";
import type { TeamMember } from "@/lib/types";

/** An employee's attendance seen by their manager chain or an admin (scope enforced by the API). */
export function EmployeeDetailPage() {
  const { id } = useParams();
  const employeeId = Number(id);
  const { user } = useAuth();
  const [params] = useSearchParams();
  // One lookup for this person (scope-checked by the API), never the whole team list.
  const member = useQuery({
    queryKey: ["team-member", employeeId],
    queryFn: () => api.get<TeamMember>("/team/members/" + employeeId),
    enabled: Number.isInteger(employeeId) && employeeId > 0,
    retry: (count, e) => !(e instanceof ApiError && e.status === 404) && count < 2,
  });
  if (member.isError && !(member.error instanceof ApiError && member.error.status === 404))
    return <ErrorState error={member.error} onRetry={() => member.refetch()} />;
  if (member.isLoading) return <PageSkeleton />;
  const emp = member.data;
  if (!emp || !user)
    return <EmptyState title="Employee not found" description="This person is not in your reporting scope." action={<Link to="/team" className="text-sm text-brand-text hover:underline">Back to team</Link>} />;

  const back = "/team?" + new URLSearchParams({ start: params.get("start") ?? "", end: params.get("end") ?? "" }).toString();
  return (
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
                  {emp.employee_code} · {emp.job_title ?? ROLE_LABEL[emp.role]} · reports to {emp.manager_name ?? "—"}
                  {!emp.is_active && " · inactive"}
                </span>
              </span>
            </span>
          }
        />
      }
    />
  );
}
