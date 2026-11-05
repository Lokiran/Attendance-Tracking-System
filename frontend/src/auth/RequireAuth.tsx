import { Navigate, Outlet, useLocation } from "react-router-dom";

import { PageSkeleton } from "@/components/ui/Skeleton";
import type { Role } from "@/lib/types";

import { useAuth } from "./AuthContext";

/** UX guard only: the backend enforces every permission independently. */
export function RequireAuth({ roles }: { roles?: Role[] }) {
  const { user, loading } = useAuth();
  const location = useLocation();
  if (loading) return <PageSkeleton />;
  if (!user) return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  if (roles && !roles.includes(user.role)) return <Navigate to="/" replace />;
  return <Outlet />;
}
