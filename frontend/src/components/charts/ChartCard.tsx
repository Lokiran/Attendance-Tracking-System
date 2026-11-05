import { BarChart3, Table2 } from "lucide-react";
import { useState, type ReactNode } from "react";

import { Card, CardHeader } from "@/components/ui/Card";
import { ChartSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";

export interface TableView {
  columns: string[];
  rows: (string | number)[][];
}

/** Frame for every chart: title, loading/error/empty states, and an accessible table view. */
export function ChartCard({ title, subtitle, actions, loading, error, onRetry, isEmpty, emptyText, table, height = 260, children, legend }: {
  title: string;
  subtitle?: ReactNode;
  actions?: ReactNode;
  loading?: boolean;
  error?: unknown;
  onRetry?: () => void;
  isEmpty?: boolean;
  emptyText?: string;
  table?: TableView;
  height?: number;
  legend?: ReactNode;
  children: ReactNode;
}) {
  const [asTable, setAsTable] = useState(false);
  let body: ReactNode;
  if (error) body = <ErrorState error={error} onRetry={onRetry} />;
  else if (loading) body = <ChartSkeleton height={height} />;
  else if (isEmpty) body = <EmptyState title="No data for this period" description={emptyText ?? "Adjust the date range or filters."} />;
  else if (asTable && table)
    body = (
      <div className="max-h-[320px] overflow-auto px-4 pb-3">
        <table className="w-full text-xs">
          <thead className="sticky top-0 bg-surface">
            <tr className="border-b border-line text-left text-ink-3">
              {table.columns.map((c) => (
                <th key={c} scope="col" className="py-2 pr-3 font-medium">
                  {c}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {table.rows.map((r, i) => (
              <tr key={i} className="border-b border-line last:border-0">
                {r.map((v, j) => (
                  <td key={j} className="tabular py-1.5 pr-3 text-ink">
                    {v}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    );
  else
    body = (
      <div className="px-2 pb-3 pt-2">
        {legend && <div className="px-3 pb-2">{legend}</div>}
        <div style={{ height }}>{children}</div>
      </div>
    );

  return (
    <Card className="flex min-w-0 flex-col">
      <CardHeader
        title={title}
        subtitle={subtitle}
        actions={
          <>
            {actions}
            {table && !isEmpty && !loading && !error && (
              <button
                type="button"
                onClick={() => setAsTable((t) => !t)}
                className="rounded-md p-1.5 text-ink-3 hover:bg-surface-2 hover:text-ink"
                aria-label={asTable ? "Show chart" : "Show data table"}
                title={asTable ? "Show chart" : "Show data table"}
              >
                {asTable ? <BarChart3 className="size-4" /> : <Table2 className="size-4" />}
              </button>
            )}
          </>
        }
      />
      {body}
    </Card>
  );
}

export function Legend({ items }: { items: { label: string; color: string; dashed?: boolean }[] }) {
  return (
    <ul className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-ink-2">
      {items.map((i) => (
        <li key={i.label} className="inline-flex items-center gap-1.5">
          {i.dashed ? (
            <span aria-hidden className="inline-block w-4 border-t-2 border-dashed" style={{ borderColor: i.color }} />
          ) : (
            <span aria-hidden className="inline-block size-2.5 rounded-sm" style={{ background: i.color }} />
          )}
          {i.label}
        </li>
      ))}
    </ul>
  );
}
