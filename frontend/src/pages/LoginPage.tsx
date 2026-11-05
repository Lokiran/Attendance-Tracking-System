import { zodResolver } from "@hookform/resolvers/zod";
import { CalendarCheck, Info } from "lucide-react";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Navigate, useLocation, useNavigate } from "react-router-dom";
import { z } from "zod";

import { useAuth } from "@/auth/AuthContext";
import { Button } from "@/components/ui/Button";
import { Field, Input } from "@/components/ui/Fields";
import { errorMessage } from "@/lib/api";

export const loginSchema = z.object({
  email: z.string().trim().min(1, "Email is required").email("Enter a valid email address"),
  password: z.string().min(1, "Password is required"),
});
type LoginValues = z.infer<typeof loginSchema>;

export function LoginPage() {
  const { user, login, notice } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [error, setError] = useState<string | null>(null);
  const form = useForm<LoginValues>({ resolver: zodResolver(loginSchema), defaultValues: { email: "", password: "" } });
  const { errors, isSubmitting } = form.formState;
  const from = (location.state as { from?: string } | null)?.from ?? "/";

  if (user) return <Navigate to={from} replace />;

  const onSubmit = async (v: LoginValues) => {
    setError(null);
    try {
      await login(v.email, v.password);
      navigate(from, { replace: true });
    } catch (e) {
      setError(errorMessage(e));
      form.setValue("password", "");
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-bg px-4 py-10">
      <div className="w-full max-w-sm">
        <div className="mb-6 flex items-center gap-3">
          <span className="inline-flex size-10 items-center justify-center rounded-xl bg-brand text-on-brand" aria-hidden>
            <CalendarCheck className="size-5" />
          </span>
          <div>
            <h1 className="text-lg font-semibold text-ink">Nexa Attendance</h1>
            <p className="text-xs text-ink-3">Office attendance tracker</p>
          </div>
        </div>
        <div className="rounded-2xl border border-line bg-surface p-6 shadow-sm">
          <h2 className="text-base font-semibold text-ink">Sign in</h2>
          <p className="mt-1 text-sm text-ink-2">Use your work email and password.</p>
          {(error || notice) && (
            <div role="alert" className="mt-4 flex gap-2 rounded-lg border border-line bg-surface-2 p-3 text-sm text-ink">
              <Info className="mt-0.5 size-4 shrink-0" style={{ color: error ? "var(--critical)" : "var(--st-incomplete)" }} aria-hidden />
              {error ?? notice}
            </div>
          )}
          <form noValidate onSubmit={form.handleSubmit(onSubmit)} className="mt-5 space-y-4">
            <Field label="Email" htmlFor="email" error={errors.email?.message}>
              <Input id="email" type="email" autoComplete="username" {...form.register("email")} aria-invalid={!!errors.email} />
            </Field>
            <Field label="Password" htmlFor="password" error={errors.password?.message}>
              <Input id="password" type="password" autoComplete="current-password" {...form.register("password")} aria-invalid={!!errors.password} />
            </Field>
            <Button type="submit" className="w-full" size="lg" loading={isSubmitting}>
              Sign in
            </Button>
          </form>
        </div>
        <p className="mt-4 text-center text-xs text-ink-3">
          Attendance is recorded from manual check-in and check-out events; it does not verify physical presence.
        </p>
      </div>
    </div>
  );
}
