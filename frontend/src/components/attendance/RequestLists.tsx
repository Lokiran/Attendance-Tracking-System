import { useMutation, useQuery } from "@tanstack/react-query";
import { useState } from "react";

import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, Pagination, type Column } from "@/components/ui/DataTable";
import { Field, Select, TextArea } from "@/components/ui/Fields";
import { ConfirmDialog, Modal } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/States";
import { RequestBadge } from "@/components/ui/StatusBadge";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import { formatDate, formatDateTime, formatMinutes, formatTime, titleCase } from "@/lib/format";
import { qk, useInvalidateAttendance } from "@/lib/queries";
import type { Correction, Leave, Page, RequestStatus } from "@/lib/types";

type Scope = "mine" | "review";

function minutesBetween(a: string, b: string, brk: number) {
  return Math.floor((new Date(b).getTime() - new Date(a).getTime()) / 60000) - brk;
}

function ReviewDialog({ kind, id, approve, onClose }: { kind: "corrections" | "leaves"; id: number | null; approve: boolean; onClose: () => void }) {
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const [comment, setComment] = useState("");
  const m = useMutation({
    mutationFn: () => api.post(`/${kind}/${id}/${approve ? "approve" : "reject"}`, { comment: comment || null }),
    onSuccess: () => {
      toast.success(approve ? "Request approved" : "Request rejected");
      invalidate();
      setComment("");
      onClose();
    },
    onError: (e) => toast.error(e),
  });
  const needsComment = !approve && !comment.trim();
  return (
    <Modal
      open={id !== null}
      onClose={onClose}
      size="sm"
      title={approve ? "Approve request?" : "Reject request?"}
      description={approve && kind === "corrections" ? "The original session is kept as history and a corrected session is created." : undefined}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button variant={approve ? "primary" : "danger"} loading={m.isPending} disabled={needsComment} onClick={() => m.mutate()}>
            {approve ? "Approve" : "Reject"}
          </Button>
        </>
      }
    >
      <Field label={approve ? "Comment (optional)" : "Reason for rejection"} htmlFor="review-comment" required={!approve} hint="Recorded with your name and the time in the audit log.">
        <TextArea id="review-comment" value={comment} onChange={(e) => setComment(e.target.value)} />
      </Field>
    </Modal>
  );
}

export function CorrectionsList({ scope, title, pageSize = 10, defaultStatus = "" }: {
  scope: Scope;
  title: string;
  pageSize?: number;
  defaultStatus?: RequestStatus | "";
}) {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<RequestStatus | "">(defaultStatus);
  const [review, setReview] = useState<{ id: number; approve: boolean } | null>(null);
  const [cancelId, setCancelId] = useState<number | null>(null);
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const q = useQuery({
    queryKey: [...qk.corrections, scope, status, page, pageSize],
    queryFn: () => api.get<Page<Correction>>("/corrections", { scope, status, page, page_size: pageSize }),
  });
  const cancel = useMutation({
    mutationFn: (id: number) => api.post(`/corrections/${id}/cancel`),
    onSuccess: () => {
      toast.success("Request cancelled");
      invalidate();
      setCancelId(null);
    },
    onError: (e) => toast.error(e),
  });

  const columns: Column<Correction>[] = [
    ...(scope === "review"
      ? [{ key: "emp", header: "Employee", cell: (c: Correction) => <span className="font-medium">{c.employee.full_name}<span className="block text-xs text-ink-3">{c.employee.employee_code}</span></span> }]
      : []),
    { key: "date", header: "Date", cell: (c) => formatDate(c.attendance_date, { weekday: "short" }) },
    { key: "type", header: "Type", cell: (c) => (c.request_type === "ADD_SESSION" ? "Add missing session" : "Fix session") },
    {
      key: "change",
      header: "Requested change",
      cell: (c) => (
        <span className="tabular text-xs">
          {formatTime(c.requested_check_in_at)} → {formatTime(c.requested_check_out_at)}
          {c.requested_break_minutes ? ` · ${c.requested_break_minutes}m break` : ""} ·{" "}
          {formatMinutes(minutesBetween(c.requested_check_in_at, c.requested_check_out_at, c.requested_break_minutes))}
          {c.original_values && (
            <span className="block text-ink-3">
              was {formatTime(String(c.original_values.check_in_at))} → {c.original_values.check_out_at ? formatTime(String(c.original_values.check_out_at)) : "no check-out"}
            </span>
          )}
        </span>
      ),
    },
    { key: "reason", header: "Reason", cell: (c) => <span className="line-clamp-2 max-w-64 text-xs text-ink-2">{c.reason}</span> },
    {
      key: "status",
      header: "Status",
      cell: (c) => (
        <div>
          <RequestBadge status={c.status} />
          {c.reviewed_by && (
            <div className="mt-1 text-[11px] text-ink-3">
              {c.reviewed_by.full_name} · {formatDateTime(c.reviewed_at)}
              {c.reviewer_comment && <span className="block italic">“{c.reviewer_comment}”</span>}
            </div>
          )}
        </div>
      ),
    },
    {
      key: "actions",
      header: "",
      cell: (c) =>
        c.status !== "PENDING" ? null : scope === "review" ? (
          <div className="flex gap-1">
            <Button size="sm" onClick={() => setReview({ id: c.id, approve: true })}>
              Approve
            </Button>
            <Button size="sm" variant="secondary" onClick={() => setReview({ id: c.id, approve: false })}>
              Reject
            </Button>
          </div>
        ) : (
          <Button size="sm" variant="ghost" onClick={() => setCancelId(c.id)}>
            Cancel
          </Button>
        ),
    },
  ];

  return (
    <Card>
      <CardHeader
        title={title}
        actions={
          <>
            <label htmlFor={"cs-" + scope} className="sr-only">
              Filter by status
            </label>
            <Select id={"cs-" + scope} className="w-36" value={status} onChange={(e) => { setStatus(e.target.value as RequestStatus | ""); setPage(1); }}>
              <option value="">All</option>
              {(["PENDING", "APPROVED", "REJECTED", "CANCELLED"] as const).map((s) => (
                <option key={s} value={s}>
                  {titleCase(s)}
                </option>
              ))}
            </Select>
          </>
        }
      />
      <DataTable
        caption={title}
        columns={columns}
        rows={q.data?.items}
        rowKey={(c) => c.id}
        loading={q.isFetching}
        error={q.error}
        onRetry={() => q.refetch()}
        empty={<EmptyState title={scope === "review" ? "Nothing to review" : "No correction requests"} description={scope === "review" ? "Requests from your team will appear here." : "Open a day in your calendar to request a correction."} />}
      />
      {q.data && <Pagination page={page} pageSize={pageSize} total={q.data.total} onPage={setPage} />}
      <ReviewDialog kind="corrections" id={review?.id ?? null} approve={review?.approve ?? true} onClose={() => setReview(null)} />
      <ConfirmDialog open={cancelId !== null} title="Cancel this request?" confirmLabel="Cancel request" danger loading={cancel.isPending} onCancel={() => setCancelId(null)} onConfirm={() => cancelId && cancel.mutate(cancelId)} />
    </Card>
  );
}

