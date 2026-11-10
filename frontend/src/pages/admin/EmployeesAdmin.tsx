import { zodResolver } from "@hookform/resolvers/zod";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { KeyRound, Pencil, UserPlus } from "lucide-react";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { Link } from "react-router-dom";
import { z } from "zod";

import { useAuth } from "@/auth/AuthContext";
import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, Pagination, type Column } from "@/components/ui/DataTable";
import { Checkbox, Field, Input, SearchInput, Select } from "@/components/ui/Fields";
import { ConfirmDialog, Modal } from "@/components/ui/Modal";
import { PageHeader } from "@/components/ui/PageHeader";
import { EmptyState } from "@/components/ui/States";
import { useToast } from "@/components/ui/Toast";
import { useUrlState } from "@/hooks/useUrlState";
import { api } from "@/lib/api";
import { todayInOffice } from "@/lib/dates";
import { formatDate, ROLE_LABEL, titleCase } from "@/lib/format";
import { qk } from "@/lib/queries";
import type { AdminUser, Department, Page, Role } from "@/lib/types";

const ROLES: Role[] = ["EMPLOYEE", "MANAGER", "ADMIN"];

const baseSchema = z.object({
  employee_code: z.string().trim().min(2, "At least 2 characters").max(32).regex(/^[A-Za-z0-9_-]+$/, "Letters, digits, - and _ only"),
  full_name: z.string().trim().min(2, "Name is required").max(120),
  email: z.string().trim().email("Enter a valid email"),
  role: z.enum(["EMPLOYEE", "MANAGER", "ADMIN"]),
  job_title: z.string().max(120).optional(),
  department_id: z.string(),
  manager_id: z.string(),
  date_joined: z.string().min(1, "Joining date is required"),
  attendance_tracked: z.boolean(),
  password: z.string().optional(),
});
type FormValues = z.infer<typeof baseSchema>;

const passwordRule = (v: string | undefined) => !!v && v.length >= 10 && /[A-Za-z]/.test(v) && /\d/.test(v);

function useAdminLookups() {
  const departments = useQuery({ queryKey: ["departments"], queryFn: () => api.get<Department[]>("/departments") });
  const managers = useQuery({ queryKey: [...qk.admin, "managers"], queryFn: () => api.get<AdminUser[]>("/managers") });
  return { departments: departments.data ?? [], managers: managers.data ?? [] };
}

