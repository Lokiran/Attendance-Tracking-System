import { useMutation, useQuery } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, FileUp, Trash2 } from "lucide-react";
import { useRef, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { Checkbox } from "@/components/ui/Fields";
import { PageHeader } from "@/components/ui/PageHeader";
import { EmptyState } from "@/components/ui/States";
import { CardGrid, SummaryCard } from "@/components/ui/SummaryCard";
import { useToast } from "@/components/ui/Toast";
import { MODE_META } from "@/components/wfo/WfoParts";
import { api } from "@/lib/api";
import { formatDate, formatDateTime } from "@/lib/format";
import { useInvalidateAttendance } from "@/lib/queries";
import type { AttendanceUpload, UploadConflict, WorkMode } from "@/lib/types";

const KEY = ["admin", "attendance-uploads"];

function Preview({ upload, onDone }: { upload: AttendanceUpload; onDone: (u: AttendanceUpload) => void }) {
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const [overwrite, setOverwrite] = useState(false);
  const s = upload.summary!;
  const commit = useMutation({
    mutationFn: () => api.post<AttendanceUpload>(`/admin/attendance-uploads/${upload.id}/commit`, { overwrite_overrides: overwrite }),
    onSuccess: (u) => { toast.success(`Imported ${u.commit_summary?.applied_days ?? 0} day(s)`); invalidate(); onDone(u); },
    onError: (e) => toast.error(e),
  });
  const discard = useMutation({
    mutationFn: () => api.post<AttendanceUpload>(`/admin/attendance-uploads/${upload.id}/discard`),
    onSuccess: (u) => { toast.success("Upload discarded"); onDone(u); },
    onError: (e) => toast.error(e),
  });
  const conflictColumns: Column<UploadConflict>[] = [
    { key: "date", header: "Date", cell: (c) => formatDate(c.date, { weekday: "short" }) },
    { key: "emp", header: "Employee", cell: (c) => <span>{c.full_name}<span className="block text-xs text-ink-3">{c.employee_code}</span></span> },
    { key: "c", header: "Conflict (resolved as office)", cell: (c) => <span className="text-xs text-ink-2">{c.conflicts.join("; ")}</span> },
    { key: "rows", header: "File rows", cell: (c) => <span className="tabular text-xs text-ink-3">{c.rows.join(", ")}</span> },
  ];
  const errorColumns: Column<{ row: number; employee: string | null; error: string }>[] = [
    { key: "row", header: "Row", align: "right", cell: (e) => e.row },
    { key: "emp", header: "Employee", cell: (e) => e.employee ?? "—" },
    { key: "err", header: "Problem", cell: (e) => <span className="text-xs text-critical-text">{e.error}</span> },
  ];
  const isPreview = upload.status === "PREVIEW";
  return (
    <Card>
      <CardHeader
        title={`${upload.filename} · ${isPreview ? "preview" : upload.status.toLowerCase()}`}
        subtitle={s.date_from ? `${formatDate(s.date_from)} – ${formatDate(s.date_to!)} · ${s.employees} employee(s), ${s.employee_days} employee-day(s)` : "No valid rows"}
      />
      <div className="space-y-5 p-5">
        <CardGrid cols={6}>
          <SummaryCard label="Rows" value={s.rows} hint={`${s.valid_rows} valid, ${s.error_rows} with problems`} />
          <SummaryCard label="Office (WFO)" value={s.office} hint={s.office_below_minimum ? `${s.office_below_minimum} below the minimum hours` : "days classified as office"} />
          <SummaryCard label="WFH" value={s.wfh} hint="days classified as WFH" />
          <SummaryCard label="Conflicts" value={s.conflict_count} hint="office + WFH signals: marked office" />
          <SummaryCard label="Badge rows" value={s.badge_rows} hint={s.duplicate_rows ? `${s.duplicate_rows} duplicate row(s): later wins` : "swipe / access rows"} />
          <SummaryCard label="New days" value={s.new_days} hint={`${s.employee_days - s.new_days} already had data`} />
        </CardGrid>
        <ul className="flex flex-wrap gap-x-5 gap-y-1 text-xs text-ink-2" aria-label="Classified days by work mode">
          {Object.entries(s.by_work_mode).map(([mode, n]) => (
            <li key={mode} className="flex items-center gap-1.5">
              <span className="inline-block size-2.5 rounded-sm" style={{ background: MODE_META[mode as WorkMode]?.color ?? "var(--st-pending)" }} aria-hidden />
              {MODE_META[mode as WorkMode]?.label ?? "No work mode"}: <span className="tabular font-medium text-ink">{n}</span>
            </li>
          ))}
        </ul>
        {upload.commit_summary && (
          <p className="flex items-center gap-2 text-sm text-ink"><CheckCircle2 className="size-4" style={{ color: "var(--good)" }} aria-hidden />
            Applied {upload.commit_summary.applied_days} day(s) ({upload.commit_summary.created_days} new); skipped {upload.commit_summary.skipped_overrides} overridden day(s)
            {upload.commit_summary.overrides_overwritten ? `; replaced ${upload.commit_summary.overrides_overwritten} override(s)` : ""}.
          </p>
        )}
        {s.conflicts.length > 0 && (
          <div>
            <h3 className="mb-2 flex items-center gap-1.5 text-sm font-semibold text-ink"><AlertTriangle className="size-4" style={{ color: "var(--st-incomplete)" }} aria-hidden />Conflicting signals</h3>
            <DataTable caption="Conflicts" columns={conflictColumns} rows={s.conflicts} rowKey={(c) => c.employee_code + c.date} />
          </div>
        )}
        {s.errors.length > 0 && (
          <div>
            <h3 className="mb-2 text-sm font-semibold text-ink">Rows that will not be imported{s.error_rows > s.errors.length ? ` (first ${s.errors.length} of ${s.error_rows})` : ""}</h3>
            <DataTable caption="Row problems" columns={errorColumns} rows={s.errors} rowKey={(e) => e.row} />
          </div>
        )}
        {isPreview && (
          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-line pt-4">
            {s.protected_overrides > 0 ? (
              <Checkbox label={`Overwrite ${s.protected_overrides} day(s) an admin overrode (otherwise they are skipped)`} checked={overwrite} onChange={(e) => setOverwrite(e.target.checked)} />
            ) : <span className="text-xs text-ink-3">Days are upserted per employee and date; re-importing a file replaces its earlier values.</span>}
            <div className="flex gap-2">
              <Button variant="ghost" icon={<Trash2 className="size-4" />} loading={discard.isPending} onClick={() => discard.mutate()}>Discard</Button>
              <Button loading={commit.isPending} disabled={!s.valid_rows} onClick={() => commit.mutate()}>Import {s.employee_days} day(s)</Button>
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}

export function AttendanceImportPage() {
  const toast = useToast();
  const fileRef = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [current, setCurrent] = useState<AttendanceUpload | null>(null);
  const history = useQuery({ queryKey: KEY, queryFn: () => api.get<AttendanceUpload[]>("/admin/attendance-uploads") });
  const upload = useMutation({
    mutationFn: (f: File) => api.upload<AttendanceUpload>("/admin/attendance-uploads", f),
    onSuccess: (u) => { setCurrent(u); setFile(null); if (fileRef.current) fileRef.current.value = ""; history.refetch(); },
    onError: (e) => toast.error(e),
  });
  const columns: Column<AttendanceUpload>[] = [
    { key: "file", header: "File", cell: (u) => <button type="button" className="font-medium text-ink hover:text-brand-text hover:underline" onClick={() => setCurrent(u)}>{u.filename}</button> },
    { key: "status", header: "Status", cell: (u) => u.status.charAt(0) + u.status.slice(1).toLowerCase() },
    { key: "rows", header: "Rows", align: "right", cell: (u) => `${u.row_count}${u.error_count ? ` (${u.error_count} with problems)` : ""}` },
    { key: "by", header: "Uploaded by", cell: (u) => u.uploaded_by_name ?? "—" },
    { key: "at", header: "Uploaded", cell: (u) => formatDateTime(u.created_at) },
  ];
  return (
    <div className="space-y-5">
      <PageHeader title="Import attendance" description="Upload an attendance or badge / swipe export (CSV or Excel). You see a preview first; nothing changes until you import." />
      <Card className="p-5">
        <div className="flex flex-wrap items-end gap-3">
          <div className="min-w-64 flex-1">
            <label htmlFor="wfo-file" className="mb-1.5 block text-sm font-medium text-ink">File</label>
            <input ref={fileRef} id="wfo-file" type="file" accept=".csv,.xlsx" onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              className="block w-full rounded-lg border border-line bg-surface text-sm text-ink file:mr-3 file:border-0 file:bg-surface-2 file:px-3 file:py-2 file:text-sm file:font-medium file:text-ink" />
          </div>
          <Button icon={<FileUp className="size-4" />} disabled={!file} loading={upload.isPending} onClick={() => file && upload.mutate(file)}>Upload and preview</Button>
        </div>
        <details className="mt-4 text-xs text-ink-2">
          <summary className="cursor-pointer font-medium text-ink">Expected columns</summary>
          <ul className="mt-2 list-disc space-y-1 pl-5">
            <li><b>Employee</b> (employee code or email) and <b>Date</b> (31/03/2026, 2026-03-31 or an Excel date): required.</li>
            <li><b>Work mode</b> / location: WFO, Office, On-site → office; WFH, Remote → home; also Leave, Holiday, Week off.</li>
            <li><b>Hours</b> (7.5 or 7:30) or <b>In</b> / <b>Out</b> times. A day counts as WFO only with enough office hours.</li>
            <li><b>Source</b>: rows marked Badge, Swipe or Access card are badge evidence (office).</li>
            <li>Accepted header names and values are configured in <code>backend/config/wfo_mapping.json</code>. Examples: <code>samples/</code> in the repository.</li>
          </ul>
        </details>
      </Card>
      {current?.summary && <Preview key={current.id + current.status} upload={current} onDone={(u) => { setCurrent(u); history.refetch(); }} />}
      <Card>
        <CardHeader title="Recent uploads" />
        <DataTable caption="Recent uploads" columns={columns} rows={history.data} rowKey={(u) => u.id} loading={history.isLoading} error={history.error}
          onRetry={() => history.refetch()} empty={<EmptyState title="No uploads yet" />} />
      </Card>
    </div>
  );
}
