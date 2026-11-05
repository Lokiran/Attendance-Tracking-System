import { clsx } from "clsx";
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight } from "lucide-react";
import type { ReactNode } from "react";

import { Button } from "./Button";
import { TableSkeleton } from "./Skeleton";
import { EmptyState, ErrorState } from "./States";

export interface Column<T> {
  key: string;
  header: string;
  cell: (row: T) => ReactNode;
  sortable?: boolean;
  align?: "left" | "right";
  className?: string;
}

export interface SortState {
  sort: string;
  order: "asc" | "desc";
}

/** Server-driven table: sorting/pagination state lives in the caller (usually the URL). */
export function DataTable<T>({
  columns,
  rows,
  rowKey,
  loading,
  error,
  onRetry,
  sort,
  onSort,
  onRowClick,
  empty,
  caption,
}: {
  columns: Column<T>[];
  rows: T[] | undefined;
  rowKey: (row: T) => string | number;
  loading?: boolean;
  error?: unknown;
  onRetry?: () => void;
  sort?: SortState;
  onSort?: (s: SortState) => void;
  onRowClick?: (row: T) => void;
  empty?: ReactNode;
  caption?: string;
}) {
  if (error) return <ErrorState error={error} onRetry={onRetry} />;
  if (loading && !rows) return <TableSkeleton />;
  if (!rows?.length) return <>{empty ?? <EmptyState title="No records found" description="Try a different period or filter." />}</>;

  return (
    <div className={clsx("overflow-x-auto", loading && "opacity-60 transition-opacity")}>
      <table className="w-full min-w-[640px] border-collapse text-sm">
        {caption && <caption className="sr-only">{caption}</caption>}
        <thead>
          <tr className="border-b border-line bg-secondary-soft text-left text-xs text-ink-2">
            {columns.map((c) => {
              const active = sort?.sort === c.key;
              const ariaSort = active ? (sort?.order === "asc" ? "ascending" : "descending") : undefined;
              return (
                <th key={c.key} scope="col" aria-sort={ariaSort} className={clsx("whitespace-nowrap px-4 py-2.5 font-medium", c.align === "right" && "text-right", c.className)}>
                  {c.sortable && onSort ? (
                    <button
                      type="button"
                      onClick={() => onSort({ sort: c.key, order: active && sort?.order === "asc" ? "desc" : "asc" })}
                      className="inline-flex items-center gap-1 hover:text-ink"
                    >
                      {c.header}
                      {active ? (
                        sort?.order === "asc" ? <ArrowUp className="size-3" aria-hidden /> : <ArrowDown className="size-3" aria-hidden />
                      ) : (
                        <ArrowUpDown className="size-3 opacity-50" aria-hidden />
                      )}
                    </button>
                  ) : (
                    c.header
                  )}
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr
              key={rowKey(row)}
              onClick={onRowClick ? () => onRowClick(row) : undefined}
              className={clsx("border-b border-line last:border-0", onRowClick && "cursor-pointer hover:bg-surface-2")}
            >
              {columns.map((c) => (
                <td key={c.key} className={clsx("px-4 py-2.5 align-middle text-ink", c.align === "right" && "tabular text-right", c.className)}>
                  {c.cell(row)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function Pagination({ page, pageSize, total, onPage }: {
  page: number;
  pageSize: number;
  total: number;
  onPage: (p: number) => void;
}) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (total === 0) return null;
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  return (
    <nav aria-label="Pagination" className="flex items-center justify-between gap-3 border-t border-line px-4 py-2.5 text-xs text-ink-2">
      <span className="tabular">
        {from}–{to} of {total}
      </span>
      <div className="flex items-center gap-1">
        <Button variant="ghost" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)} aria-label="Previous page" icon={<ChevronLeft className="size-4" />} />
        <span className="tabular px-2">
          Page {page} of {pages}
        </span>
        <Button variant="ghost" size="sm" disabled={page >= pages} onClick={() => onPage(page + 1)} aria-label="Next page" icon={<ChevronRight className="size-4" />} />
      </div>
    </nav>
  );
}
