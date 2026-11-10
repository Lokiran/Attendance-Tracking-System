import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { BarChart3, Pencil, Plus, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { Link } from "react-router-dom";

import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { Field, Input, TextArea } from "@/components/ui/Fields";
import { ConfirmDialog, Modal } from "@/components/ui/Modal";
import { PageHeader } from "@/components/ui/PageHeader";
import { PersonPicker } from "@/components/ui/PersonPicker";
import { EmptyState } from "@/components/ui/States";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import { qk } from "@/lib/queries";
import type { Department } from "@/lib/types";

function DepartmentModal({ dept, open, onClose }: { dept: Department | null; open: boolean; onClose: () => void }) {
  const toast = useToast();
  const qc = useQueryClient();
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [head, setHead] = useState("");
  useEffect(() => {
    if (open) {
      setName(dept?.name ?? "");
      setDescription(dept?.description ?? "");
      setHead(dept?.department_head_id ? String(dept.department_head_id) : "");
    }
  }, [open, dept]);
  const m = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), description: description.trim() || null, department_head_id: head ? Number(head) : null };
      return dept ? api.patch<Department>("/admin/departments/" + dept.id, body) : api.post<Department>("/admin/departments", body);
    },
    onSuccess: (d) => {
      toast.success((dept ? "Updated " : "Created ") + d.name);
      qc.invalidateQueries({ queryKey: ["departments"] });
      qc.invalidateQueries({ queryKey: qk.dashboard });
      onClose();
    },
    onError: (e) => toast.error(e),
  });
  const nameError = name.trim().length < 2 ? "Name must be at least 2 characters" : undefined;
  return (
    <Modal open={open} onClose={onClose} title={dept ? "Edit department" : "New department"} description="Department names are unique (case-insensitive)."
      footer={<><Button variant="secondary" onClick={onClose}>Cancel</Button><Button loading={m.isPending} disabled={!!nameError} onClick={() => m.mutate()}>{dept ? "Save" : "Create"}</Button></>}>
      <div className="space-y-4">
        <Field label="Name" htmlFor="d-name" error={name ? nameError : undefined} required>
          <Input id="d-name" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Department head" htmlFor="d-head" hint="Informational: reporting lines, not this field, control data access.">
          <PersonPicker id="d-head" source="admin" value={head} onChange={(id) => setHead(id)} selectedLabel={dept?.head?.full_name} placeholder="None. Search to choose a head" />
        </Field>
        <Field label="Description" htmlFor="d-desc">
          <TextArea id="d-desc" value={description} onChange={(e) => setDescription(e.target.value)} />
        </Field>
      </div>
    </Modal>
  );
}

export function DepartmentsAdmin() {
  const toast = useToast();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ["departments"], queryFn: () => api.get<Department[]>("/departments") });
  const [editing, setEditing] = useState<Department | null>(null);
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<Department | null>(null);
  const del = useMutation({
    mutationFn: (d: Department) => api.del("/admin/departments/" + d.id),
    onSuccess: () => {
      toast.success("Department deleted");
      qc.invalidateQueries({ queryKey: ["departments"] });
      setDeleting(null);
    },
    onError: (e) => {
      toast.error(e);
      setDeleting(null);
    },
  });
  const columns: Column<Department>[] = [
    { key: "name", header: "Department", cell: (d) => <span className="font-medium">{d.name}{d.description && <span className="block max-w-md truncate text-xs font-normal text-ink-3">{d.description}</span>}</span> },
    { key: "head", header: "Head", cell: (d) => d.head?.full_name ?? "—" },
    { key: "members", header: "Active members", align: "right", cell: (d) => d.member_count },
    {
      key: "actions",
      header: "",
      cell: (d) => (
        <div className="flex justify-end gap-1">
          <Link to={"/admin/departments/" + d.id + "/dashboard"} className="inline-flex h-8 items-center gap-1.5 rounded-lg px-3 text-xs font-medium text-ink-2 hover:bg-surface-2">
            <BarChart3 className="size-3.5" aria-hidden /> Statistics
          </Link>
          <Button size="sm" variant="ghost" icon={<Pencil className="size-3.5" />} onClick={() => setEditing(d)} aria-label={"Edit " + d.name} />
          <Button size="sm" variant="ghost" icon={<Trash2 className="size-3.5" />} onClick={() => setDeleting(d)} aria-label={"Delete " + d.name} />
        </div>
      ),
    },
  ];
  return (
    <div className="space-y-5">
      <PageHeader title="Departments" description="Create departments, assign heads and open department statistics." actions={<Button icon={<Plus className="size-4" />} onClick={() => setCreating(true)}>New department</Button>} />
      <Card>
        <DataTable caption="Departments" columns={columns} rows={q.data} rowKey={(d) => d.id} loading={q.isFetching} error={q.error} onRetry={() => q.refetch()} empty={<EmptyState title="No departments yet" action={<Button size="sm" onClick={() => setCreating(true)}>Create the first department</Button>} />} />
      </Card>
      <DepartmentModal open={creating || !!editing} dept={editing} onClose={() => { setCreating(false); setEditing(null); }} />
      <ConfirmDialog open={!!deleting} title={"Delete " + (deleting?.name ?? "") + "?"} message="Only departments without members can be deleted. This cannot be undone." confirmLabel="Delete" danger loading={del.isPending} onCancel={() => setDeleting(null)} onConfirm={() => deleting && del.mutate(deleting)} />
    </div>
  );
}
