import { useQuery } from "@tanstack/react-query";
import { Link, useParams } from "react-router-dom";

import { PageSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { api } from "@/lib/api";
import type { Department, TeamMember } from "@/lib/types";

import { TeamDashboardView } from "./TeamDashboard";

const crumb = (label: string) => (
  <>
    <Link to="/admin" className="hover:underline">Organisation</Link> / {label}
  </>
);

export function DepartmentDashboardPage() {
  const id = Number(useParams().id);
  const q = useQuery({ queryKey: ["departments"], queryFn: () => api.get<Department[]>("/departments") });
  if (q.isError) return <ErrorState error={q.error} onRetry={() => q.refetch()} />;
  if (!q.data) return <PageSkeleton />;
  const dept = q.data.find((d) => d.id === id);
  if (!dept) return <EmptyState title="Department not found" />;
  return (
    <TeamDashboardView
      key={"d" + id}
      scope={{ department_id: id }}
      title={dept.name}
      breadcrumb={crumb("Departments")}
      description={(dept.head ? "Head: " + dept.head.full_name + " · " : "") + dept.member_count + " active member(s)"}
    />
  );
}

export function ManagerDashboardPage() {
  const id = Number(useParams().id);
  const q = useQuery({ queryKey: ["team-members", "all"], queryFn: () => api.get<TeamMember[]>("/team/members", { include_inactive: true }) });
  if (q.isError) return <ErrorState error={q.error} onRetry={() => q.refetch()} />;
  if (!q.data) return <PageSkeleton />;
  const mgr = q.data.find((m) => m.id === id);
  if (!mgr) return <EmptyState title="Manager not found" description="This person is not in your reporting scope." />;
  return (
    <TeamDashboardView
      key={"m" + id}
      scope={{ manager_id: id }}
      title={mgr.full_name + "'s team"}
      breadcrumb={crumb("Managers")}
      description={(mgr.job_title ?? "Manager") + " · " + (mgr.department_name ?? "Unassigned") + " · includes indirect reports"}
    />
  );
}
