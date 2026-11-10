/* eslint-disable @typescript-eslint/no-explicit-any */
import type { ReactNode } from "react";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  LabelList,
  Line,
  LineChart,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";

import { formatDate, formatMinutes, STATUS_LABEL } from "@/lib/format";
import type { DayRecord, DayStatus, TrendBucket } from "@/lib/types";

import { STATUS_META } from "../ui/StatusBadge";

const axis = { stroke: "var(--chart-axis)", fontSize: 11, tickLine: false, axisLine: false } as const;
const grid = <CartesianGrid stroke="var(--chart-grid)" vertical={false} />;
const cursor = { fill: "var(--surface-2)" };

/** Shared tooltip: values in text ink, a colour chip carries series identity. */
function Tip({ active, payload, label, title, rows }: {
  active?: boolean;
  payload?: any[];
  label?: any;
  title?: (label: any, payload: any[]) => ReactNode;
  rows?: (payload: any[]) => { label: string; value: string; color?: string }[];
}) {
  if (!active || !payload?.length) return null;
  const items = rows
    ? rows(payload)
    : payload.map((p) => ({ label: String(p.name), value: String(p.value), color: p.color ?? p.fill }));
  return (
    <div className="rounded-lg border border-line bg-surface px-3 py-2 text-xs shadow-lg">
      <div className="mb-1 font-medium text-ink">{title ? title(label, payload) : label}</div>
      {items.map((r) => (
        <div key={r.label} className="flex items-center gap-2 text-ink-2">
          {r.color && <span aria-hidden className="inline-block size-2 rounded-sm" style={{ background: r.color }} />}
          <span>{r.label}</span>
          <span className="tabular ml-auto pl-3 font-medium text-ink">{r.value}</span>
        </div>
      ))}
    </div>
  );
}

const shortDate = (d: string) => formatDate(d, { year: undefined });

/** Net hours per day (single series) with the required hours as a dashed reference. */
export function DailyHoursChart({ days, onSelectDay }: { days: DayRecord[]; onSelectDay?: (date: string) => void }) {
  const data = days.map((d) => ({ ...d, hours: +(d.net_minutes / 60).toFixed(2) }));
  const required = Math.max(0, ...days.map((d) => d.required_minutes)) / 60;
  return (
    <ResponsiveContainer>
      <BarChart data={data} margin={{ top: 8, right: 60, bottom: 0, left: -12 }}>
        {grid}
        <XAxis dataKey="date" tickFormatter={shortDate} {...axis} minTickGap={16} />
        <YAxis {...axis} unit="h" allowDecimals={false} />
        <Tooltip
          cursor={cursor}
          content={
            <Tip
              title={(l) => formatDate(l, { weekday: "short" })}
              rows={(p) => {
                const d = p[0].payload as DayRecord;
                return [
                  { label: "Net time", value: formatMinutes(d.net_minutes), color: "var(--series-1)" },
                  { label: "Required", value: formatMinutes(d.required_minutes) },
                  { label: "Status", value: STATUS_LABEL[d.status] },
                ];
              }}
            />
          }
        />
        {required > 0 && (
          <ReferenceLine y={required} stroke="var(--text-3)" strokeDasharray="4 4" label={{ value: "Required", position: "right", fill: "var(--text-3)", fontSize: 11 }} />
        )}
        <Bar
          dataKey="hours"
          name="Net hours"
          fill="var(--series-1)"
          radius={[4, 4, 0, 0]}
          maxBarSize={22}
          cursor={onSelectDay ? "pointer" : undefined}
          onClick={(d: any) => onSelectDay?.(d?.payload?.date ?? d?.date)}
        />
      </BarChart>
    </ResponsiveContainer>
  );
}

/** Actual (net) vs required hours per bucket: two series, one unit, one axis. */
export function ActualVsRequiredChart({ items }: { items: TrendBucket[] }) {
  // Required hours of the days whose hours the timesheet recorded, so actual and required compare like with like.
  const data = items.map((b) => ({ label: b.label, actual: +(b.net_minutes / 60).toFixed(1), required: +(b.hours_required_minutes / 60).toFixed(1) }));
  return (
    <ResponsiveContainer>
      <BarChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: -8 }} barGap={2}>
        {grid}
        <XAxis dataKey="label" {...axis} minTickGap={8} />
        <YAxis {...axis} unit="h" />
        <Tooltip cursor={cursor} content={<Tip rows={(p) => p.map((x) => ({ label: x.name, value: x.value + " h", color: x.color }))} />} />
        <Bar dataKey="actual" name="Actual (net)" fill="var(--series-1)" radius={[4, 4, 0, 0]} maxBarSize={20} />
        <Bar dataKey="required" name="Required" fill="var(--series-2)" radius={[4, 4, 0, 0]} maxBarSize={20} />
      </BarChart>
    </ResponsiveContainer>
  );
}

