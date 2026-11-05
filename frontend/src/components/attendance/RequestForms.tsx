import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation } from "@tanstack/react-query";
import { useEffect } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";

import { Button } from "@/components/ui/Button";
import { Checkbox, Field, Input, Select, TextArea } from "@/components/ui/Fields";
import { Modal } from "@/components/ui/Modal";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import { todayInOffice } from "@/lib/dates";
import { useInvalidateAttendance } from "@/lib/queries";
import type { Correction, Leave } from "@/lib/types";

const time = z.string().regex(/^\d{2}:\d{2}$/, "Use HH:MM");

export const correctionSchema = z
  .object({
    attendance_date: z.string().min(1, "Date is required"),
    check_in_time: time,
    check_out_time: time,
    check_out_next_day: z.boolean(),
    break_minutes: z.number({ message: "Enter minutes" }).int().min(0).max(720),
    reason: z.string().trim().min(5, "Explain the reason (at least 5 characters)").max(1000),
  })
  .refine((v) => v.attendance_date <= todayInOffice(), { path: ["attendance_date"], message: "Date cannot be in the future" })
  .refine((v) => v.check_out_next_day || v.check_out_time > v.check_in_time, {
    path: ["check_out_time"],
    message: "Check-out must be after check-in (tick 'next day' for overnight sessions)",
  });
type CorrectionValues = z.infer<typeof correctionSchema>;

export function CorrectionFormModal({ open, onClose, date, sessionId }: {
  open: boolean;
  onClose: () => void;
  date: string | null;
  sessionId: number | null;
}) {
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const form = useForm<CorrectionValues>({
    resolver: zodResolver(correctionSchema),
    defaultValues: { attendance_date: date ?? todayInOffice(), check_in_time: "09:30", check_out_time: "18:30", check_out_next_day: false, break_minutes: 60, reason: "" },
  });
  useEffect(() => {
    if (open) form.reset({ ...form.getValues(), attendance_date: date ?? todayInOffice(), reason: "" });
  }, [open, date]); // eslint-disable-line react-hooks/exhaustive-deps

  const submit = useMutation({
    mutationFn: (v: CorrectionValues) => api.post<Correction>("/corrections", { ...v, session_id: sessionId }),
    onSuccess: () => {
      toast.success("Correction request submitted for review");
      invalidate();
      onClose();
    },
    onError: (e) => toast.error(e),
  });
  const { errors } = form.formState;

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={sessionId ? "Fix a recorded session" : "Add missing attendance"}
      description="Your manager reviews the request. The original record is kept in the history."
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="correction-form" loading={submit.isPending}>
            Submit request
          </Button>
        </>
      }
    >
      <form id="correction-form" noValidate onSubmit={form.handleSubmit((v) => submit.mutate(v))} className="grid gap-4 sm:grid-cols-2">
        <Field label="Attendance date" htmlFor="c-date" error={errors.attendance_date?.message} required>
          <Input id="c-date" type="date" max={todayInOffice()} {...form.register("attendance_date")} aria-invalid={!!errors.attendance_date} readOnly={!!sessionId} />
        </Field>
        <Field label="Break (minutes)" htmlFor="c-break" error={errors.break_minutes?.message}>
          <Input id="c-break" type="number" min={0} {...form.register("break_minutes", { valueAsNumber: true })} aria-invalid={!!errors.break_minutes} />
        </Field>
        <Field label="Check-in time (office time)" htmlFor="c-in" error={errors.check_in_time?.message} required>
          <Input id="c-in" type="time" {...form.register("check_in_time")} aria-invalid={!!errors.check_in_time} />
        </Field>
        <Field label="Check-out time (office time)" htmlFor="c-out" error={errors.check_out_time?.message} required>
          <Input id="c-out" type="time" {...form.register("check_out_time")} aria-invalid={!!errors.check_out_time} />
        </Field>
        <div className="sm:col-span-2">
          <Checkbox label="Check-out was on the next day (overnight session)" {...form.register("check_out_next_day")} />
        </div>
        <div className="sm:col-span-2">
          <Field label="Reason" htmlFor="c-reason" error={errors.reason?.message} required>
            <TextArea id="c-reason" {...form.register("reason")} aria-invalid={!!errors.reason} placeholder="e.g. Forgot to check out before leaving the office" />
          </Field>
        </div>
      </form>
    </Modal>
  );
}

const LEAVE_TYPES = ["ANNUAL", "SICK", "CASUAL", "UNPAID", "OTHER"] as const;

export const leaveSchema = z
  .object({
    start_date: z.string().min(1, "Start date is required"),
    end_date: z.string().min(1, "End date is required"),
    leave_type: z.enum(LEAVE_TYPES),
    reason: z.string().max(1000).optional(),
  })
  .refine((v) => v.end_date >= v.start_date, { path: ["end_date"], message: "End date must be on or after the start date" });
type LeaveValues = z.infer<typeof leaveSchema>;

export function LeaveFormModal({ open, onClose, employeeId, employeeName }: {
  open: boolean;
  onClose: () => void;
  /** When set, a reviewer records already-approved leave for this employee. */
  employeeId?: number;
  employeeName?: string;
}) {
  const toast = useToast();
  const invalidate = useInvalidateAttendance();
  const today = todayInOffice();
  const form = useForm<LeaveValues>({ resolver: zodResolver(leaveSchema), defaultValues: { start_date: today, end_date: today, leave_type: "ANNUAL", reason: "" } });
  useEffect(() => {
    if (open) form.reset({ start_date: today, end_date: today, leave_type: "ANNUAL", reason: "" });
  }, [open]); // eslint-disable-line react-hooks/exhaustive-deps
  const submit = useMutation({
    mutationFn: (v: LeaveValues) =>
      employeeId ? api.post<Leave>("/leaves/record", { ...v, employee_id: employeeId }) : api.post<Leave>("/leaves", v),
    onSuccess: () => {
      toast.success(employeeId ? "Approved leave recorded" : "Leave request submitted");
      invalidate();
      onClose();
    },
    onError: (e) => toast.error(e),
  });
  const { errors } = form.formState;
  return (
    <Modal
      open={open}
      onClose={onClose}
      title={employeeId ? "Record leave for " + employeeName : "Request leave"}
      description={employeeId ? "Recorded leave is approved immediately and audited." : "Only approved leave affects attendance calculations."}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" form="leave-form" loading={submit.isPending}>
            {employeeId ? "Record leave" : "Submit request"}
          </Button>
        </>
      }
    >
      <form id="leave-form" noValidate onSubmit={form.handleSubmit((v) => submit.mutate(v))} className="grid gap-4 sm:grid-cols-2">
        <Field label="From" htmlFor="l-start" error={errors.start_date?.message} required>
          <Input id="l-start" type="date" {...form.register("start_date")} aria-invalid={!!errors.start_date} />
        </Field>
        <Field label="To" htmlFor="l-end" error={errors.end_date?.message} required>
          <Input id="l-end" type="date" {...form.register("end_date")} aria-invalid={!!errors.end_date} />
        </Field>
        <Field label="Leave type" htmlFor="l-type" required>
          <Select id="l-type" {...form.register("leave_type")}>
            {LEAVE_TYPES.map((t) => (
              <option key={t} value={t}>
                {t.charAt(0) + t.slice(1).toLowerCase()}
              </option>
            ))}
          </Select>
        </Field>
        <div className="sm:col-span-2">
          <Field label="Reason (optional)" htmlFor="l-reason" error={errors.reason?.message}>
            <TextArea id="l-reason" {...form.register("reason")} />
          </Field>
        </div>
      </form>
    </Modal>
  );
}
