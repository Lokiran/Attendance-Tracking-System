import { CalendarRange } from "lucide-react";
import { useId } from "react";

import { presetRange, todayInOffice, type RangePreset } from "@/lib/dates";

const PRESETS: { value: RangePreset; label: string }[] = [
  { value: "this_month", label: "This month" },
  { value: "last_month", label: "Last month" },
  { value: "last_7", label: "Last 7 days" },
  { value: "last_30", label: "Last 30 days" },
  { value: "last_90", label: "Last 90 days" },
  { value: "custom", label: "Custom" },
];

export function detectPreset(start: string, end: string): RangePreset {
  const today = todayInOffice();
  for (const p of PRESETS) {
    if (p.value === "custom") continue;
    const r = presetRange(p.value, today);
    if (r.start === start && r.end === end) return p.value;
  }
  return "custom";
}

export function DateRangeFilter({ start, end, onChange }: {
  start: string;
  end: string;
  onChange: (range: { start: string; end: string }) => void;
}) {
  const id = useId();
  const preset = detectPreset(start, end);
  const today = todayInOffice();
  const invalid = end < start;
  return (
    <fieldset className="flex flex-wrap items-center gap-2">
      <legend className="sr-only">Reporting period</legend>
      <CalendarRange className="size-4 text-ink-3" aria-hidden />
      <label htmlFor={id + "p"} className="sr-only">
        Preset
      </label>
      <select
        id={id + "p"}
        value={preset}
        onChange={(e) => {
          const v = e.target.value as RangePreset;
          if (v !== "custom") onChange(presetRange(v, today));
        }}
        className="h-9 rounded-lg border border-line-strong bg-surface px-2 text-sm text-ink"
      >
        {PRESETS.map((p) => (
          <option key={p.value} value={p.value}>
            {p.label}
          </option>
        ))}
      </select>
      <label htmlFor={id + "s"} className="sr-only">
        Start date
      </label>
      <input
        id={id + "s"}
        type="date"
        value={start}
        max={today}
        onChange={(e) => e.target.value && onChange({ start: e.target.value, end })}
        className="h-9 rounded-lg border border-line-strong bg-surface px-2 text-sm text-ink"
      />
      <span className="text-xs text-ink-3">to</span>
      <label htmlFor={id + "e"} className="sr-only">
        End date
      </label>
      <input
        id={id + "e"}
        type="date"
        value={end}
        min={start}
        onChange={(e) => e.target.value && onChange({ start, end: e.target.value })}
        aria-invalid={invalid}
        className="h-9 rounded-lg border border-line-strong bg-surface px-2 text-sm text-ink aria-[invalid=true]:border-critical"
      />
      {invalid && (
        <span role="alert" className="text-xs text-critical-text">
          End date is before start date
        </span>
      )}
    </fieldset>
  );
}
