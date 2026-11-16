import { clsx } from "clsx";
import { Download, RefreshCw } from "lucide-react";
import { useEffect, useRef, useState, type ReactNode } from "react";

import { api, type Params } from "@/lib/api";
import { formatTime } from "@/lib/format";

import { Button } from "./Button";
import { useToast } from "./Toast";

/** Downloads a CSV export; authorization is enforced by the same backend rules as the dashboards. */
export function ExportButton({ path, params, label = "Export CSV", size = "md" }: {
  path: string;
  params?: Params;
  label?: string;
  size?: "sm" | "md";
}) {
  const toast = useToast();
  const [busy, setBusy] = useState(false);
  return (
    <Button
      variant="secondary"
      size={size}
      loading={busy}
      icon={<Download className="size-4" aria-hidden />}
      onClick={async () => {
        setBusy(true);
        try {
          const name = await api.download(path, params);
          toast.success("Downloaded " + name);
        } catch (e) {
          toast.error(e);
        } finally {
          setBusy(false);
        }
      }}
    >
      {label}
    </Button>
  );
}

export interface ExportOption {
  label: string;
  /** What the file is for, shown under the label. */
  hint?: string;
  path: string;
  icon?: ReactNode;
}

/** One "Export" button that opens a small menu of file formats (Excel, PDF, CSV). */
export function ExportMenu({ options, params, label = "Export" }: { options: ExportOption[]; params?: Params; label?: string }) {
  const toast = useToast();
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const away = (e: MouseEvent) => { if (!root.current?.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", away);
    document.addEventListener("keydown", esc);
    return () => { document.removeEventListener("mousedown", away); document.removeEventListener("keydown", esc); };
  }, [open]);
  const run = async (o: ExportOption) => {
    setOpen(false);
    setBusy(o.path);
    try {
      toast.success("Downloaded " + (await api.download(o.path, params)));
    } catch (e) {
      toast.error(e);
    } finally {
      setBusy(null);
    }
  };
  return (
    <div ref={root} className="relative">
      <Button variant="secondary" loading={busy !== null} icon={<Download className="size-4" aria-hidden />}
        aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((v) => !v)}>
        {label}
      </Button>
      {open && (
        <div role="menu" aria-label={label} className="absolute right-0 z-20 mt-1.5 w-64 rounded-xl border border-line bg-surface p-1.5 shadow-lg">
          {options.map((o) => (
            <button key={o.path} type="button" role="menuitem" onClick={() => run(o)}
              className="flex w-full items-start gap-2.5 rounded-lg px-2.5 py-2 text-left hover:bg-surface-2">
              {o.icon && <span className="mt-0.5 text-ink-3" aria-hidden>{o.icon}</span>}
              <span className="min-w-0">
                <span className="block text-sm font-medium text-ink">{o.label}</span>
                {o.hint && <span className="block text-xs text-ink-3">{o.hint}</span>}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** "Updated 10:42 AM" with a manual refresh button. */
export function LastUpdated({ updatedAt, onRefresh, fetching }: {
  updatedAt: number | undefined;
  onRefresh: () => void;
  fetching?: boolean;
}) {
  const [, tick] = useState(0);
  useEffect(() => {
    const t = setInterval(() => tick((n) => n + 1), 30_000);
    return () => clearInterval(t);
  }, []);
  return (
    <div className="flex items-center gap-2 text-xs text-ink-3" aria-live="polite">
      <span>{updatedAt ? "Updated " + formatTime(new Date(updatedAt).toISOString()) : "Loading…"}</span>
      <Button variant="ghost" size="sm" onClick={onRefresh} aria-label="Refresh data" icon={<RefreshCw className={clsx("size-3.5", fetching && "animate-spin")} />} />
    </div>
  );
}

export function Tabs<T extends string>({ value, onChange, options, label }: {
  value: T;
  onChange: (v: T) => void;
  options: { value: T; label: ReactNode }[];
  label: string;
}) {
  return (
    <div role="tablist" aria-label={label} className="inline-flex rounded-lg border border-line bg-surface-2 p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          role="tab"
          type="button"
          aria-selected={value === o.value}
          onClick={() => onChange(o.value)}
          className={clsx(
            "rounded-md px-3 py-1 text-xs font-medium transition-colors",
            value === o.value ? "bg-surface text-ink shadow-sm" : "text-ink-2 hover:text-ink",
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function Avatar({ name, size = "md" }: { name: string; size?: "sm" | "md" }) {
  const letters = name
    .split(" ")
    .filter(Boolean)
    .slice(0, 2)
    .map((p) => p[0]?.toUpperCase())
    .join("");
  return (
    <span
      aria-hidden
      className={clsx(
        "inline-flex shrink-0 items-center justify-center rounded-full bg-surface-3 font-semibold text-ink-2",
        size === "sm" ? "size-7 text-[11px]" : "size-9 text-xs",
      )}
    >
      {letters}
    </span>
  );
}