export function LeaveList({ scope, title, pageSize = 10, defaultStatus = "" }: { scope: Scope; title: string; pageSize?: number; defaultStatus?: RequestStatus | "" }) {
  const [page, setPage] = useState(1);
  const [status, setStatus] = useState<RequestStatus | "">(defaultStatus);
  const [review, setReview] = useState<{ id: number; approve: boolean } | null>(null);
  const [cancelId, setCancelId] = useState<number | null>(null);
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const q = useQuery({
    queryKey: [...qk.leaves, scope, status, page, pageSize],
    queryFn: () => api.get<Page<Leave>>("/leaves", { scope, status, page, page_size: pageSize }),
  });
  const cancel = useMutation({
    mutationFn: (id: number) => api.post(`/leaves/${id}/cancel`),
    onSuccess: () => {
      toast.success("Leave request cancelled");
      invalidate();
      setCancelId(null);
    },
    onError: (e) => toast.error(e),
  });
  const columns: Column<Leave>[] = [
    ...(scope === "review" ? [{ key: "emp", header: "Employee", cell: (l: Leave) => <span className="font-medium">{l.employee.full_name}</span> }] : []),
    { key: "dates", header: "Dates", cell: (l) => (l.start_date === l.end_date ? formatDate(l.start_date) : `${formatDate(l.start_date)} – ${formatDate(l.end_date)}`) },
    { key: "type", header: "Type", cell: (l) => titleCase(l.leave_type) },
    { key: "reason", header: "Reason", cell: (l) => <span className="text-xs text-ink-2">{l.reason ?? "—"}</span> },
    {
      key: "status",
      header: "Status",
      cell: (l) => (
        <div>
          <RequestBadge status={l.status} />
          {l.reviewed_by && <div className="mt-1 text-[11px] text-ink-3">{l.reviewed_by.full_name}{l.reviewer_comment && ` · “${l.reviewer_comment}”`}</div>}
        </div>
      ),
    },
    {
      key: "actions",
      header: "",
      cell: (l) =>
        l.status !== "PENDING" ? null : scope === "review" ? (
          <div className="flex gap-1">
            <Button size="sm" onClick={() => setReview({ id: l.id, approve: true })}>Approve</Button>
            <Button size="sm" variant="secondary" onClick={() => setReview({ id: l.id, approve: false })}>Reject</Button>
          </div>
        ) : (
          <Button size="sm" variant="ghost" onClick={() => setCancelId(l.id)}>Cancel</Button>
        ),
    },
  ];
  return (
    <Card>
      <CardHeader
        title={title}
        actions={
          <>
            <label htmlFor={"ls-" + scope} className="sr-only">Filter by status</label>
            <Select id={"ls-" + scope} className="w-36" value={status} onChange={(e) => { setStatus(e.target.value as RequestStatus | ""); setPage(1); }}>
              <option value="">All</option>
              {(["PENDING", "APPROVED", "REJECTED", "CANCELLED"] as const).map((s) => (
                <option key={s} value={s}>{titleCase(s)}</option>
              ))}
            </Select>
          </>
        }
      />
      <DataTable caption={title} columns={columns} rows={q.data?.items} rowKey={(l) => l.id} loading={q.isFetching} error={q.error} onRetry={() => q.refetch()} empty={<EmptyState title="No leave records" />} />
      {q.data && <Pagination page={page} pageSize={pageSize} total={q.data.total} onPage={setPage} />}
      <ReviewDialog kind="leaves" id={review?.id ?? null} approve={review?.approve ?? true} onClose={() => setReview(null)} />
      <ConfirmDialog open={cancelId !== null} title="Cancel this leave request?" confirmLabel="Cancel request" danger loading={cancel.isPending} onCancel={() => setCancelId(null)} onConfirm={() => cancelId && cancel.mutate(cancelId)} />
    </Card>
  );
}
