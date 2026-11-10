import { useAuth } from "@/auth/AuthContext";
import { EmployeeAttendanceView } from "@/components/attendance/EmployeeAttendanceView";
import { TodayCard } from "@/components/attendance/TodayCard";

export function MyDashboard() {
  const { user } = useAuth();
  if (!user) return null;
  return (
    <div className="space-y-5">
      <TodayCard user={user} />
      <EmployeeAttendanceView employeeId={user.id} />
    </div>
  );
}
