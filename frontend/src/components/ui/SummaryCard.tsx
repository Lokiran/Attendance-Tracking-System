import { clsx } from "clsx";
import type { ReactNode } from "react";

import { Skeleton } from "./Skeleton";

export function SummaryCard({ label, value, hint, icon, loading, onClick, active }: {
  label: string;
  value: ReactNode;
  hint?: ReactNode;
  icon?: ReactNode;
  loading?: boolean;
  /** When set, the card becomes a drill-down button. */
  onClick?: () => void;
  active?: boolean;
}) {
  const body = (
    <>
      <div className="flex items-center justify-between gap-2">
        <span className="text-xs font-medium text-ink-2">{label}</span>
        {icon && <span className="rounded-md bg-brand-soft p-1.5 text-brand-text" aria-hidden>{icon}</span>}
      </div>
      {loading ? (
        <Skeleton className="mt-2 h-7 w-20" />
      ) : (
        <div className="tabular mt-1.5 text-2xl font-semibold tracking-tight text-ink">{value}</div>
      )}
      {hint && !loading && <div className="mt-1 text-xs text-ink-3">{hint}</div>}
    </>
  );
  const cls = clsx("rounded-xl border bg-surface p-4 text-left shadow-card", active ? "border-brand ring-1 ring-brand" : "border-line");
  return onClick ? (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={clsx(cls, "transition-colors hover:border-line-strong hover:bg-surface-2")}
    >
      {body}
    </button>
  ) : (
    <div className={cls}>{body}</div>
  );
}

export function CardGrid({ children, cols = 6 }: { children: ReactNode; cols?: 4 | 6 | 7 }) {
  const map = { 4: "lg:grid-cols-4", 6: "lg:grid-cols-6", 7: "lg:grid-cols-4 xl:grid-cols-7" };
  return <div className={clsx("grid grid-cols-2 gap-3 sm:grid-cols-3", map[cols])}>{children}</div>;
}