function UserFormModal({ user, open, onClose }: { user: AdminUser | null; open: boolean; onClose: () => void }) {
  const isNew = !user;
  const toast = useToast();
  const qc = useQueryClient();
  const { departments, managers } = useAdminLookups();
  const schema = isNew ? baseSchema.refine((v) => passwordRule(v.password), { path: ["password"], message: "At least 10 characters with letters and digits" }) : baseSchema;
  const form = useForm<FormValues>({ resolver: zodResolver(schema) });
  useEffect(() => {
    if (!open) return;
    form.reset({
      employee_code: user?.employee_code ?? "",
      full_name: user?.full_name ?? "",
      email: user?.email ?? "",
      role: user?.role ?? "EMPLOYEE",
      job_title: user?.job_title ?? "",
      department_id: user?.department ? String(user.department.id) : "",
      manager_id: user?.manager ? String(user.manager.id) : "",
      date_joined: user?.date_joined ?? todayInOffice(),
      attendance_tracked: user?.attendance_tracked ?? true,
      password: "",
    });
  }, [open, user]); // eslint-disable-line react-hooks/exhaustive-deps

  const save = useMutation({
    mutationFn: (v: FormValues) => {
      const body = {
        employee_code: v.employee_code,
        full_name: v.full_name,
        email: v.email,
        role: v.role,
        job_title: v.job_title || null,
        department_id: v.department_id ? Number(v.department_id) : null,
        manager_id: v.manager_id ? Number(v.manager_id) : null,
        date_joined: v.date_joined,
        attendance_tracked: v.attendance_tracked,
      };
      return isNew ? api.post<AdminUser>("/admin/users", { ...body, password: v.password }) : api.patch<AdminUser>("/admin/users/" + user!.id, body);
    },
    onSuccess: (u) => {
      toast.success((isNew ? "Created " : "Updated ") + u.full_name);
      qc.invalidateQueries({ queryKey: qk.admin });
      qc.invalidateQueries({ queryKey: qk.dashboard });
      qc.invalidateQueries({ queryKey: ["team-members"] });
      onClose();
    },
    onError: (e) => toast.error(e),
  });
  const { errors } = form.formState;
  return (
    <Modal
      open={open}
      onClose={onClose}
      size="lg"
      title={isNew ? "Add employee" : "Edit " + user?.full_name}
      description="Changes are validated by the server (unique IDs, valid managers, no circular reporting) and audited."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button type="submit" form="user-form" loading={save.isPending}>{isNew ? "Create employee" : "Save changes"}</Button>
        </>
      }
    >
      <form id="user-form" noValidate onSubmit={form.handleSubmit((v) => save.mutate(v))} className="grid gap-4 sm:grid-cols-2">
        <Field label="Full name" htmlFor="u-name" error={errors.full_name?.message} required>
          <Input id="u-name" {...form.register("full_name")} aria-invalid={!!errors.full_name} />
        </Field>
        <Field label="Employee ID" htmlFor="u-code" error={errors.employee_code?.message} required>
          <Input id="u-code" {...form.register("employee_code")} aria-invalid={!!errors.employee_code} />
        </Field>
        <Field label="Work email" htmlFor="u-email" error={errors.email?.message} required>
          <Input id="u-email" type="email" {...form.register("email")} aria-invalid={!!errors.email} />
        </Field>
        <Field label="Job title" htmlFor="u-title">
          <Input id="u-title" {...form.register("job_title")} />
        </Field>
        <Field label="Role" htmlFor="u-role" hint="Admins cannot change their own role; the last active admin cannot be demoted.">
          <Select id="u-role" {...form.register("role")}>
            {ROLES.map((r) => <option key={r} value={r}>{titleCase(r)}</option>)}
          </Select>
        </Field>
        <Field label="Joined on" htmlFor="u-joined" error={errors.date_joined?.message} required>
          <Input id="u-joined" type="date" {...form.register("date_joined")} />
        </Field>
        <Field label="Department" htmlFor="u-dept">
          <Select id="u-dept" {...form.register("department_id")}>
            <option value="">Unassigned</option>
            {departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </Select>
        </Field>
        <Field label="Manager" htmlFor="u-mgr" hint="Only active managers and admins can be assigned.">
          <Select id="u-mgr" {...form.register("manager_id")}>
            <option value="">No manager</option>
            {managers.filter((m) => m.id !== user?.id).map((m) => <option key={m.id} value={m.id}>{m.full_name} ({m.employee_code})</option>)}
          </Select>
        </Field>
        {isNew && (
          <Field label="Initial password" htmlFor="u-pw" error={errors.password?.message} hint="Share it securely; the employee should change it after first sign-in." required>
            <Input id="u-pw" type="password" autoComplete="new-password" {...form.register("password")} aria-invalid={!!errors.password} />
          </Field>
        )}
        <div className="flex items-end sm:col-span-2">
          <Checkbox label="Include in attendance statistics" {...form.register("attendance_tracked")} />
        </div>
      </form>
    </Modal>
  );
}

function ResetPasswordModal({ user, onClose }: { user: AdminUser | null; onClose: () => void }) {
  const toast = useToast();
  const [pw, setPw] = useState("");
  const m = useMutation({
    mutationFn: () => api.post("/admin/users/" + user!.id + "/reset-password", { password: pw }),
    onSuccess: () => {
      toast.success("Password reset for " + user?.full_name);
      setPw("");
      onClose();
    },
    onError: (e) => toast.error(e),
  });
  const ok = passwordRule(pw);
  return (
    <Modal open={!!user} onClose={onClose} size="sm" title={"Reset password · " + (user?.full_name ?? "")} description="All of the user's active sessions will be signed out."
      footer={<><Button variant="secondary" onClick={onClose}>Cancel</Button><Button disabled={!ok} loading={m.isPending} onClick={() => m.mutate()}>Reset password</Button></>}>
      <Field label="New password" htmlFor="rp" hint="At least 10 characters with letters and digits." error={pw && !ok ? "Password is too weak" : undefined}>
        <Input id="rp" type="password" autoComplete="new-password" value={pw} onChange={(e) => setPw(e.target.value)} />
      </Field>
    </Modal>
  );
}

