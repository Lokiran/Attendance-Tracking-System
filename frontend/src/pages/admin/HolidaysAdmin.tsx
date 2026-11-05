import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2 } from "lucide-react";
import { useState } from "react";

import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { Field, Input, Select } from "@/components/ui/Fields";
import { ConfirmDialog } from "@/components/ui/Modal";
import { PageHeader } from "@/components/ui/PageHeader";
import { EmptyState } from "@/components/ui/States";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import { formatDate } from "@/lib/format";
import { useInvalidateAttendance } from "@/lib/queries";
import type { Department, Holiday } from "@/lib/types";

export function HolidaysAdmin() {
  const toast = useToast();
  const qc = useQueryClient();
  const invalidate = useInvalidateAttendance();
  const [year, setYear] = useState(new Date().getFullYear());
  const holidays = useQuery({ queryKey: ["holidays", "admin", year], queryFn: () => api.get<Holiday[]>("/holidays", { start: `${year}-01-01`, end: `${year}-12-31` }) });
  const departments = useQuery({ queryKey: ["departments"], queryFn: () => api.get<Department[]>("/departments") });
  const [name, setName] = useState("");
  const [date, setDate] = useState("");
  const [dept, setDept] = useState("");
  const [deleting, setDeleting] = useState<Holiday | null>(null);
  const deptName = (id: number | null) => (id ? departments.data?.find((d) => d.id === id)?.name ?? "Department" : "Organisation-wide");
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ["holidays"] });
    invalidate();
  };
  const create = useMutation({
    mutationFn: () => api.post<Holiday>("/admin/holidays", { name: name.trim(), holiday_date: date, department_id: dept ? Number(dept) : null }),
    onSuccess: (h) => {
      toast.success("Added " + h.name);
      setName("");
      setDate("");
      refresh();
    },
    onError: (e) => toast.error(e),
  });
  const del = useMutation({
    mutationFn: (h: Holiday) => api.del("/admin/holidays/" + h.id),
    onSuccess: () => {
      toast.success("Holiday removed");
      setDeleting(null);
      refresh();
    },
    onError: (e) => toast.error(e),
  });
  const columns: Column<Holiday>[] = [
    { key: "date", header: "Date", cell: (h) => formatDate(h.holiday_date, { weekday: "short" }) },
    { key: "name", header: "Holiday", cell: (h) => <span className="font-medium">{h.name}</span> },
    { key: "scope", header: "Applies to", cell: (h) => deptName(h.department_id) },
    { key: "a", header: "", cell: (h) => <div className="flex justify-end"><Button size="sm" variant="ghost" icon={<Trash2 className="size-3.5" />} onClick={() => setDeleting(h)} aria-label={"Delete " + h.name} /></div> },
  ];
  const canAdd = name.trim().length >= 2 && !!date;
  return (
    <div className="space-y-5">
      <PageHeader title="Holidays" description="Holidays on scheduled working days are excluded from expected attendance." />
      <Card>
        <CardHeader title="Add holiday" />
        <form
          className="flex flex-wrap items-end gap-3 p-4"
          onSubmit={(e) => {
            e.preventDefault();
            if (canAdd) create.mutate();
          }}
        >
          <Field label="Name" htmlFor="h-name">
            <Input id="h-name" value={name} onChange={(e) => setName(e.target.value)} className="w-56" />
          </Field>
          <Field label="Date" htmlFor="h-date">
            <Input id="h-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          </Field>
          <Field label="Applies to" htmlFor="h-dept">
            <Select id="h-dept" value={dept} onChange={(e) => setDept(e.target.value)} className="w-52">
              <option value="">Organisation-wide</option>
              {departments.data?.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </Select>
          </Field>
          <Button type="submit" icon={<Plus className="size-4" />} disabled={!canAdd} loading={create.isPending}>Add holiday</Button>
        </form>
      </Card>
      <Card>
        <CardHeader
          title={"Holidays in " + year}
          actions={
            <div className="flex items-center gap-1">
              <Button size="sm" variant="ghost" onClick={() => setYear((y) => y - 1)}>← {year - 1}</Button>
              <Button size="sm" variant="ghost" onClick={() => setYear((y) => y + 1)}>{year + 1} →</Button>
            </div>
          }
        />
        <DataTable caption="Holidays" columns={columns} rows={holidays.data} rowKey={(h) => h.id} loading={holidays.isFetching} error={holidays.error} onRetry={() => holidays.refetch()} empty={<EmptyState title={"No holidays in " + year} />} />
      </Card>
      <ConfirmDialog open={!!deleting} title={"Remove " + (deleting?.name ?? "") + "?"} message="Attendance for this date will be recalculated as a normal working day." confirmLabel="Remove" danger loading={del.isPending} onCancel={() => setDeleting(null)} onConfirm={() => deleting && del.mutate(deleting)} />
    </div>
  );
}
