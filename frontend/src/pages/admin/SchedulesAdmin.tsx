import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";

import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { Field, Input, Select } from "@/components/ui/Fields";
import { ConfirmDialog, Modal } from "@/components/ui/Modal";
import { PageHeader } from "@/components/ui/PageHeader";
import { PersonPicker } from "@/components/ui/PersonPicker";
import { EmptyState } from "@/components/ui/States";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import { todayInOffice } from "@/lib/dates";
import { formatDate, formatMinutes } from "@/lib/format";
import { qk, useInvalidateAttendance } from "@/lib/queries";
import type { Department, Schedule } from "@/lib/types";

const DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

const schema = z
  .object({
    name: z.string().trim().min(2, "Name is required").max(100),
    target: z.enum(["org", "department", "employee"]),
    target_id: z.string(),
    start_time: z.string().regex(/^\d{2}:\d{2}/, "Required"),
    end_time: z.string().regex(/^\d{2}:\d{2}/, "Required"),
    required_minutes: z.number({ message: "Enter minutes" }).int().min(1).max(1440),
    working_days: z.array(z.number()).min(1, "Pick at least one working day"),
    effective_from: z.string().min(1, "Required"),
    effective_to: z.string(),
  })
  .refine((v) => v.target === "org" || v.target_id, { path: ["target_id"], message: "Choose who this schedule applies to" })
  .refine((v) => !v.effective_to || v.effective_to >= v.effective_from, { path: ["effective_to"], message: "Must be on or after the start date" })
  .refine((v) => v.start_time !== v.end_time, { path: ["end_time"], message: "Start and end cannot be equal" });
type Values = z.infer<typeof schema>;

