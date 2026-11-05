import { useQuery } from "@tanstack/react-query";
import { CalendarPlus, ClipboardPlus } from "lucide-react";
import { useState } from "react";

import { CorrectionFormModal, LeaveFormModal } from "@/components/attendance/RequestForms";
import { CorrectionsList, LeaveList } from "@/components/attendance/RequestLists";
import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { PageHeader } from "@/components/ui/PageHeader";
import { TableSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { api } from "@/lib/api";
import { addDays, todayInOffice } from "@/lib/dates";
import { formatDate } from "@/lib/format";
import type { Holiday } from "@/lib/types";

export function MyRequests() {
  const [correction, setCorrection] = useState(false);
  const [leave, setLeave] = useState(false);
  const today = todayInOffice();
  const holidays = useQuery({
    queryKey: ["holidays", today],
    queryFn: () => api.get<Holiday[]>("/holidays", { start: addDays(today, -30), end: addDays(today, 180) }),
  });
  return (
    <div className="space-y-5">
      <PageHeader
        title="Requests & leave"
        description="Ask for attendance corrections, request leave and track their status."
        actions={
          <>
            <Button variant="secondary" icon={<ClipboardPlus className="size-4" />} onClick={() => setCorrection(true)}>
              Add missing attendance
            </Button>
            <Button icon={<CalendarPlus className="size-4" />} onClick={() => setLeave(true)}>
              Request leave
            </Button>
          </>
        }
      />
      <CorrectionsList scope="mine" title="Correction requests" />
      <div className="grid gap-5 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <LeaveList scope="mine" title="Leave" />
        </div>
        <Card>
          <CardHeader title="Holidays" subtitle="Company and department holidays that apply to you" />
          {holidays.isError ? (
            <ErrorState error={holidays.error} onRetry={() => holidays.refetch()} />
          ) : !holidays.data ? (
            <TableSkeleton rows={3} />
          ) : holidays.data.length === 0 ? (
            <EmptyState title="No holidays in this window" />
          ) : (
            <ul className="divide-y divide-line">
              {holidays.data.map((h) => (
                <li key={h.id} className="flex items-center justify-between px-5 py-2.5 text-sm">
                  <span className="text-ink">{h.name}</span>
                  <span className={"tabular text-xs " + (h.holiday_date < today ? "text-ink-3" : "text-ink-2")}>{formatDate(h.holiday_date, { weekday: "short" })}</span>
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>
      <CorrectionFormModal open={correction} onClose={() => setCorrection(false)} date={null} sessionId={null} />
      <LeaveFormModal open={leave} onClose={() => setLeave(false)} />
    </div>
  );
}
