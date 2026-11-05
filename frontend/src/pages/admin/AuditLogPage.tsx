import { keepPreviousData, useQuery } from "@tanstack/react-query";

import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, Pagination, type Column } from "@/components/ui/DataTable";
import { Input, SearchInput, Select } from "@/components/ui/Fields";
import { PageHeader } from "@/components/ui/PageHeader";
import { EmptyState } from "@/components/ui/States";
import { useUrlState } from "@/hooks/useUrlState";
import { api } from "@/lib/api";
import { formatDateTime, titleCase } from "@/lib/format";
import { qk } from "@/lib/queries";
import type { AuditEntry, Page } from "@/lib/types";

const ENTITY_TYPES = ["user", "department", "attendance_session", "attendance_correction", "work_schedule", "holiday", "attendance_policy", "report"];

function Changes({ before, after }: { before: Record<string, unknown> | null; after: Record<string, unknown> | null }) {
  const keys = Array.from(new Set([...Object.keys(before ?? {}), ...Object.keys(after ?? {})]));
  if (!keys.length) return <span className="text-xs text-ink-3">—</span>;
  return (
    <details className="text-xs">
      <summary className="cursor-pointer text-ink-2">{keys.length} field(s)</summary>
      <table className="mt-1">
        <tbody>
          {keys.map((k) => (
            <tr key={k} className="align-top">
              <td className="pr-2 font-medium text-ink-2">{k}</td>
              <td className="pr-2 text-ink-3 line-through">{before && k in before ? JSON.stringify(before[k]) : ""}</td>
              <td className="text-ink">{after && k in after ? JSON.stringify(after[k]) : ""}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </details>
  );
}

export function AuditLogPage() {
  const { get, set } = useUrlState();
  const page = Number(get("page", "1")) || 1;
  const params = { search: get("q"), entity_type: get("entity"), start: get("from"), end: get("to"), page, page_size: 25 };
  const q = useQuery({ queryKey: [...qk.admin, "audit", params], queryFn: () => api.get<Page<AuditEntry>>("/admin/audit-logs", params), placeholderData: keepPreviousData });
  const columns: Column<AuditEntry>[] = [
    { key: "when", header: "When", cell: (e) => <span className="tabular whitespace-nowrap text-xs">{formatDateTime(e.created_at)}</span> },
    { key: "actor", header: "Actor", cell: (e) => e.actor_name ?? <span className="text-ink-3">System</span> },
    { key: "action", header: "Action", cell: (e) => <span className="font-medium">{titleCase(e.action)}</span> },
    { key: "entity", header: "Entity", cell: (e) => <span className="text-xs">{titleCase(e.entity_type)} {e.entity_id && "#" + e.entity_id}</span> },
    { key: "changes", header: "Changes", cell: (e) => <Changes before={e.before_values} after={e.after_values} /> },
    { key: "req", header: "Request", cell: (e) => <span className="font-mono text-[11px] text-ink-3" title={e.ip_address ?? undefined}>{e.request_id?.slice(0, 8) ?? "—"}</span> },
  ];
  return (
    <div className="space-y-5">
      <PageHeader title="Audit log" description="Corrections, approvals, role and manager changes, schedule changes, exports and sign-ins. Credentials are never logged." />
      <Card>
        <CardHeader
          title={q.data ? q.data.total + " entries" : "Entries"}
          actions={
            <>
              <SearchInput value={get("q")} onChange={(v) => set({ q: v })} placeholder="Action, actor or entity ID" label="Search audit log" />
              <label htmlFor="a-entity" className="sr-only">Entity type</label>
              <Select id="a-entity" className="w-48" value={get("entity")} onChange={(e) => set({ entity: e.target.value })}>
                <option value="">All entities</option>
                {ENTITY_TYPES.map((t) => <option key={t} value={t}>{titleCase(t)}</option>)}
              </Select>
              <label htmlFor="a-from" className="sr-only">From</label>
              <Input id="a-from" type="date" className="w-40" value={get("from")} onChange={(e) => set({ from: e.target.value })} />
              <label htmlFor="a-to" className="sr-only">To</label>
              <Input id="a-to" type="date" className="w-40" value={get("to")} onChange={(e) => set({ to: e.target.value })} />
            </>
          }
        />
        <DataTable caption="Audit log" columns={columns} rows={q.data?.items} rowKey={(e) => e.id} loading={q.isFetching} error={q.error} onRetry={() => q.refetch()} empty={<EmptyState title="No audit entries match" />} />
        {q.data && <Pagination page={page} pageSize={25} total={q.data.total} onPage={(p) => set({ page: p }, false)} />}
      </Card>
    </div>
  );
}