/** Attendance % over time (0–100). */
export function AttendanceTrendChart({ items, onSelect }: { items: TrendBucket[]; onSelect?: (b: TrendBucket) => void }) {
  const data = items.map((b) => ({ ...b, pct: b.attendance_percentage }));
  return (
    <ResponsiveContainer>
      <LineChart
        data={data}
        margin={{ top: 8, right: 12, bottom: 0, left: -12 }}
        onClick={(s: any) => {
          const idx = s?.activeTooltipIndex ?? s?.activeIndex;
          if (onSelect && idx !== undefined && idx !== null && data[Number(idx)]) onSelect(data[Number(idx)]);
        }}
      >
        {grid}
        <XAxis dataKey="label" {...axis} minTickGap={16} />
        <YAxis {...axis} domain={[0, 100]} unit="%" />
        <Tooltip
          cursor={{ stroke: "var(--border-strong)" }}
          content={
            <Tip
              rows={(p) => {
                const b = p[0].payload as TrendBucket;
                return [
                  { label: "Attendance", value: b.attendance_percentage === null ? "—" : b.attendance_percentage + "%", color: "var(--series-1)" },
                  { label: "Present / expected days", value: b.present_expected + " / " + b.expected },
                  { label: "Avg net / day", value: formatMinutes(b.average_net_minutes) },
                ];
              }}
            />
          }
        />
        <Line
          type="monotone"
          dataKey="pct"
          name="Attendance %"
          stroke="var(--series-1)"
          strokeWidth={2}
          dot={{ r: 4, strokeWidth: 2, stroke: "var(--surface)", fill: "var(--series-1)" }}
          activeDot={{ r: 6, strokeWidth: 2, stroke: "var(--surface)" }}
          connectNulls
          cursor={onSelect ? "pointer" : undefined}
        />
      </LineChart>
    </ResponsiveContainer>
  );
}

/** Day-status distribution as horizontal bars (status colours + icons in the legend/labels). */
export function StatusDistributionChart({ data, onSelect }: {
  data: { status: DayStatus; count: number }[];
  onSelect?: (s: DayStatus) => void;
}) {
  const rows = data.filter((d) => d.count > 0).map((d) => ({ ...d, label: STATUS_LABEL[d.status] }));
  return (
    <ResponsiveContainer>
      <BarChart data={rows} layout="vertical" margin={{ top: 4, right: 36, bottom: 0, left: 8 }}>
        <XAxis type="number" hide allowDecimals={false} />
        <YAxis type="category" dataKey="label" {...axis} width={96} />
        <Tooltip cursor={cursor} content={<Tip rows={(p) => [{ label: "Days", value: String(p[0].value), color: p[0].payload && STATUS_META[p[0].payload.status as DayStatus].color }]} />} />
        <Bar dataKey="count" name="Days" radius={[0, 4, 4, 0]} maxBarSize={18} cursor={onSelect ? "pointer" : undefined} onClick={(d: any) => onSelect?.(d?.payload?.status ?? d?.status)}>
          {rows.map((r) => (
            <Cell key={r.status} fill={STATUS_META[r.status].color} />
          ))}
          <LabelList dataKey="count" position="right" fill="var(--text-2)" fontSize={11} />
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  );
}

// Present / WFH / Absent: adjacent pairs validated with the dataviz palette checker (light and dark).
export const TEAM_STACK = [
  { key: "present", label: "Present", color: "var(--st-present)" },
  { key: "wfh", label: "WFH", color: "var(--st-wfh)" },
  { key: "absent", label: "Absent", color: "var(--st-absent)" },
] as const;

