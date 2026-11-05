import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { lazy, Suspense } from "react";
import { Link, Route, Routes } from "react-router-dom";

import { AuthProvider } from "@/auth/AuthContext";
import { RequireAuth } from "@/auth/RequireAuth";
import { AppLayout } from "@/components/layout/AppLayout";
import { PageSkeleton } from "@/components/ui/Skeleton";
import { EmptyState } from "@/components/ui/States";
import { ToastProvider } from "@/components/ui/Toast";
import { ApiError } from "@/lib/api";
import { LoginPage } from "@/pages/LoginPage";
import { MyDashboard } from "@/pages/MyDashboard";

// Route-level code splitting: each page loads on first visit.
const ApprovalsPage = lazy(() => import("@/pages/ApprovalsPage").then((m) => ({ default: m.ApprovalsPage })));
const AuditLogPage = lazy(() => import("@/pages/admin/AuditLogPage").then((m) => ({ default: m.AuditLogPage })));
const DepartmentsAdmin = lazy(() => import("@/pages/admin/DepartmentsAdmin").then((m) => ({ default: m.DepartmentsAdmin })));
const EmployeesAdmin = lazy(() => import("@/pages/admin/EmployeesAdmin").then((m) => ({ default: m.EmployeesAdmin })));
const HierarchyPage = lazy(() => import("@/pages/admin/HierarchyPage").then((m) => ({ default: m.HierarchyPage })));
const HolidaysAdmin = lazy(() => import("@/pages/admin/HolidaysAdmin").then((m) => ({ default: m.HolidaysAdmin })));
const OrgDashboard = lazy(() => import("@/pages/admin/OrgDashboard").then((m) => ({ default: m.OrgDashboard })));
const PolicyAdmin = lazy(() => import("@/pages/admin/PolicyAdmin").then((m) => ({ default: m.PolicyAdmin })));
const SchedulesAdmin = lazy(() => import("@/pages/admin/SchedulesAdmin").then((m) => ({ default: m.SchedulesAdmin })));
const EmployeeDetailPage = lazy(() => import("@/pages/EmployeeDetailPage").then((m) => ({ default: m.EmployeeDetailPage })));
const MyRequests = lazy(() => import("@/pages/MyRequests").then((m) => ({ default: m.MyRequests })));
const ProfilePage = lazy(() => import("@/pages/ProfilePage").then((m) => ({ default: m.ProfilePage })));
const ReportsPage = lazy(() => import("@/pages/ReportsPage").then((m) => ({ default: m.ReportsPage })));
const TeamDashboard = lazy(() => import("@/pages/TeamDashboard").then((m) => ({ default: m.TeamDashboard })));
const DepartmentDashboardPage = lazy(() => import("@/pages/ScopedDashboards").then((m) => ({ default: m.DepartmentDashboardPage })));
const ManagerDashboardPage = lazy(() => import("@/pages/ScopedDashboards").then((m) => ({ default: m.ManagerDashboardPage })));

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 15_000,
        refetchOnWindowFocus: true,
        // Do not hammer the API on client errors (401/403/404/422); retry transient failures once.
        retry: (count, err) => !(err instanceof ApiError && err.status >= 400 && err.status < 500) && count < 1,
      },
    },
  });
}

export function AppRoutes() {
  return (
    <Suspense fallback={<PageSkeleton />}>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route element={<RequireAuth />}>
          <Route element={<AppLayout />}>
            <Route index element={<MyDashboard />} />
            <Route path="my/requests" element={<MyRequests />} />
            <Route path="profile" element={<ProfilePage />} />
            <Route element={<RequireAuth roles={["MANAGER", "ADMIN"]} />}>
              <Route path="team" element={<TeamDashboard />} />
              <Route path="people/:id" element={<EmployeeDetailPage />} />
              <Route path="approvals" element={<ApprovalsPage />} />
              <Route path="reports" element={<ReportsPage />} />
              <Route path="admin/managers/:id" element={<ManagerDashboardPage />} />
            </Route>
            <Route element={<RequireAuth roles={["ADMIN"]} />}>
              <Route path="admin" element={<OrgDashboard />} />
              <Route path="admin/hierarchy" element={<HierarchyPage />} />
              <Route path="admin/employees" element={<EmployeesAdmin />} />
              <Route path="admin/departments" element={<DepartmentsAdmin />} />
              <Route path="admin/departments/:id/dashboard" element={<DepartmentDashboardPage />} />
              <Route path="admin/schedules" element={<SchedulesAdmin />} />
              <Route path="admin/holidays" element={<HolidaysAdmin />} />
              <Route path="admin/policy" element={<PolicyAdmin />} />
              <Route path="admin/audit" element={<AuditLogPage />} />
            </Route>
            <Route
              path="*"
              element={<EmptyState title="Page not found" description="The page you requested does not exist." action={<Link to="/" className="text-sm text-brand-text hover:underline">Go to my dashboard</Link>} />}
            />
          </Route>
        </Route>
      </Routes>
    </Suspense>
  );
}

export function App({ client = createQueryClient() }: { client?: QueryClient }) {
  return (
    <QueryClientProvider client={client}>
      <ToastProvider>
        <AuthProvider>
          <AppRoutes />
        </AuthProvider>
      </ToastProvider>
    </QueryClientProvider>
  );
}
