import { clsx } from "clsx";

export function Skeleton({ className, style }: { className?: string; style?: React.CSSProperties }) {
  return <div aria-hidden style={style} className={clsx("animate-pulse rounded-md bg-surface-3", className)} />;
}

export function TableSkeleton({ rows = 6 }: { rows?: number }) {
  return (
    <div className="space-y-2 p-4" role="status" aria-label="Loading">
      {Array.from({ length: rows }, (_, i) => (
        <Skeleton key={i} className="h-8 w-full" />
      ))}
    </div>
  );
}

const BAR_HEIGHTS = [40, 65, 50, 80, 55, 70, 45, 60];

export function ChartSkeleton({ height = 240 }: { height?: number }) {
  return (
    <div role="status" aria-label="Loading chart" className="flex items-end gap-2 p-4" style={{ height }}>
      {BAR_HEIGHTS.map((h, i) => (
        <Skeleton key={i} className="flex-1" style={{ height: h + "%" }} />
      ))}
    </div>
  );
}

export function PageSkeleton() {
  return (
    <div className="space-y-4 p-6" role="status" aria-label="Loading page">
      <Skeleton className="h-8 w-64" />
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} className="h-24" />
        ))}
      </div>
      <Skeleton className="h-72" />
    </div>
  );
}
