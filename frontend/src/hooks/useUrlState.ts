import { useCallback } from "react";
import { useSearchParams } from "react-router-dom";

import { presetRange, todayInOffice } from "@/lib/dates";

/** Filters live in the URL so they survive navigation, reloads and drill-down links. */
export function useUrlState() {
  const [params, setParams] = useSearchParams();
  const get = useCallback((key: string, fallback = "") => params.get(key) ?? fallback, [params]);
  const set = useCallback(
    (updates: Record<string, string | number | null | undefined>, resetPage = true) => {
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev);
          for (const [k, v] of Object.entries(updates)) {
            if (v === null || v === undefined || v === "") next.delete(k);
            else next.set(k, String(v));
          }
          if (resetPage && !("page" in updates)) next.delete("page");
          return next;
        },
        { replace: true },
      );
    },
    [setParams],
  );
  return { params, get, set };
}

export function useRangeParams(defaultPreset: "this_month" | "last_30" = "this_month") {
  const { get, set, params } = useUrlState();
  const fallback = presetRange(defaultPreset, todayInOffice());
  const start = get("start", fallback.start);
  const end = get("end", fallback.end);
  return {
    start,
    end,
    valid: start <= end,
    setRange: (r: { start: string; end: string }) => set({ start: r.start, end: r.end }),
    get,
    set,
    params,
  };
}

/** Carries the current period into drill-down links. */
export function withRange(path: string, start: string, end: string, extra: Record<string, string | number | undefined> = {}) {
  const qs = new URLSearchParams({ start, end });
  for (const [k, v] of Object.entries(extra)) if (v !== undefined && v !== "") qs.set(k, String(v));
  return path + "?" + qs.toString();
}
