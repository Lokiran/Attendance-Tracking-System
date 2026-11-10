import { useQuery, useQueryClient } from "@tanstack/react-query";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";

import { api, ApiError, setCsrfToken } from "@/lib/api";
import { setLatestTimesheetDate } from "@/lib/dates";
import { setOfficeTimezone } from "@/lib/format";
import { qk } from "@/lib/queries";
import type { MeResponse, Profile, Role } from "@/lib/types";

interface AuthState {
  user: Profile | null;
  hasReports: boolean;
  loading: boolean;
  /** Message shown on the login page after an involuntary sign-out (e.g. expired session). */
  notice: string | null;
  login: (email: string, password: string) => Promise<Profile>;
  /** Local testing only (DEV_LOGIN_ENABLED): sign in as an active user without a password. */
  devLogin: (email: string) => Promise<Profile>;
  logout: () => Promise<void>;
  hasRole: (...roles: Role[]) => boolean;
}

const AuthContext = createContext<AuthState | null>(null);

const AUTH_EVENT_KEY = "ats-auth-event";

/** Tells other open tabs that the signed-in user changed (they share the session cookie). */
function broadcastAuth(userId: number | null) {
  try {
    localStorage.setItem(AUTH_EVENT_KEY, JSON.stringify({ userId, at: Date.now() }));
  } catch {
    /* storage unavailable: the focus re-check still catches the change */
  }
}

function apply(me: MeResponse) {
  setCsrfToken(me.csrf_token);
  setOfficeTimezone(me.office_timezone);
  setLatestTimesheetDate(me.latest_timesheet_date);
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  const [notice, setNotice] = useState<string | null>(null);

  const me = useQuery({
    queryKey: qk.me,
    queryFn: async () => {
      try {
        const data = await api.get<MeResponse>("/auth/me");
        apply(data);
        return data;
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) return null;
        throw e;
      }
    },
    staleTime: 30_000,
    // All tabs share one session cookie; re-check who is signed in when a tab regains focus.
    refetchOnWindowFocus: "always",
    retry: false,
  });

  const lastUserId = useRef<number | null | undefined>(undefined);
  useEffect(() => {
    const id = me.data === undefined ? undefined : (me.data?.user.id ?? null);
    if (id === undefined) return;
    if (lastUserId.current !== undefined && lastUserId.current !== id) {
      // Identity changed underneath this tab (focus re-check): drop data fetched as the old user.
      qc.removeQueries({ predicate: (q) => q.queryKey[0] !== "me" });
    }
    lastUserId.current = id;
  }, [me.data, qc]);

  useEffect(() => {
    // Another tab signed in as someone else (or signed out): reload so this tab never shows one
    // user's screen while sending another user's cookie.
    const onStorage = (ev: StorageEvent) => {
      if (ev.key !== AUTH_EVENT_KEY || !ev.newValue) return;
      const current = (qc.getQueryData(qk.me) as MeResponse | null | undefined)?.user.id ?? null;
      const next = (JSON.parse(ev.newValue) as { userId: number | null }).userId;
      if (next !== current) window.location.reload();
    };
    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, [qc]);

  useEffect(() => {
    const onUnauthorized = (ev: Event) => {
      const err = (ev as CustomEvent<ApiError>).detail;
      if (qc.getQueryData(qk.me)) {
        setNotice(err?.message ?? "Your session has ended. Please sign in again.");
        setCsrfToken(null);
        qc.setQueryData(qk.me, null);
        qc.removeQueries({ predicate: (q) => q.queryKey[0] !== "me" });
      }
    };
    window.addEventListener("ats:unauthorized", onUnauthorized);
    return () => window.removeEventListener("ats:unauthorized", onUnauthorized);
  }, [qc]);

  const signedIn = useCallback(
    (data: MeResponse) => {
      apply(data);
      setNotice(null);
      qc.removeQueries({ predicate: (q) => q.queryKey[0] !== "me" });
      qc.setQueryData(qk.me, data);
      broadcastAuth(data.user.id);
      return data.user;
    },
    [qc],
  );
  const login = useCallback(
    async (email: string, password: string) => signedIn(await api.post<MeResponse>("/auth/login", { email, password })),
    [signedIn],
  );
  const devLogin = useCallback(
    async (email: string) => signedIn(await api.post<MeResponse>("/auth/dev-login", { email })),
    [signedIn],
  );

  const logout = useCallback(async () => {
    try {
      await api.post("/auth/logout");
    } finally {
      setCsrfToken(null);
      qc.setQueryData(qk.me, null);
      qc.removeQueries({ predicate: (q) => q.queryKey[0] !== "me" });
      broadcastAuth(null);
    }
  }, [qc]);

  const value = useMemo<AuthState>(() => {
    const user = me.data?.user ?? null;
    return {
      user,
      hasReports: me.data?.has_reports ?? false,
      loading: me.isLoading,
      notice,
      login,
      devLogin,
      logout,
      hasRole: (...roles) => !!user && roles.includes(user.role),
    };
  }, [me.data, me.isLoading, notice, login, devLogin, logout]);

  if (me.isError) {
    // Backend unreachable on first load: show a clear message rather than a blank screen.
    return (
      <div className="flex min-h-screen items-center justify-center p-6">
        <div className="max-w-md rounded-xl border border-line bg-surface p-6 text-center">
          <h1 className="text-lg font-semibold">Unable to reach the attendance service</h1>
          <p className="mt-2 text-sm text-ink-2">{(me.error as Error).message}</p>
          <button
            className="mt-4 rounded-lg bg-brand px-4 py-2 text-sm font-medium text-on-brand hover:bg-brand-hover"
            onClick={() => me.refetch()}
          >
            Retry
          </button>
        </div>
      </div>
    );
  }

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}
