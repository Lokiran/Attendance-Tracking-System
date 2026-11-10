import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import { z } from "zod";

import { useAuth } from "@/auth/AuthContext";
import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { Field, Input } from "@/components/ui/Fields";
import { PageHeader } from "@/components/ui/PageHeader";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import { formatDate, ROLE_LABEL } from "@/lib/format";

const schema = z
  .object({
    current_password: z.string().min(1, "Enter your current password"),
    new_password: z
      .string()
      .min(10, "At least 10 characters")
      .max(128)
      .regex(/[A-Za-z]/, "Include a letter")
      .regex(/\d/, "Include a digit"),
    confirm: z.string(),
  })
  .refine((v) => v.new_password === v.confirm, { path: ["confirm"], message: "Passwords do not match" });
type Values = z.infer<typeof schema>;

export function ProfilePage() {
  const { user } = useAuth();
  const toast = useToast();
  const form = useForm<Values>({ resolver: zodResolver(schema), defaultValues: { current_password: "", new_password: "", confirm: "" } });
  const m = useMutation({
    mutationFn: (v: Values) => api.post<{ message: string }>("/auth/change-password", { current_password: v.current_password, new_password: v.new_password }),
    onSuccess: (r) => {
      toast.success(r.message);
      form.reset();
    },
    onError: (e) => toast.error(e),
  });
  if (!user) return null;
  const { errors } = form.formState;
  const rows: [string, string][] = [
    ["Name", user.full_name],
    ["Email", user.email],
    ["Employee ID", user.employee_code],
    ["Role", ROLE_LABEL[user.role]],
    ["Job title", user.job_title ?? "—"],
    ["Department", user.department?.name ?? "Unassigned"],
    ["Manager", user.manager?.full_name ?? "—"],
    ["Joined", formatDate(user.date_joined)],
  ];
  return (
    <div className="space-y-5">
      <PageHeader title="Profile" description="Your account details are managed by an administrator." />
      <div className="grid gap-5 lg:grid-cols-2">
        <Card>
          <CardHeader title="Account" />
          <dl className="divide-y divide-line">
            {rows.map(([k, v]) => (
              <div key={k} className="flex justify-between gap-4 px-5 py-2.5 text-sm">
                <dt className="text-ink-3">{k}</dt>
                <dd className="text-right text-ink">{v}</dd>
              </div>
            ))}
          </dl>
        </Card>
        <Card>
          <CardHeader title="Change password" subtitle="Other signed-in sessions will be signed out." />
          <form noValidate onSubmit={form.handleSubmit((v) => m.mutate(v))} className="space-y-4 p-5">
            <Field label="Current password" htmlFor="pw-cur" error={errors.current_password?.message}>
              <Input id="pw-cur" type="password" autoComplete="current-password" {...form.register("current_password")} aria-invalid={!!errors.current_password} />
            </Field>
            <Field label="New password" htmlFor="pw-new" error={errors.new_password?.message} hint="At least 10 characters with letters and digits.">
              <Input id="pw-new" type="password" autoComplete="new-password" {...form.register("new_password")} aria-invalid={!!errors.new_password} />
            </Field>
            <Field label="Confirm new password" htmlFor="pw-conf" error={errors.confirm?.message}>
              <Input id="pw-conf" type="password" autoComplete="new-password" {...form.register("confirm")} aria-invalid={!!errors.confirm} />
            </Field>
            <Button type="submit" loading={m.isPending}>
              Update password
            </Button>
          </form>
        </Card>
      </div>
    </div>
  );
}
