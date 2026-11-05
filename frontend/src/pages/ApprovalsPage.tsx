import { CorrectionsList, LeaveList } from "@/components/attendance/RequestLists";
import { PageHeader } from "@/components/ui/PageHeader";

export function ApprovalsPage() {
  return (
    <div className="space-y-5">
      <PageHeader
        title="Approvals"
        description="Review attendance corrections and leave for people in your reporting scope. Decisions are recorded in the audit log."
      />
      <CorrectionsList scope="review" title="Attendance corrections" defaultStatus="PENDING" />
      <LeaveList scope="review" title="Leave requests" defaultStatus="PENDING" />
    </div>
  );
}
