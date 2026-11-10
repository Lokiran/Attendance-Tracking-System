import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import { CalendarCheck, FlaskConical, Info, KeyRound } from "lucide-react";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { Navigate, useLocation, useNavigate, useSearchParams } from "react-router-dom";
import { z } from "zod";

import { useAuth } from "@/auth/AuthContext";
import { Button, buttonClass } from "@/components/ui/Button";
import { Field, Input } from "@/components/ui/Fields";
import { api, errorMessage } from "@/lib/api";
import type { AuthProviders } from "@/lib/types";

export const loginSchema = z.object({
  email: z.string().trim().min(1, "Email is required").email("Enter a valid email address"),
  password: z.string().min(1, "Password is required"),
});
type LoginValues = z.infer<typeof loginSchema>;

/** Messages for the ?error= codes the Microsoft sign-in callback can return. */
export const ENTRA_ERRORS: Record<string, string> = {
  no_account: "Your Microsoft account isn't linked to an attendance account yet. Ask HR to add you.",
  account_inactive: "Your attendance account is inactive. Contact HR.",
  account_mismatch: "This attendance account is linked to a different Microsoft account. Contact HR.",
  entra_denied: "Microsoft sign-in was cancelled.",
  sign_in_expired: "The sign-in took too long or was interrupted. Please try again.",
};
const ENTRA_FAILED = "Microsoft sign-in didn't work. Please try again, or contact IT if it keeps happening.";

// If the providers request fails, offer the password form rather than an empty page.
const FALLBACK: AuthProviders = { password: true, entra: false, dev: false };

export function LoginPage() {
  const { user, login, devLogin, notice } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();
  const [error, setError] = useState<string | null>(() => {
    const code = params.get("error");
    return code ? (ENTRA_ERRORS[code] ?? ENTRA_FAILED) : null;
  });
  const [devEmail, setDevEmail] = useState("");
  const [devBusy, setDevBusy] = useState(false);
  const providers = useQuery({
    queryKey: ["auth-providers"],
    queryFn: () => api.get<AuthProviders>("/auth/providers"),
    staleTime: Infinity,
    retry: false,
  });
  const form = useForm<LoginValues>({ resolver: zodResolver(loginSchema), defaultValues: { email: "", password: "" } });
  const { errors, isSubmitting } = form.formState;
  const from = (location.state as { from?: string } | null)?.from ?? "/";

  if (user) return <Navigate to={from} replace />;
  const p = providers.data ?? (providers.isError ? FALLBACK : null);

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

  const onDevLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setDevBusy(true);
    try {
      await devLogin(devEmail.trim());
      navigate(from, { replace: true });
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setDevBusy(false);
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
          <p className="mt-1 text-sm text-ink-2">
            {p?.entra ? "Use your work Microsoft account." : "Use your work email and password."}
          </p>
          {(error || notice) && (
            <div role="alert" className="mt-4 flex gap-2 rounded-lg border border-line bg-surface-2 p-3 text-sm text-ink">
              <Info className="mt-0.5 size-4 shrink-0" style={{ color: error ? "var(--critical)" : "var(--st-incomplete)" }} aria-hidden />
              {error ?? notice}
            </div>
          )}

          {!p ? (
            <div className="mt-5 h-11 animate-pulse rounded-lg bg-surface-2" aria-label="Loading sign-in options" />
          ) : (
            <>
              {p.entra && (
                <a href={"/api/auth/entra/login?next=" + encodeURIComponent(from)} className={buttonClass("primary", "lg", "mt-5 w-full")}>
                  <KeyRound className="size-4" aria-hidden />
                  Sign in with Microsoft
                </a>
              )}

              {p.entra && p.password && (
                <div className="my-5 flex items-center gap-3 text-xs text-ink-3" aria-hidden>
                  <span className="h-px flex-1 bg-line" />
                  or use a password
                  <span className="h-px flex-1 bg-line" />
                </div>
              )}

              {p.password && (
                <form noValidate onSubmit={form.handleSubmit(onSubmit)} className={p.entra ? "space-y-4" : "mt-5 space-y-4"}>
                  <Field label="Email" htmlFor="email" error={errors.email?.message}>
                    <Input id="email" type="email" autoComplete="username" {...form.register("email")} aria-invalid={!!errors.email} />
                  </Field>
                  <Field label="Password" htmlFor="password" error={errors.password?.message}>
                    <Input id="password" type="password" autoComplete="current-password" {...form.register("password")} aria-invalid={!!errors.password} />
                  </Field>
                  <Button type="submit" className="w-full" size="lg" variant={p.entra ? "secondary" : "primary"} loading={isSubmitting}>
                    Sign in
                  </Button>
                </form>
              )}

              {p.dev && (
                <form onSubmit={onDevLogin} className="mt-5 space-y-3 rounded-lg border border-dashed border-line-strong p-4">
                  <p className="flex items-center gap-2 text-xs font-medium text-ink">
                    <FlaskConical className="size-4" style={{ color: "var(--st-incomplete)" }} aria-hidden />
                    Development sign-in (no password)
                  </p>
                  <p className="text-xs text-ink-3">For local testing only. Turned on by DEV_LOGIN_ENABLED and impossible in production.</p>
                  <Field label="Sign in as (email)" htmlFor="dev-email">
                    <Input id="dev-email" type="email" value={devEmail} onChange={(e) => setDevEmail(e.target.value)} placeholder="rahul.reddy@nexa.example" />
                  </Field>
                  <Button type="submit" variant="secondary" className="w-full" loading={devBusy} disabled={!devEmail.trim()}>
                    Dev sign-in
                  </Button>
                </form>
              )}
            </>
          )}
        </div>
        <p className="mt-4 text-center text-xs text-ink-3">
          Attendance comes from the monthly timesheets HR imports; this app does not record or verify presence itself.
        </p>
      </div>
    </div>
  );
}
