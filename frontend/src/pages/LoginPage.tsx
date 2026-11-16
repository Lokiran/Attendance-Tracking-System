import { zodResolver } from "@hookform/resolvers/zod";
import { useQuery } from "@tanstack/react-query";
import {
  AlertCircle,
  BarChart3,
  CalendarCheck,
  CheckCircle2,
  ChevronDown,
  Eye,
  EyeOff,
  FileSpreadsheet,
  FlaskConical,
  Info,
  KeyRound,
  Lock,
  Mail,
  Users,
} from "lucide-react";
import { useState, type ReactNode } from "react";
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

const FEATURES: { icon: typeof FileSpreadsheet; title: string; text: string }[] = [
  { icon: FileSpreadsheet, title: "Monthly timesheets", text: "HR imports Excel, CSV or PDF timesheets, with a preview before anything changes." },
  { icon: BarChart3, title: "WFO targets at a glance", text: "Office days against the monthly target, colour-coded so everyone knows where they stand." },
  { icon: Users, title: "A view for every role", text: "Employees see their month, managers their team, HR the whole organisation." },
];

/** Decorative preview of the WFO target panel (example figures, clearly labelled). */
function ExamplePanel() {
  return (
    <div className="rounded-xl border border-white/10 bg-white/5 p-4" aria-hidden>
      <div className="flex items-center justify-between text-xs text-on-secondary-muted">
        <span>Work from office · example</span>
        <span className="inline-flex items-center gap-1 rounded-full bg-white/10 px-2 py-0.5 text-on-secondary">
          <CheckCircle2 className="size-3.5" style={{ color: "var(--good)" }} /> On target
        </span>
      </div>
      <div className="mt-2 flex items-baseline gap-1.5 text-on-secondary">
        <span className="tabular text-3xl font-semibold">14</span>
        <span className="text-sm text-on-secondary-muted">of 12 required days</span>
      </div>
      <div className="mt-3 h-2 overflow-hidden rounded-full bg-white/10">
        <div className="h-full w-full rounded-full" style={{ background: "var(--good)" }} />
      </div>
      <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-on-secondary-muted">
        {[["var(--good)", "12+ days"], ["var(--warning)", "up to 3 short"], ["var(--critical)", "more than 3 short"]].map(([c, t]) => (
          <span key={t} className="inline-flex items-center gap-1.5"><span className="size-2 rounded-full" style={{ background: c }} />{t}</span>
        ))}
      </div>
    </div>
  );
}

function BrandPanel() {
  return (
    <aside className="tone-secondary relative hidden overflow-hidden lg:flex lg:w-[46%] lg:flex-col lg:justify-between xl:w-1/2">
      {/* soft accent glow, purely decorative */}
      <div aria-hidden className="pointer-events-none absolute -left-24 -top-24 size-96 rounded-full opacity-30 blur-3xl" style={{ background: "var(--brand)" }} />
      <div aria-hidden className="pointer-events-none absolute -bottom-32 right-0 size-80 rounded-full opacity-20 blur-3xl" style={{ background: "var(--brand)" }} />
      <div className="relative px-12 pt-12">
        <div className="flex items-center gap-3">
          <span className="inline-flex size-10 items-center justify-center rounded-xl bg-brand text-on-brand"><CalendarCheck className="size-5" aria-hidden /></span>
          <div>
            <p className="text-base font-semibold text-on-secondary">Nexa Attendance</p>
            <p className="text-xs text-on-secondary-muted">Nexa Technologies</p>
          </div>
        </div>
        <h2 className="mt-14 max-w-md text-3xl font-semibold leading-tight tracking-tight text-on-secondary">
          Office attendance, straight from your monthly timesheets.
        </h2>
        <ul className="mt-8 max-w-md space-y-5">
          {FEATURES.map(({ icon: Icon, title, text }) => (
            <li key={title} className="flex gap-3">
              <span className="mt-0.5 inline-flex size-8 shrink-0 items-center justify-center rounded-lg bg-white/10 text-on-secondary"><Icon className="size-4" aria-hidden /></span>
              <div>
                <p className="text-sm font-semibold text-on-secondary">{title}</p>
                <p className="mt-0.5 text-sm text-on-secondary-muted">{text}</p>
              </div>
            </li>
          ))}
        </ul>
      </div>
      <div className="relative px-12 pb-12">
        <div className="max-w-sm"><ExamplePanel /></div>
        <p className="mt-6 text-xs text-on-secondary-muted">© Nexa Technologies · Attendance tracking</p>
      </div>
    </aside>
  );
}

