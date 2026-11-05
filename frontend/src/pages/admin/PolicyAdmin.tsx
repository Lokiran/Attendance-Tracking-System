import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useEffect } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";

import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { Checkbox, Field, Input, TextArea } from "@/components/ui/Fields";
import { PageHeader } from "@/components/ui/PageHeader";
import { PageSkeleton } from "@/components/ui/Skeleton";
import { ErrorState } from "@/components/ui/States";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { useInvalidateAttendance } from "@/lib/queries";
import type { Policy } from "@/lib/types";

const schema = z.object({
  min_present_minutes: z.number().int().min(0).max(1440),
  count_incomplete_as_present: z.boolean(),
  max_session_minutes: z.number().int().min(60).max(1440),
  late_grace_minutes: z.number().int().min(0).max(240),
  correction_window_days: z.number().int().min(1).max(365),
  manager_can_approve: z.boolean(),
  notes: z.string().max(2000).nullable(),
});
type Values = z.infer<typeof schema>;

export function PolicyAdmin() {
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const q = useQuery({ queryKey: ["policy"], queryFn: () => api.get<Policy>("/policy") });
  const form = useForm<Values>({ resolver: zodResolver(schema) });
  useEffect(() => {
    if (q.data) form.reset(q.data);
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
        <Card>
          <CardHeader title="Day qualification" />
          <div className="space-y-4 p-5">
            <Field label="Minimum net minutes for a finished day to count as present" htmlFor="p-min" error={errors.min_present_minutes?.message} hint="0 means any completed session qualifies.">
              <Input id="p-min" type="number" {...form.register("min_present_minutes", num)} />
            </Field>
            <Checkbox label="Count days with only a missing check-out as present" {...form.register("count_incomplete_as_present")} />
            <p className="text-xs text-ink-3">Unresolved time never adds to hours, whichever option is chosen.</p>
            <Field label="Late-arrival grace (minutes after scheduled start)" htmlFor="p-late" error={errors.late_grace_minutes?.message}>
              <Input id="p-late" type="number" {...form.register("late_grace_minutes", num)} />
            </Field>
          </div>
        </Card>
        <Card>
          <CardHeader title="Sessions & corrections" />
          <div className="space-y-4 p-5">
            <Field label="Maximum session length (minutes)" htmlFor="p-max" error={errors.max_session_minutes?.message} hint="Open sessions older than this are flagged as a missing check-out.">
              <Input id="p-max" type="number" {...form.register("max_session_minutes", num)} />
            </Field>
            <Field label="Correction window (days)" htmlFor="p-win" error={errors.correction_window_days?.message} hint="How far back employees may request corrections.">
              <Input id="p-win" type="number" {...form.register("correction_window_days", num)} />
            </Field>
            <Checkbox label="Managers may approve requests for their reporting line" {...form.register("manager_can_approve")} />
            <p className="text-xs text-ink-3">When off, only administrators can approve. Nobody can approve their own request.</p>
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
