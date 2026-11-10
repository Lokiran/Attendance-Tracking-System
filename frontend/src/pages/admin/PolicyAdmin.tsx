import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useEffect } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";

import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { Field, Input, TextArea } from "@/components/ui/Fields";
import { PageHeader } from "@/components/ui/PageHeader";
import { PageSkeleton } from "@/components/ui/Skeleton";
import { ErrorState } from "@/components/ui/States";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { useInvalidateAttendance } from "@/lib/queries";
import type { Policy } from "@/lib/types";

// Only the rules that apply to imported timesheets (the check-in settings are kept in the database, unused).
const schema = z.object({
  min_present_minutes: z.number().int().min(0).max(1440),
  late_grace_minutes: z.number().int().min(0).max(240),
  notes: z.string().max(2000).nullable(),
});
type Values = z.infer<typeof schema>;

export function PolicyAdmin() {
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const q = useQuery({ queryKey: ["policy"], queryFn: () => api.get<Policy>("/policy") });
  const form = useForm<Values>({ resolver: zodResolver(schema) });
  useEffect(() => {
    if (q.data) form.reset({ min_present_minutes: q.data.min_present_minutes, late_grace_minutes: q.data.late_grace_minutes, notes: q.data.notes });
  }, [q.data]); // eslint-disable-line react-hooks/exhaustive-deps
  const m = useMutation({
    mutationFn: (v: Values) => api.put<Policy>("/admin/policy", v),
    onSuccess: () => {
      toast.success("Attendance policy updated");
      q.refetch();
      invalidate();
    },
    onError: (e) => toast.error(e),
  });
  if (q.isError) return <ErrorState error={q.error} onRetry={() => q.refetch()} />;
  if (!q.data) return <PageSkeleton />;
  const { errors } = form.formState;
  const num = { valueAsNumber: true } as const;
  return (
    <div className="space-y-5">
      <PageHeader title="Attendance policy" description={"Rules applied to every dashboard, summary and report. Last changed " + formatDateTime(q.data.updated_at) + "."} />
      <form noValidate onSubmit={form.handleSubmit((v) => m.mutate(v))} className="grid gap-5 lg:grid-cols-2">
        <Card className="lg:col-span-2">
          <CardHeader title="Day qualification" subtitle="Applied to the imported timesheets" />
          <div className="grid gap-4 p-5 sm:grid-cols-2">
            <Field label="Minimum office minutes for a day to count as present" htmlFor="p-min" error={errors.min_present_minutes?.message}
              hint="Only for days whose hours the timesheet gives. 0: every office day counts. Days listed without hours always count.">
              <Input id="p-min" type="number" {...form.register("min_present_minutes", num)} />
            </Field>
            <Field label="Late-arrival grace (minutes after scheduled start)" htmlFor="p-late" error={errors.late_grace_minutes?.message}
              hint="Needs in times in the timesheet.">
              <Input id="p-late" type="number" {...form.register("late_grace_minutes", num)} />
            </Field>
          </div>
        </Card>
        <Card className="lg:col-span-2">
          <CardHeader title="Notes" />
          <div className="p-5">
            <Field label="Policy notes (visible to administrators)" htmlFor="p-notes">
              <TextArea id="p-notes" {...form.register("notes")} />
            </Field>
            <Button type="submit" className="mt-4" loading={m.isPending} disabled={!form.formState.isDirty}>
              Save policy
            </Button>
          </div>
        </Card>
      </form>
    </div>
  );
}