function Alert({ tone, children }: { tone: "error" | "info"; children: ReactNode }) {
  const Icon = tone === "error" ? AlertCircle : Info;
  return (
    <div role="alert" className="mt-5 flex gap-2.5 rounded-lg border border-line bg-surface-2 p-3 text-sm text-ink">
      <Icon className="mt-0.5 size-4 shrink-0" style={{ color: tone === "error" ? "var(--critical)" : "var(--warning)" }} aria-hidden />
      <span>{children}</span>
    </div>
  );
}

/** Text input with a leading icon (and optional trailing control). */
function IconInput({ icon, trailing, ...rest }: React.InputHTMLAttributes<HTMLInputElement> & { icon: ReactNode; trailing?: ReactNode }) {
  return (
    <div className="relative">
      <span className="pointer-events-none absolute inset-y-0 left-3 flex items-center text-ink-3" aria-hidden>{icon}</span>
      <Input {...rest} className={trailing ? "h-11! pl-10! pr-11!" : "h-11! pl-10!"} />
      {trailing && <span className="absolute inset-y-0 right-1.5 flex items-center">{trailing}</span>}
    </div>
  );
}

export function LoginPage() {
  const { user, login, devLogin, notice } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();
  const [error, setError] = useState<string | null>(() => {
    const code = params.get("error");
    return code ? (ENTRA_ERRORS[code] ?? ENTRA_FAILED) : null;
  });
  const [showPassword, setShowPassword] = useState(false);
  const [capsLock, setCapsLock] = useState(false);
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

  const passwordField = form.register("password");

  return (
    <div className="flex min-h-screen bg-bg">
      <BrandPanel />

      <main className="flex flex-1 items-center justify-center px-4 py-10 sm:px-8">
        <div className="w-full max-w-md">
          {/* compact brand header on small screens (the side panel is hidden there) */}
          <div className="mb-8 flex items-center gap-3 lg:hidden">
            <span className="inline-flex size-10 items-center justify-center rounded-xl bg-brand text-on-brand" aria-hidden>
              <CalendarCheck className="size-5" />
            </span>
            <div>
              <p className="text-lg font-semibold text-ink">Nexa Attendance</p>
              <p className="text-xs text-ink-3">Office attendance tracker</p>
            </div>
          </div>

          <div className="rounded-2xl border border-line bg-surface p-6 shadow-card sm:p-8">
            <h1 className="text-2xl font-semibold tracking-tight text-ink">Sign in</h1>
            <p className="mt-1.5 text-sm text-ink-2">
              Welcome back. {p?.entra ? "Use your work Microsoft account." : "Use your work email and password."}
            </p>

            {error ? <Alert tone="error">{error}</Alert> : notice ? <Alert tone="info">{notice}</Alert> : null}

            {!p ? (
              <div className="mt-6 space-y-3" aria-label="Loading sign-in options">
                <div className="h-11 animate-pulse rounded-lg bg-surface-2" />
                <div className="h-11 animate-pulse rounded-lg bg-surface-2" />
              </div>
            ) : (
              <>
                {p.entra && (
                  <a href={"/api/auth/entra/login?next=" + encodeURIComponent(from)} className={buttonClass("primary", "lg", "mt-6 h-11 w-full")}>
                    <KeyRound className="size-4" aria-hidden />
                    Sign in with Microsoft
                  </a>
                )}

                {p.entra && p.password && (
                  <div className="my-6 flex items-center gap-3 text-xs text-ink-3" aria-hidden>
                    <span className="h-px flex-1 bg-line" />
                    or use a password
                    <span className="h-px flex-1 bg-line" />
                  </div>
                )}

                {p.password && (
                  <form noValidate onSubmit={form.handleSubmit(onSubmit)} className={p.entra ? "space-y-4" : "mt-6 space-y-4"}>
                    <Field label="Email" htmlFor="email" error={errors.email?.message}>
                      <IconInput id="email" type="email" autoComplete="username" placeholder="you@company.com" icon={<Mail className="size-4" />}
                        {...form.register("email")} aria-invalid={!!errors.email} />
                    </Field>
                    <Field label="Password" htmlFor="password" error={errors.password?.message}
                      hint={capsLock ? <span className="inline-flex items-center gap-1 text-ink"><AlertCircle className="size-3.5" style={{ color: "var(--warning)" }} aria-hidden />Caps Lock is on</span> : undefined}>
                      <IconInput
                        id="password"
                        type={showPassword ? "text" : "password"}
                        autoComplete="current-password"
                        icon={<Lock className="size-4" />}
                        {...passwordField}
                        onKeyUp={(e) => setCapsLock(e.getModifierState?.("CapsLock") ?? false)}
                        onBlur={(e) => { setCapsLock(false); passwordField.onBlur(e); }}
                        aria-invalid={!!errors.password}
                        trailing={
                          <button
                            type="button"
                            onClick={() => setShowPassword((v) => !v)}
                            className="inline-flex size-8 items-center justify-center rounded-md text-ink-3 hover:bg-surface-2 hover:text-ink"
                            aria-label={showPassword ? "Hide password" : "Show password"}
                            aria-pressed={showPassword}
                          >
                            {showPassword ? <EyeOff className="size-4" aria-hidden /> : <Eye className="size-4" aria-hidden />}
                          </button>
                        }
                      />
                    </Field>
                    <Button type="submit" className="h-11 w-full" size="lg" variant={p.entra ? "secondary" : "primary"} loading={isSubmitting}>
                      Sign in
                    </Button>
                  </form>
                )}

                {p.dev && (
                  <details className="group mt-6 rounded-xl border border-dashed border-line-strong" open={!p.password && !p.entra}>
                    <summary className="flex cursor-pointer list-none items-center gap-2 px-4 py-3 text-sm font-medium text-ink [&::-webkit-details-marker]:hidden">
                      <FlaskConical className="size-4" style={{ color: "var(--warning)" }} aria-hidden />
                      Development sign-in (no password)
                      <ChevronDown className="ml-auto size-4 text-ink-3 transition-transform group-open:rotate-180" aria-hidden />
                    </summary>
                    <form onSubmit={onDevLogin} className="space-y-3 border-t border-dashed border-line-strong px-4 pb-4 pt-3">
                      <p className="text-xs text-ink-3">For local testing only: turned on by DEV_LOGIN_ENABLED and impossible in production.</p>
                      <Field label="Sign in as (email or employee ID)" htmlFor="dev-email">
                        <Input id="dev-email" value={devEmail} onChange={(e) => setDevEmail(e.target.value)} placeholder="e.g. 25298 or name@company.com"
                          autoComplete="off" className="h-10!" />
                      </Field>
                      <Button type="submit" variant="secondary" className="w-full" loading={devBusy} disabled={!devEmail.trim()}>
                        Dev sign-in
                      </Button>
                    </form>
                  </details>
                )}
              </>
            )}
          </div>

          <p className="mt-6 text-center text-xs leading-relaxed text-ink-3">
            Attendance comes from the monthly timesheets HR imports; this app does not record or verify presence itself.
            <br />
            Trouble signing in? Contact HR.
          </p>
        </div>
      </main>
    </div>
  );
}
