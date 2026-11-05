import { AlertTriangle, ClipboardPen, ListChecks, LogIn, LogOut } from "lucide-react";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";

import { Button } from "@/components/ui/Button";
import { Card } from "@/components/ui/Card";
import { Field, Input } from "@/components/ui/Fields";
import { ConfirmDialog } from "@/components/ui/Modal";
import { Skeleton } from "@/components/ui/Skeleton";
import { ErrorState } from "@/components/ui/States";
import { StatusBadge } from "@/components/ui/StatusBadge";
import { useToast } from "@/components/ui/Toast";
import { formatDate, formatDateTime, formatMinutes, formatTime, getOfficeTimezone } from "@/lib/format";
import { useCheckIn, useCheckOut, useCurrentStatus } from "@/lib/queries";
import type { Profile } from "@/lib/types";

function useElapsed(checkIn: string | null | undefined, serverTime: string | undefined, fetchedAt: number) {
  // Server time anchors the clock so a wrong device clock does not distort the display.
  const offset = useMemo(() => (serverTime ? new Date(serverTime).getTime() - fetchedAt : 0), [serverTime, fetchedAt]);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!checkIn) return;
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, [checkIn]);
  if (!checkIn) return null;
  return Math.max(0, now + offset - new Date(checkIn).getTime());
}

function clock(ms: number) {
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  return [h, m, s % 60].map((n) => String(n).padStart(2, "0")).join(":");
}

/** "Good morning" etc. by the office clock, so it matches the attendance day. */
export function greeting(now: Date = new Date()): string {
  const hour = Number(new Intl.DateTimeFormat("en-GB", { timeZone: getOfficeTimezone(), hour: "numeric", hour12: false }).format(now));
  if (hour < 12) return "Good morning";
  if (hour < 17) return "Good afternoon";
  return "Good evening";
}

function Tile({ label, value, hint, children }: { label: string; value: ReactNode; hint?: ReactNode; children?: ReactNode }) {
  return (
    <div className="px-5 py-4 sm:px-6">
      <dt className="text-xs font-medium text-ink-3">{label}</dt>
      <dd className="tabular mt-1 text-xl font-semibold tracking-tight text-ink">{value}</dd>
      {hint && <dd className="mt-0.5 text-xs text-ink-3">{hint}</dd>}
      {children}
    </div>
  );
}