function ScheduleModal({ schedule, open, onClose }: { schedule: Schedule | null; open: boolean; onClose: () => void }) {
  const toast = useToast();
  const qc = useQueryClient();
  const invalidate = useInvalidateAttendance();
  const departments = useQuery({ queryKey: ["departments"], queryFn: () => api.get<Department[]>("/departments"), enabled: open });
  const form = useForm<Values>({ resolver: zodResolver(schema) });
  useEffect(() => {
    if (!open) return;
    form.reset({
      name: schedule?.name ?? "",
      target: schedule?.employee_id ? "employee" : schedule?.department_id ? "department" : "org",
      target_id: String(schedule?.employee_id ?? schedule?.department_id ?? ""),
      start_time: schedule?.start_time.slice(0, 5) ?? "09:30",
      end_time: schedule?.end_time.slice(0, 5) ?? "18:30",
      required_minutes: schedule?.required_minutes ?? 480,
      working_days: schedule?.working_days ?? [1, 2, 3, 4, 5],
      effective_from: schedule?.effective_from ?? todayInOffice(),
      effective_to: schedule?.effective_to ?? "",
    });
  }, [open, schedule]); // eslint-disable-line react-hooks/exhaustive-deps
  const target = form.watch("target");
  const days = form.watch("working_days") ?? [];
  const m = useMutation({
    mutationFn: (v: Values) => {
      const common = {
        name: v.name,
        start_time: v.start_time,
        end_time: v.end_time,
        required_minutes: v.required_minutes,
        working_days: v.working_days,
        effective_from: v.effective_from,
        effective_to: v.effective_to || null,
      };
      if (schedule) return api.patch<Schedule>("/admin/schedules/" + schedule.id, common);
      return api.post<Schedule>("/admin/schedules", {
        ...common,
        employee_id: v.target === "employee" ? Number(v.target_id) : null,
        department_id: v.target === "department" ? Number(v.target_id) : null,
      });
    },
    onSuccess: () => {
      toast.success("Schedule saved; attendance figures now use it");
      qc.invalidateQueries({ queryKey: [...qk.admin, "schedules"] });
      invalidate();
      onClose();
    },
    onError: (e) => toast.error(e),
  });
  const { errors } = form.formState;
  return (
    <Modal open={open} onClose={onClose} size="lg" title={schedule ? "Edit schedule" : "New schedule"} description="Employee schedules override department schedules, which override the organisation default."
      footer={<><Button variant="secondary" onClick={onClose}>Cancel</Button><Button type="submit" form="schedule-form" loading={m.isPending}>Save schedule</Button></>}>
      <form id="schedule-form" noValidate onSubmit={form.handleSubmit((v) => m.mutate(v))} className="grid gap-4 sm:grid-cols-2">
        <Field label="Name" htmlFor="s-name" error={errors.name?.message} required>
          <Input id="s-name" {...form.register("name")} />
        </Field>
        <Field label="Applies to" htmlFor="s-target">
          <Select id="s-target" {...form.register("target")} disabled={!!schedule}>
            <option value="org">Whole organisation (default)</option>
            <option value="department">A department</option>
            <option value="employee">An employee</option>
          </Select>
        </Field>
        {target !== "org" && (
          <Field label={target === "department" ? "Department" : "Employee"} htmlFor="s-tid" error={errors.target_id?.message} required>
            {target === "department" ? (
              <Select id="s-tid" {...form.register("target_id")} disabled={!!schedule}>
                <option value="">Select…</option>
                {departments.data?.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
              </Select>
            ) : (
              <PersonPicker
                id="s-tid"
                source="admin"
                value={form.watch("target_id") ?? ""}
                onChange={(id) => form.setValue("target_id", id, { shouldValidate: true })}
                selectedLabel={schedule?.employee_id ? schedule.target_label.replace(/^Employee: /, "") : undefined}
                disabled={!!schedule}
              />
            )}
          </Field>
        )}
        <Field label="Start time" htmlFor="s-start" error={errors.start_time?.message} required>
          <Input id="s-start" type="time" {...form.register("start_time")} />
        </Field>
        <Field label="End time" htmlFor="s-end" error={errors.end_time?.message} hint="Earlier than the start time means an overnight shift." required>
          <Input id="s-end" type="time" {...form.register("end_time")} />
        </Field>
        <Field label="Required net minutes per day" htmlFor="s-req" error={errors.required_minutes?.message} required>
          <Input id="s-req" type="number" min={1} max={1440} {...form.register("required_minutes", { valueAsNumber: true })} />
        </Field>
        <Field label="Effective from" htmlFor="s-from" error={errors.effective_from?.message} required>
          <Input id="s-from" type="date" {...form.register("effective_from")} />
        </Field>
        <Field label="Effective to (optional)" htmlFor="s-to" error={errors.effective_to?.message}>
          <Input id="s-to" type="date" {...form.register("effective_to")} />
        </Field>
        <fieldset className="sm:col-span-2">
          <legend className="mb-1 text-xs font-medium text-ink-2">Working days</legend>
          <div className="flex flex-wrap gap-1.5">
            {DAYS.map((label, i) => {
              const d = i + 1;
              const on = days.includes(d);
              return (
                <button
                  key={d}
                  type="button"
                  aria-pressed={on}
                  onClick={() => form.setValue("working_days", on ? days.filter((x) => x !== d) : [...days, d].sort(), { shouldValidate: true })}
                  className={"rounded-lg border px-3 py-1.5 text-xs font-medium " + (on ? "border-brand bg-brand-soft text-brand-text" : "border-line text-ink-2 hover:bg-surface-2")}
                >
                  {label}
                </button>
              );
            })}
          </div>
          {errors.working_days && <p className="mt-1 text-xs text-critical-text" role="alert">{errors.working_days.message}</p>}
        </fieldset>
      </form>
    </Modal>
  );
}

export function SchedulesAdmin() {
  const toast = useToast();
  const qc = useQueryClient();
  const invalidate = useInvalidateAttendance();
  const q = useQuery({ queryKey: [...qk.admin, "schedules"], queryFn: () => api.get<Schedule[]>("/admin/schedules") });
  const [editing, setEditing] = useState<Schedule | null>(null);
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<Schedule | null>(null);
  const del = useMutation({
    mutationFn: (s: Schedule) => api.del("/admin/schedules/" + s.id),
    onSuccess: () => {
      toast.success("Schedule deleted");
      qc.invalidateQueries({ queryKey: [...qk.admin, "schedules"] });
      invalidate();
      setDeleting(null);
    },
    onError: (e) => toast.error(e),
  });
  const columns: Column<Schedule>[] = [
    { key: "name", header: "Schedule", cell: (s) => <span className="font-medium">{s.name}</span> },
    { key: "target", header: "Applies to", cell: (s) => s.target_label },
    { key: "hours", header: "Hours", cell: (s) => <span className="tabular">{s.start_time.slice(0, 5)}–{s.end_time.slice(0, 5)}{s.end_time < s.start_time && " (overnight)"}</span> },
    { key: "req", header: "Required", cell: (s) => formatMinutes(s.required_minutes) },
    { key: "days", header: "Working days", cell: (s) => s.working_days.map((d) => DAYS[d - 1]).join(", ") },
    { key: "eff", header: "Effective", cell: (s) => formatDate(s.effective_from) + " → " + (s.effective_to ? formatDate(s.effective_to) : "open") },
    {
      key: "a",
      header: "",
      cell: (s) => (
        <div className="flex justify-end gap-1">
          <Button size="sm" variant="ghost" icon={<Pencil className="size-3.5" />} onClick={() => setEditing(s)} aria-label={"Edit " + s.name} />
          <Button size="sm" variant="ghost" icon={<Trash2 className="size-3.5" />} onClick={() => setDeleting(s)} aria-label={"Delete " + s.name} />
        </div>
      ),
    },
  ];
  return (
    <div className="space-y-5">
      <PageHeader title="Work schedules" description="Working hours, required minutes and working days used for attendance calculations." actions={<Button icon={<Plus className="size-4" />} onClick={() => setCreating(true)}>New schedule</Button>} />
      <Card>
        <DataTable caption="Work schedules" columns={columns} rows={q.data} rowKey={(s) => s.id} loading={q.isFetching} error={q.error} onRetry={() => q.refetch()} empty={<EmptyState title="No schedules" description="Without a schedule, days are treated as not scheduled and no attendance is expected." />} />
      </Card>
      <ScheduleModal open={creating || !!editing} schedule={editing} onClose={() => { setCreating(false); setEditing(null); }} />
      <ConfirmDialog open={!!deleting} title={"Delete " + (deleting?.name ?? "") + "?"} message="Attendance for affected days will be recalculated without this schedule." confirmLabel="Delete" danger loading={del.isPending} onCancel={() => setDeleting(null)} onConfirm={() => deleting && del.mutate(deleting)} />
    </div>
  );
}