/** People per status per day, stacked; click a day to drill down. */
export function TeamDailyChart({ items, onSelectDate }: { items: TrendBucket[]; onSelectDate?: (d: string) => void }) {
  return (
    <ResponsiveContainer>
      <BarChart data={items} margin={{ top: 8, right: 8, bottom: 0, left: -16 }}>
        {grid}
        <XAxis dataKey="start" tickFormatter={shortDate} {...axis} minTickGap={16} />
        <YAxis {...axis} allowDecimals={false} />
        <Tooltip cursor={cursor} content={<Tip title={(l) => formatDate(l, { weekday: "short" })} rows={(p) => [...p].reverse().map((x) => ({ label: x.name, value: String(x.value), color: x.color }))} />} />
        {TEAM_STACK.map((s, i) => (
          <Bar
            key={s.key}
            dataKey={s.key}
            name={s.label}
            stackId="a"
            fill={s.color}
            stroke="var(--surface)"
            strokeWidth={1}
            maxBarSize={26}
            radius={i === TEAM_STACK.length - 1 ? [4, 4, 0, 0] : undefined}
            cursor={onSelectDate ? "pointer" : undefined}
            onClick={(d: any) => onSelectDate?.(d?.payload?.start ?? d?.start)}
          />
        ))}
      </BarChart>
    </ResponsiveContainer>
  );
}

export const EXCEPTION_SERIES = [
  { key: "late", label: "Late arrival", color: "var(--series-1)" },
  { key: "short_hours", label: "Short hours", color: "var(--series-2)" },
] as const;

export function ExceptionTrendChart({ items }: { items: TrendBucket[] }) {
  return (
    <ResponsiveContainer>
      <BarChart data={items} margin={{ top: 8, right: 8, bottom: 0, left: -16 }}>
        {grid}
        <XAxis dataKey="label" {...axis} minTickGap={8} />
        <YAxis {...axis} allowDecimals={false} />
        <Tooltip cursor={cursor} content={<Tip rows={(p) => p.map((x) => ({ label: x.name, value: String(x.value), color: x.color }))} />} />
        {EXCEPTION_SERIES.map((s) => (
          <Bar key={s.key} dataKey={s.key} name={s.label} fill={s.color} radius={[4, 4, 0, 0]} maxBarSize={16} />
        ))}
      </BarChart>
    </ResponsiveContainer>
  );
}

/** Ranked horizontal bars for comparisons (employees, departments, managers). */
export function RankedBarChart({ data, unit, onSelect, color = "var(--series-1)", max }: {
  data: { id: number | string | null; label: string; value: number | null; hint?: string }[];
  unit: "h" | "%";
  onSelect?: (id: number | string | null) => void;
  color?: string;
  max?: number;
}) {
  const rows = data.filter((d) => d.value !== null);
  const fmt = (v: number) => (unit === "%" ? v.toFixed(1) + "%" : v.toFixed(1) + " h");
  return (
    <ResponsiveContainer>
      <BarChart data={rows} layout="vertical" margin={{ top: 4, right: 48, bottom: 0, left: 8 }}>
        <XAxis type="number" hide domain={[0, max ?? "auto"]} />
        <YAxis type="category" dataKey="label" {...axis} width={120} interval={0} />
        <Tooltip
          cursor={cursor}
          content={<Tip rows={(p) => [{ label: unit === "%" ? "Attendance" : "Avg net / day", value: fmt(p[0].value), color }, ...(p[0].payload.hint ? [{ label: "", value: p[0].payload.hint }] : [])]} />}
        />
        <Bar dataKey="value" fill={color} radius={[0, 4, 4, 0]} maxBarSize={16} cursor={onSelect ? "pointer" : undefined} onClick={(d: any) => onSelect?.(d?.payload?.id ?? d?.id)}>
          <LabelList dataKey="value" position="right" fill="var(--text-2)" fontSize={11} formatter={(v: any) => (typeof v === "number" ? fmt(v) : "")} />
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  );
}

/** Histogram of employees by attendance-percentage band. */
export function DistributionChart({ bins }: { bins: { label: string; count: number }[] }) {
  return (
    <ResponsiveContainer>
      <BarChart data={bins} margin={{ top: 16, right: 8, bottom: 0, left: -16 }}>
        {grid}
        <XAxis dataKey="label" {...axis} />
        <YAxis {...axis} allowDecimals={false} />
        <Tooltip cursor={cursor} content={<Tip rows={(p) => [{ label: "Employees", value: String(p[0].value), color: "var(--series-1)" }]} />} />
        <Bar dataKey="count" name="Employees" fill="var(--series-1)" radius={[4, 4, 0, 0]} maxBarSize={40}>
          <LabelList dataKey="count" position="top" fill="var(--text-2)" fontSize={11} />
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  );
}
