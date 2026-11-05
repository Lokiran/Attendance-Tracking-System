import { CorrectionsList } from "@/components/attendance/RequestLists";
import { PageHeader } from "@/components/ui/PageHeader";

export function ApprovalsPage() {
  return (
    <div className="space-y-5">
      <PageHeader
        title="Approvals"
        description="Review attendance corrections for people in your reporting scope. Decisions are recorded in the audit log."
      />
      <CorrectionsList scope="review" title="Attendance corrections" defaultStatus="PENDING" />
    </div>
  );
}
