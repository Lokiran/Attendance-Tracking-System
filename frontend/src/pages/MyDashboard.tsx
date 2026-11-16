import { ArrowRight, Building, Building2, FileSpreadsheet, FileUp, Users } from "lucide-react";
import type { ComponentType } from "react";
import { Link } from "react-router-dom";

import { useAuth } from "@/auth/AuthContext";
import { EmployeeAttendanceView } from "@/components/attendance/EmployeeAttendanceView";
import { TodayCard } from "@/components/attendance/TodayCard";
import { Card } from "@/components/ui/Card";
import type { Profile } from "@/lib/types";

type Shortcut = { to: string; title: string; text: string; icon: ComponentType<{ className?: string }> };

const ADMIN_SHORTCUTS: Shortcut[] = [
  { to: "/admin", title: "Organisation", text: "Attendance across every department", icon: Building2 },
  { to: "/admin/wfo", title: "Org office days", text: "Who met the WFO requirement this month", icon: Building },
  { to: "/admin/attendance-import", title: "Import timesheets", text: "Add a month's timesheet: Excel, CSV or PDF", icon: FileUp },
  { to: "/reports", title: "Reports", text: "Download attendance and WFO reports", icon: FileSpreadsheet },
];
const MANAGER_SHORTCUTS: Shortcut[] = [
  { to: "/team", title: "Team attendance", text: "Your team's attendance", icon: Users },
  { to: "/team/wfo", title: "Team office days", text: "Who met the WFO requirement this month", icon: Building },
  { to: "/reports", title: "Reports", text: "Download attendance and WFO reports", icon: FileSpreadsheet },
];

/** For accounts left out of attendance (such as the HR admin), there is no personal attendance to show: offer the
 *  pages they work in instead of an empty dashboard. */
function Shortcuts({ user }: { user: Profile }) {
  const items = user.role === "ADMIN" ? ADMIN_SHORTCUTS : user.role === "MANAGER" ? MANAGER_SHORTCUTS : [];
  return (
    <div className="space-y-3">
      <p className="text-sm text-ink-2">Your own attendance is not tracked, so there is no personal dashboard.{items.length > 0 && " Jump to:"}</p>
      {items.length > 0 && (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
          {items.map(({ to, title, text, icon: Icon }) => (
            <Link key={to} to={to} className="group rounded-xl">
              <Card className="flex h-full items-start gap-3 p-5 transition-colors group-hover:border-brand">
                <span className="rounded-lg bg-brand-soft p-2 text-brand-text" aria-hidden><Icon className="size-5" /></span>
                <span className="min-w-0 flex-1">
                  <span className="flex items-center justify-between gap-2 text-sm font-semibold text-ink">
                    {title}
                    <ArrowRight className="size-4 text-ink-3 transition-transform group-hover:translate-x-0.5" aria-hidden />
                  </span>
                  <span className="mt-0.5 block text-xs text-ink-3">{text}</span>
                </span>
              </Card>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}

export function MyDashboard() {
  const { user } = useAuth();
  if (!user) return null;
  return (
    <div className="space-y-5">
      <TodayCard user={user} />
      {user.attendance_tracked ? <EmployeeAttendanceView employeeId={user.id} /> : <Shortcuts user={user} />}
    </div>
  );
}
