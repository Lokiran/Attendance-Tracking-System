import { useState } from "react";
import { Link } from "react-router-dom";

import { useAuth } from "@/auth/AuthContext";
import { AttendanceActionCard } from "@/components/attendance/AttendanceActionCard";
import { EmployeeAttendanceView } from "@/components/attendance/EmployeeAttendanceView";
import { CorrectionFormModal } from "@/components/attendance/RequestForms";
import { CorrectionsList } from "@/components/attendance/RequestLists";

export function MyDashboard() {
  const { user } = useAuth();
  const [fix, setFix] = useState<{ date: string | null; sessionId: number | null } | null>(null);
  if (!user) return null;

  return (
    <div className="space-y-5">
      <AttendanceActionCard
        user={user}
        onRequestCorrection={(date, sessionId) => setFix({ date, sessionId })}
        onFixPastDay={() => setFix({ date: null, sessionId: null })}
      />

      <EmployeeAttendanceView employeeId={user.id} isSelf />

      <div>
        <div className="mb-2 flex items-center justify-between">
          <h2 className="text-sm font-semibold text-ink">My correction requests</h2>
          <Link to="/my/requests" className="text-xs font-medium text-brand-text hover:underline">
            All my requests →
          </Link>
        </div>
        <CorrectionsList scope="mine" title="Recent correction requests" pageSize={5} />
      </div>

      <CorrectionFormModal open={!!fix} onClose={() => setFix(null)} date={fix?.date ?? null} sessionId={fix?.sessionId ?? null} />
    </div>
  );
}