export function AttendanceActionCard({ user, onRequestCorrection, onFixPastDay }: {
  /** When given, the panel greets the person and shows their ID, department and manager. */
  user?: Profile;
  onRequestCorrection?: (date: string, sessionId: number) => void;
  onFixPastDay?: () => void;
}) {
  const status = useCurrentStatus();
  const checkIn = useCheckIn();
  const checkOut = useCheckOut();
  const toast = useToast();
  const [confirm, setConfirm] = useState(false);
  const [breakMin, setBreakMin] = useState("0");
  const active = status.data?.active_session ?? null;
  const elapsed = useElapsed(active?.check_in_at, status.data?.server_time, status.dataUpdatedAt);

  if (status.isError) return <Card><ErrorState error={status.error} onRetry={() => status.refetch()} /></Card>;
  if (!status.data) return <Card className="p-6"><Skeleton className="h-36" /></Card>;

  const s = status.data;
  const pending = checkIn.isPending || checkOut.isPending;
  const breakValue = Number(breakMin);
  const breakInvalid = !Number.isInteger(breakValue) || breakValue < 0 || breakValue > 720;
  const completed = s.today_record?.net_minutes ?? 0;
  const worked = completed + (elapsed !== null ? Math.floor(elapsed / 60000) : 0);
  const target = s.schedule?.required_minutes ?? 0;
  const progress = target ? Math.min(100, Math.round((worked / target) * 100)) : 0;
  const lastOut = s.today_record?.last_check_out ?? null;
  const name = user ? (user.attendance_tracked ? user.full_name.split(" ")[0] : user.full_name) : null;
  const shift = s.schedule
    ? `${s.schedule.name} ${s.schedule.start_time.slice(0, 5)}–${s.schedule.end_time.slice(0, 5)}`
    : "No working schedule today";

  const checkButton = active ? (
    <Button variant="secondary" size="lg" icon={<LogOut className="size-4" />} loading={checkOut.isPending} disabled={pending} onClick={() => setConfirm(true)}>
      Check out
    </Button>
  ) : (
    <Button
      size="lg"
      icon={<LogIn className="size-4" />}
      loading={checkIn.isPending}
      disabled={pending}
      onClick={() =>
        checkIn.mutate(undefined, {
          onSuccess: (sess) => toast.success("Checked in at " + formatTime(sess.check_in_at)),
          onError: (e) => toast.error(e),
        })
      }
    >
      Check in
    </Button>
  );

  return (
    <Card className="overflow-hidden">
      <div className="flex flex-wrap items-start justify-between gap-4 tone-secondary p-5 sm:p-6">
        <div className="min-w-0">
          <p className="text-sm text-on-secondary-muted">
            {formatDate(s.today, { weekday: "long", year: undefined })} · {shift}
          </p>
          {name ? (
            <h1 className="mt-1 text-2xl font-semibold tracking-tight text-on-secondary">
              {greeting()}, {name}
            </h1>
          ) : (
            <h2 className="mt-1 text-lg font-semibold text-on-secondary">Today</h2>
          )}
          {user && (
            <p className="mt-1.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-on-secondary-muted">
              <span className="tabular">{user.employee_code}</span>
              <span aria-hidden className="text-on-secondary-muted">·</span>
              <span>{user.department?.name ?? "No department"}</span>
              {user.manager && (
                <>
                  <span aria-hidden className="text-on-secondary-muted">·</span>
                  <span>
                    Reports to <span className="text-on-secondary">{user.manager.full_name}</span>
                  </span>
                </>
              )}
            </p>
          )}
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <StatusBadge status={s.today_record?.status ?? null} live={!!active} />
          {checkButton}
        </div>
      </div>

      <dl className="grid grid-cols-1 divide-y divide-line border-t border-line bg-surface-2/60 sm:grid-cols-3 sm:divide-x sm:divide-y-0">
        <Tile
          label={active ? "Checked in at" : lastOut ? "Last check-out" : "Checked in at"}
          value={active ? formatTime(active.check_in_at) : lastOut ? formatTime(lastOut) : "—"}
          hint={active ? "Recorded by the server" : lastOut ? "You're checked out" : "You haven't checked in yet"}
        />
        <Tile
          label="Time in office"
          value={<span aria-live="off">{elapsed !== null ? clock(elapsed) : "—"}</span>}
          hint={active ? "Current session, live" : "Starts when you check in"}
        />
        <Tile
          label="Worked today"
          value={formatMinutes(worked)}
          hint={target ? `of ${formatMinutes(target)} shift hours${active ? " · includes current session" : ""}` : undefined}
        >
          {target > 0 && (
            <div
              role="progressbar"
              aria-label="Progress toward today's shift hours"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={progress}
              className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-surface-3"
            >
              <div className="h-full rounded-full bg-brand transition-[width]" style={{ width: progress + "%" }} />
            </div>
          )}
        </Tile>
      </dl>

      {s.unresolved_session && (
        <div role="alert" className="flex flex-wrap items-center gap-3 border-t border-line px-5 py-3 text-sm sm:px-6">
          <AlertTriangle className="size-4 shrink-0" style={{ color: "var(--st-incomplete)" }} aria-hidden />
          <span className="flex-1 text-ink">
            You didn't check out of the session that started {formatDateTime(s.unresolved_session.check_in_at)}. It isn't counted
            until it's corrected.
          </span>
          {onRequestCorrection && (
            <Button
              size="sm"
              variant="secondary"
              onClick={() => {
                const u = s.unresolved_session!;
                const day = new Intl.DateTimeFormat("en-CA", { timeZone: s.office_timezone }).format(new Date(u.check_in_at));
                onRequestCorrection(day, u.id);
              }}
            >
              Fix it now
            </Button>
          )}
        </div>
      )}

      {onFixPastDay && (
        <div className="flex flex-wrap items-center gap-1 border-t border-line px-3 py-2 sm:px-4">
          <Button variant="ghost" size="sm" icon={<ClipboardPen className="size-4" />} onClick={onFixPastDay}>
            Fix a past day
          </Button>
          <Link
            to="/my/requests"
            className="inline-flex h-8 items-center gap-1.5 rounded-lg px-3 text-xs font-medium text-ink-2 hover:bg-surface-2 hover:text-ink"
          >
            <ListChecks className="size-4" aria-hidden /> View my requests
          </Link>
        </div>
      )}

      <ConfirmDialog
        open={confirm}
        title="Check out now?"
        message={active ? `You checked in at ${formatTime(active.check_in_at)}. The check-out time is recorded by the server.` : undefined}
        confirmLabel="Check out"
        loading={checkOut.isPending}
        onCancel={() => setConfirm(false)}
        onConfirm={() => {
          if (breakInvalid) return;
          checkOut.mutate(breakValue, {
            onSuccess: (sess) => {
              toast.success("Checked out · " + formatMinutes(sess.net_minutes) + " worked");
              setConfirm(false);
              setBreakMin("0");
            },
            onError: (e) => {
              toast.error(e);
              setConfirm(false);
            },
          });
        }}
      >
        <div className="mt-3">
          <Field
            label="Unpaid break during this session (minutes)"
            htmlFor="break"
            hint="Leave at 0 if you didn't take a break."
            error={breakInvalid ? "Enter whole minutes between 0 and 720" : undefined}
          >
            <Input id="break" type="number" min={0} max={720} inputMode="numeric" value={breakMin} onChange={(e) => setBreakMin(e.target.value)} aria-invalid={breakInvalid} />
          </Field>
        </div>
      </ConfirmDialog>
    </Card>
  );
}
