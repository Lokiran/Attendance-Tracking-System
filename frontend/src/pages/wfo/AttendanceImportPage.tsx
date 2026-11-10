import { useMutation, useQuery } from "@tanstack/react-query";
import { AlertTriangle, CheckCircle2, FileUp, Info, Trash2, UserPlus } from "lucide-react";
import { useRef, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { Checkbox, Select } from "@/components/ui/Fields";
import { PageHeader } from "@/components/ui/PageHeader";
import { EmptyState } from "@/components/ui/States";
import { CardGrid, SummaryCard } from "@/components/ui/SummaryCard";
import { useToast } from "@/components/ui/Toast";
import { MODE_META } from "@/components/wfo/WfoParts";
import { api } from "@/lib/api";
import { formatDate, formatDateTime } from "@/lib/format";
import { useInvalidateAttendance } from "@/lib/queries";
import type { AttendanceUpload, NameMismatch, UnknownEmployee, UploadConflict, WorkMode } from "@/lib/types";

const KEY = ["admin", "attendance-uploads"];
const UNKNOWN = "unknown employee ";

/** How to read the file again: another sheet, or a presence list as WFH instead of office. */
export interface ReadOptions { sheet?: string; presence_mode?: "OFFICE" | "WFH" }

function Preview({ upload, onDone, onReread, rereading }: {
  upload: AttendanceUpload;
  onDone: (u: AttendanceUpload) => void;
  /** Present while the uploaded file is still selected in this page (needed to read it again). */
  onReread?: (opts: ReadOptions) => void;
  rereading: boolean;
}) {
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const [overwrite, setOverwrite] = useState(false);
  const s = upload.summary!;
  const unknown = s.unknown_employees ?? [];
  const addable = unknown.filter((u) => u.can_add);
  const unknownRows = unknown.reduce((n, u) => n + u.rows, 0);
  const mismatches = s.name_mismatches ?? [];
  // Unknown IDs have their own list (older uploads do not, so their errors are all shown as rows).
  const otherErrors = s.unknown_employees ? s.errors.filter((e) => !e.error.startsWith(UNKNOWN)) : s.errors;
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
  const addPeople = useMutation({
    mutationFn: () => api.post<AttendanceUpload>(`/admin/attendance-uploads/${upload.id}/create-employees`, {}),
    onSuccess: (u) => { toast.success(`Added ${addable.length} employee(s); the file was checked again`); invalidate(); onDone(u); },
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
  const unknownColumns: Column<UnknownEmployee>[] = [
    { key: "id", header: "Employee ID", cell: (u) => <span className="font-medium text-ink">{u.employee_ref}</span> },
    { key: "name", header: "Name in the file", cell: (u) => <span>{u.name ?? "—"}{u.other_names.length > 0 && <span className="block text-xs text-ink-3">also: {u.other_names.join(", ")}</span>}</span> },
    { key: "rows", header: "Rows", align: "right", cell: (u) => <span className="tabular">{u.rows}</span> },
    { key: "first", header: "First date", cell: (u) => (u.first_date ? formatDate(u.first_date) : "—") },
    { key: "add", header: "Can be added", cell: (u) => (u.can_add ? "Yes" : <span className="text-xs text-critical-text">No: {u.reason}</span>) },
  ];
  const mismatchColumns: Column<NameMismatch>[] = [
    { key: "id", header: "Employee ID", cell: (m) => m.employee_ref },
    { key: "file", header: "Name in the file", cell: (m) => m.file_name },
    { key: "app", header: "Name in the app", cell: (m) => m.app_name },
  ];
  const isPreview = upload.status === "PREVIEW";
  const sheets = s.sheets ?? [];
  const busy = rereading || addPeople.isPending || commit.isPending || discard.isPending;
  return (
    <Card>
      <CardHeader
        title={`${upload.filename} · ${isPreview ? "preview" : upload.status.toLowerCase()}`}
        subtitle={s.date_from ? `${formatDate(s.date_from)} – ${formatDate(s.date_to!)} · ${s.employees} employee(s), ${s.employee_days} employee-day(s)` : "No valid rows yet"}
      />
      <div className="space-y-5 p-5">
        {(sheets.length > 1 || s.presence_list) && (
          <div className="flex flex-wrap items-end gap-x-6 gap-y-3 rounded-lg bg-surface-2 p-4 text-sm text-ink-2">
            {sheets.length > 1 && (
              <div>
                <label htmlFor="upload-sheet" className="mb-1.5 block text-xs font-medium text-ink">Sheet read</label>
                <Select id="upload-sheet" value={s.sheet ?? ""} disabled={!isPreview || !onReread || busy}
                  onChange={(e) => onReread?.({ sheet: e.target.value, presence_mode: s.presence_mode ?? undefined })}>
                  {sheets.map((sh) => (
                    <option key={sh.name} value={sh.name} disabled={!sh.usable}>
                      {sh.name}{sh.usable ? ` (${sh.rows} rows)` : " (no employee + date columns)"}
                    </option>
                  ))}
                </Select>
              </div>
            )}
            {s.presence_list && (
              <div>
                <label htmlFor="upload-presence" className="mb-1.5 block text-xs font-medium text-ink">Each listed day counts as</label>
                <Select id="upload-presence" value={s.presence_mode ?? "OFFICE"} disabled={!isPreview || !onReread || busy}
                  onChange={(e) => onReread?.({ sheet: s.sheet ?? undefined, presence_mode: e.target.value as "OFFICE" | "WFH" })}>
                  <option value="OFFICE">Office (WFO)</option>
                  <option value="WFH">Work from home</option>
                </Select>
              </div>
            )}
            <p className="max-w-xl flex-1 text-xs">
              {s.presence_list ? "This file has no work mode column, so it is read as a list of days each person was present. Days without hours still count as WFO days." : null}
              {isPreview && !onReread ? " Choose the file again above to read it differently." : null}
            </p>
          </div>
        )}
        <CardGrid cols={6}>
          <SummaryCard label="Rows" value={s.rows} hint={`${s.valid_rows} valid, ${s.error_rows} with problems`} />
          <SummaryCard label="Office (WFO)" value={s.office} hint={s.office_below_minimum ? `${s.office_below_minimum} below the minimum hours` : "days classified as office"} />
          <SummaryCard label="WFH" value={s.wfh} hint="days classified as WFH" />
          <SummaryCard label="Conflicts" value={s.conflict_count} hint="office + WFH signals: marked office" />
          <SummaryCard label="Duplicates" value={s.duplicate_rows} hint={s.badge_rows ? `${s.badge_rows} badge row(s)` : "same person and day: counted once"} />
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
        {((s.date_notes ?? []).length > 0 || (s.employees_added ?? 0) > 0) && (
          <ul className="space-y-1 text-xs text-ink-2" aria-label="Notes about the file">
            {(s.employees_added ?? 0) > 0 && (
              <li className="flex gap-1.5"><CheckCircle2 className="mt-px size-3.5 shrink-0" style={{ color: "var(--good)" }} aria-hidden />
                {s.employees_added} employee(s) were added from this file. Set their email, manager and department on the Employees page.</li>
            )}
            {(s.date_notes ?? []).map((n) => (
              <li key={n} className="flex gap-1.5"><Info className="mt-px size-3.5 shrink-0 text-ink-3" aria-hidden />{n}</li>
            ))}
          </ul>
        )}
        {upload.commit_summary && (
          <p className="flex items-center gap-2 text-sm text-ink"><CheckCircle2 className="size-4" style={{ color: "var(--good)" }} aria-hidden />
            Applied {upload.commit_summary.applied_days} day(s) ({upload.commit_summary.created_days} new); skipped {upload.commit_summary.skipped_overrides} overridden day(s)
            {upload.commit_summary.overrides_overwritten ? `; replaced ${upload.commit_summary.overrides_overwritten} override(s)` : ""}.
            {upload.period_start && upload.period_end && ` This timesheet covers ${formatDate(upload.period_start)} – ${formatDate(upload.period_end)}.`}
          </p>
        )}
        {unknown.length > 0 && (
          <div>
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <h3 className="flex items-center gap-1.5 text-sm font-semibold text-ink">
                <AlertTriangle className="size-4" style={{ color: "var(--st-incomplete)" }} aria-hidden />
                {unknown.length} employee ID(s) are not in the app yet ({unknownRows} row(s))
              </h3>
              {isPreview && addable.length > 0 && (
                <Button icon={<UserPlus className="size-4" />} loading={addPeople.isPending} disabled={busy && !addPeople.isPending} onClick={() => addPeople.mutate()}>
                  Add {addable.length} employee(s) and re-check
                </Button>
              )}
            </div>
            <p className="mb-2 text-xs text-ink-2">
              Adding creates each person with the ID and name from the file, as an employee with a placeholder email and no password, from the 1st of the month of their first date.
              Their rows are then imported with everyone else's. Or add them on the Employees page and upload the file again.
            </p>
            <DataTable caption="Employee IDs not in the app" columns={unknownColumns} rows={unknown} rowKey={(u) => u.employee_ref} />
          </div>
        )}
        {mismatches.length > 0 && (
          <details className="text-sm">
            <summary className="cursor-pointer font-semibold text-ink">Names that differ from the app ({mismatches.length}): rows are matched by ID, so they still import</summary>
            <div className="mt-2"><DataTable caption="Name differences" columns={mismatchColumns} rows={mismatches} rowKey={(m) => m.employee_ref} /></div>
          </details>
        )}
        {s.conflicts.length > 0 && (
          <div>
            <h3 className="mb-2 flex items-center gap-1.5 text-sm font-semibold text-ink"><AlertTriangle className="size-4" style={{ color: "var(--st-incomplete)" }} aria-hidden />Conflicting signals</h3>
            <DataTable caption="Conflicts" columns={conflictColumns} rows={s.conflicts} rowKey={(c) => c.employee_code + c.date} />
          </div>
        )}
        {otherErrors.length > 0 && (
          <div>
            <h3 className="mb-2 text-sm font-semibold text-ink">Rows that will not be imported{s.error_rows - unknownRows > otherErrors.length ? ` (first ${otherErrors.length} of ${s.error_rows - unknownRows})` : ""}</h3>
            <DataTable caption="Row problems" columns={errorColumns} rows={otherErrors} rowKey={(e) => e.row} />
          </div>
        )}
        {isPreview && (
          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-line pt-4">
            {s.protected_overrides > 0 ? (
              <Checkbox label={`Overwrite ${s.protected_overrides} day(s) an admin overrode (otherwise they are skipped)`} checked={overwrite} onChange={(e) => setOverwrite(e.target.checked)} />
            ) : <span className="text-xs text-ink-3">Days are upserted per employee and date; re-importing a file replaces its earlier values.</span>}
            <div className="flex gap-2">
              <Button variant="ghost" icon={<Trash2 className="size-4" />} loading={discard.isPending} disabled={busy && !discard.isPending} onClick={() => discard.mutate()}>Discard</Button>
              <Button loading={commit.isPending} disabled={!s.valid_rows || (busy && !commit.isPending)} onClick={() => commit.mutate()}>Import {s.employee_days} day(s)</Button>
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
  // The file behind the preview on screen, kept so it can be read again (another sheet, WFH instead of office).
  const [previewFile, setPreviewFile] = useState<{ file: File; uploadId: number } | null>(null);
  const [current, setCurrent] = useState<AttendanceUpload | null>(null);
  const history = useQuery({ queryKey: KEY, queryFn: () => api.get<AttendanceUpload[]>("/admin/attendance-uploads") });
  const upload = useMutation({
    mutationFn: async ({ f, opts, replace }: { f: File; opts?: ReadOptions; replace?: number }) => {
      if (replace !== undefined) await api.post(`/admin/attendance-uploads/${replace}/discard`);
      return { f, u: await api.upload<AttendanceUpload>("/admin/attendance-uploads", f, { ...opts }) };
    },
    onSuccess: ({ f, u }) => {
      setCurrent(u);
      setPreviewFile({ file: f, uploadId: u.id });
      setFile(null);
      if (fileRef.current) fileRef.current.value = "";
      history.refetch();
    },
    onError: (e) => { toast.error(e); history.refetch(); },
  });
  const columns: Column<AttendanceUpload>[] = [
    { key: "file", header: "File", cell: (u) => <button type="button" className="font-medium text-ink hover:text-brand-text hover:underline" onClick={() => setCurrent(u)}>{u.filename}</button> },
    { key: "status", header: "Status", cell: (u) => u.status.charAt(0) + u.status.slice(1).toLowerCase() },
    { key: "rows", header: "Rows", align: "right", cell: (u) => `${u.row_count}${u.error_count ? ` (${u.error_count} with problems)` : ""}` },
    { key: "by", header: "Uploaded by", cell: (u) => u.uploaded_by_name ?? "—" },
    { key: "at", header: "Uploaded", cell: (u) => formatDateTime(u.created_at) },
  ];
  const canReread = current?.status === "PREVIEW" && previewFile?.uploadId === current.id;
  return (
    <div className="space-y-5">
      <PageHeader title="Import timesheets" description="Attendance comes from these imports: upload each month's timesheet (Excel, CSV or PDF). You see a preview first; nothing changes until you import." />
      <Card className="p-5">
        <div className="flex flex-wrap items-end gap-3">
          <div className="min-w-64 flex-1">
            <label htmlFor="wfo-file" className="mb-1.5 block text-sm font-medium text-ink">File</label>
            <input ref={fileRef} id="wfo-file" type="file" accept=".csv,.tsv,.txt,.xlsx,.xlsm,.xls,.pdf" onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              className="block w-full rounded-lg border border-line bg-surface text-sm text-ink file:mr-3 file:border-0 file:bg-surface-2 file:px-3 file:py-2 file:text-sm file:font-medium file:text-ink" />
          </div>
          <Button icon={<FileUp className="size-4" />} disabled={!file} loading={upload.isPending && !!file} onClick={() => file && upload.mutate({ f: file })}>Upload and preview</Button>
        </div>
        <details className="mt-4 text-xs text-ink-2">
          <summary className="cursor-pointer font-medium text-ink">Accepted files and columns</summary>
          <ul className="mt-2 list-disc space-y-1 pl-5">
            <li><b>Files:</b> Excel (.xlsx, .xls), CSV / TSV / TXT, or a <b>PDF</b> of the sheet (a PDF exported or printed from Excel; a scanned image cannot be read). Up to 5 MB.</li>
            <li>A committed timesheet <b>covers</b> its months, up to the day before the import, for the people in it: their scheduled days in that period that are not in the file are absent. Months not imported yet show “No timesheet”, not absent, and a partial file never affects anyone else.</li>
            <li><b>Employee ID</b> (or employee code / email) and <b>Date</b> (31/03/2026, 2026-03-31 or an Excel date): required. An <b>Employee</b> / name column next to the ID is used to check names.</li>
            <li>In a workbook, the month&apos;s sheet (like &quot;September 2026&quot;) is read, or else the first sheet with these columns; you can pick another sheet in the preview. The header may be below a title row, and a pivot table beside the data is ignored.</li>
            <li>With only ID and date, every listed day is an <b>office</b> day (you can switch to WFH in the preview). Dates with day and month swapped are read in the month the file covers.</li>
            <li><b>Work mode</b> / location: WFO, Office, On-site → office; WFH, Remote → home; also Leave, Holiday, Week off.</li>
            <li><b>Hours</b> (7.5, 7:30 or 07:30:00) or <b>In</b> / <b>Out</b> times. With hours, a day counts as WFO only with enough office hours.</li>
            <li><b>Source</b>: rows marked Badge, Swipe or Access card are badge evidence (office).</li>
            <li>Accepted header names and values are configured in <code>backend/config/wfo_mapping.json</code>. Examples: <code>samples/</code> in the repository.</li>
          </ul>
        </details>
      </Card>
      {current?.summary && (
        <Preview key={current.id + current.status + (current.summary.employees_added ?? 0)} upload={current} rereading={upload.isPending}
          onReread={canReread ? (opts) => upload.mutate({ f: previewFile!.file, opts, replace: current.id }) : undefined}
          onDone={(u) => { setCurrent(u); history.refetch(); }} />
      )}
      <Card>
        <CardHeader title="Recent uploads" />
        <DataTable caption="Recent uploads" columns={columns} rows={history.data} rowKey={(u) => u.id} loading={history.isLoading} error={history.error}
          onRetry={() => history.refetch()} empty={<EmptyState title="No uploads yet" />} />
      </Card>
    </div>
  );
}