export function EmployeesAdmin() {
  const { user: me } = useAuth();
  const toast = useToast();
  const qc = useQueryClient();
  const { get, set } = useUrlState();
  const { departments } = useAdminLookups();
  const [editing, setEditing] = useState<AdminUser | null>(null);
  const [creating, setCreating] = useState(false);
  const [resetting, setResetting] = useState<AdminUser | null>(null);
  const [toggling, setToggling] = useState<AdminUser | null>(null);
  const page = Number(get("page", "1")) || 1;
  const params = { search: get("q"), role: get("role"), department_id: get("dept"), is_active: get("active", "true"), page, page_size: 15 };
  const users = useQuery({ queryKey: [...qk.admin, "users", params], queryFn: () => api.get<Page<AdminUser>>("/admin/users", params), placeholderData: keepPreviousData });
  const toggle = useMutation({
    mutationFn: (u: AdminUser) => api.patch<AdminUser>("/admin/users/" + u.id, { is_active: !u.is_active }),
    onSuccess: (u) => {
      toast.success(u.full_name + (u.is_active ? " reactivated" : " deactivated"));
      qc.invalidateQueries({ queryKey: qk.admin });
      qc.invalidateQueries({ queryKey: qk.dashboard });
      setToggling(null);
    },
    onError: (e) => {
      toast.error(e);
      setToggling(null);
    },
  });

  const columns: Column<AdminUser>[] = [
    { key: "name", header: "Name", cell: (u) => <Link to={"/people/" + u.id} className="font-medium hover:underline">{u.full_name}<span className="block text-xs font-normal text-ink-3">{u.email}</span></Link> },
    { key: "code", header: "Employee ID", cell: (u) => <span className="tabular">{u.employee_code}</span> },
    { key: "role", header: "Role", cell: (u) => ROLE_LABEL[u.role] },
    { key: "dept", header: "Department", cell: (u) => u.department?.name ?? "—" },
    { key: "mgr", header: "Manager", cell: (u) => u.manager?.full_name ?? "—" },
    { key: "joined", header: "Joined", cell: (u) => formatDate(u.date_joined) },
    { key: "state", header: "Status", cell: (u) => <span className="text-xs">{u.is_active ? "Active" : "Inactive"}{!u.attendance_tracked && <span className="block text-ink-3">not tracked</span>}</span> },
    {
      key: "actions",
      header: "",
      cell: (u) => (
        <div className="flex justify-end gap-1">
          <Button size="sm" variant="ghost" icon={<Pencil className="size-3.5" />} onClick={() => setEditing(u)} aria-label={"Edit " + u.full_name} />
          <Button size="sm" variant="ghost" icon={<KeyRound className="size-3.5" />} onClick={() => setResetting(u)} aria-label={"Reset password for " + u.full_name} />
          {u.id !== me?.id && (
            <Button size="sm" variant="ghost" onClick={() => setToggling(u)}>
              {u.is_active ? "Deactivate" : "Activate"}
            </Button>
          )}
        </div>
      ),
    },
  ];

  return (
    <div className="space-y-5">
      <PageHeader title="Employees" description="Create accounts, assign departments and managers, and manage roles." actions={<Button icon={<UserPlus className="size-4" />} onClick={() => setCreating(true)}>Add employee</Button>} />
      <Card>
        <CardHeader
          title={users.data ? users.data.total + " people" : "People"}
          actions={
            <>
              <SearchInput value={get("q")} onChange={(v) => set({ q: v })} placeholder="Name, email or ID" label="Search employees" />
              <label htmlFor="f-role" className="sr-only">Role</label>
              <Select id="f-role" className="w-36" value={get("role")} onChange={(e) => set({ role: e.target.value })}>
                <option value="">All roles</option>
                {ROLES.map((r) => <option key={r} value={r}>{titleCase(r)}</option>)}
              </Select>
              <label htmlFor="f-dept" className="sr-only">Department</label>
              <Select id="f-dept" className="w-44" value={get("dept")} onChange={(e) => set({ dept: e.target.value })}>
                <option value="">All departments</option>
                {departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
              </Select>
              <label htmlFor="f-active" className="sr-only">Account status</label>
              <Select id="f-active" className="w-32" value={get("active", "true")} onChange={(e) => set({ active: e.target.value })}>
                <option value="true">Active</option>
                <option value="false">Inactive</option>
                <option value="">All</option>
              </Select>
            </>
          }
        />
        <DataTable caption="Employees" columns={columns} rows={users.data?.items} rowKey={(u) => u.id} loading={users.isFetching} error={users.error} onRetry={() => users.refetch()} empty={<EmptyState title="No people match these filters" />} />
        {users.data && <Pagination page={page} pageSize={15} total={users.data.total} onPage={(p) => set({ page: p }, false)} />}
      </Card>
      <UserFormModal open={creating || !!editing} user={editing} onClose={() => { setCreating(false); setEditing(null); }} />
      <ResetPasswordModal user={resetting} onClose={() => setResetting(null)} />
      <ConfirmDialog
        open={!!toggling}
        title={toggling?.is_active ? "Deactivate " + toggling?.full_name + "?" : "Reactivate " + toggling?.full_name + "?"}
        message={toggling?.is_active ? "They will be signed out and cannot sign in or record attendance. Their history is kept." : "They will be able to sign in again."}
        confirmLabel={toggling?.is_active ? "Deactivate" : "Reactivate"}
        danger={toggling?.is_active}
        loading={toggle.isPending}
        onCancel={() => setToggling(null)}
        onConfirm={() => toggling && toggle.mutate(toggling)}
      />
    </div>
  );
}
