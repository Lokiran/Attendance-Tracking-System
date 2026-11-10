import { useQuery } from "@tanstack/react-query";
import { Building2, ChevronDown, ChevronRight } from "lucide-react";
import { useMemo, useState } from "react";
import { Link } from "react-router-dom";

import { Card } from "@/components/ui/Card";
import { Checkbox, SearchInput } from "@/components/ui/Fields";
import { Avatar } from "@/components/ui/Misc";
import { PageHeader } from "@/components/ui/PageHeader";
import { PageSkeleton } from "@/components/ui/Skeleton";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { api } from "@/lib/api";
import { ROLE_LABEL } from "@/lib/format";
import type { HierarchyDepartment, HierarchyNode } from "@/lib/types";

function matches(n: HierarchyNode, q: string): boolean {
  return n.full_name.toLowerCase().includes(q) || n.employee_code.toLowerCase().includes(q) || n.children.some((c) => matches(c, q));
}

const PAGE = 50;

/** Renders a long list of people 50 at a time, so a manager with hundreds of reports stays fast. */
function NodeList({ nodes, depth, q, expandAll }: { nodes: HierarchyNode[]; depth: number; q: string; expandAll: boolean }) {
  const [shown, setShown] = useState(PAGE);
  const rest = nodes.length - shown;
  return (
    <>
      {nodes.slice(0, shown).map((c) => (
        <Node key={c.id} node={c} depth={depth} q={q} expandAll={expandAll} />
      ))}
      {rest > 0 && (
        <li style={{ paddingLeft: depth * 20 + 36 }} className="py-1">
          <button type="button" onClick={() => setShown((n) => n + PAGE)} className="text-xs font-medium text-brand-text hover:underline">
            Show {Math.min(PAGE, rest)} more ({rest} not shown)
          </button>
        </li>
      )}
    </>
  );
}

function Node({ node, depth, q, expandAll }: { node: HierarchyNode; depth: number; q: string; expandAll: boolean }) {
  const [open, setOpen] = useState(depth < 1);
  const visibleKids = q ? node.children.filter((c) => matches(c, q)) : node.children;
  const isOpen = open || expandAll || !!q;
  const isManager = node.role !== "EMPLOYEE" && node.direct_reports > 0;
  return (
    <li role="treeitem" aria-expanded={visibleKids.length ? isOpen : undefined} aria-level={depth + 2}>
      <div className="flex items-center gap-2 rounded-lg py-1.5 pr-2 hover:bg-surface-2" style={{ paddingLeft: depth * 20 + 8 }}>
        {visibleKids.length ? (
          <button type="button" onClick={() => setOpen((o) => !o)} className="rounded p-0.5 text-ink-3 hover:text-ink" aria-label={(isOpen ? "Collapse " : "Expand ") + node.full_name}>
            {isOpen ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
          </button>
        ) : (
          <span className="w-5" aria-hidden />
        )}
        <Avatar name={node.full_name} size="sm" />
        <Link to={"/people/" + node.id} className="min-w-0 text-sm font-medium text-ink hover:underline">
          {node.full_name}
        </Link>
        <span className="hidden truncate text-xs text-ink-3 sm:inline">
          {node.employee_code} · {node.job_title ?? ROLE_LABEL[node.role]}
        </span>
        {isManager && (
          <Link to={"/admin/managers/" + node.id} className="ml-auto whitespace-nowrap rounded-md border border-line px-2 py-0.5 text-[11px] font-medium text-brand-text hover:bg-surface">
            Team dashboard · {node.direct_reports}
          </Link>
        )}
      </div>
      {isOpen && visibleKids.length > 0 && (
        <ul role="group">
          <NodeList nodes={visibleKids} depth={depth + 1} q={q} expandAll={expandAll} />
        </ul>
      )}
    </li>
  );
}

export function HierarchyPage() {
  const [q, setQ] = useState("");
  const [expandAll, setExpandAll] = useState(false);
  const data = useQuery({ queryKey: ["admin", "hierarchy"], queryFn: () => api.get<{ total_people: number; departments: HierarchyDepartment[] }>("/org/hierarchy") });
  const query = q.trim().toLowerCase();
  const departments = useMemo(
    () => (data.data?.departments ?? []).filter((d) => !query || d.name.toLowerCase().includes(query) || d.nodes.some((n) => matches(n, query))),
    [data.data, query],
  );
  if (data.isError) return <ErrorState error={data.error} onRetry={() => data.refetch()} />;
  if (!data.data) return <PageSkeleton />;
  return (
    <div className="space-y-5">
      <PageHeader
        title="Organisation hierarchy"
        description={`Nexa Technologies → departments → managers → employees · ${data.data.total_people} active people`}
        actions={
          <>
            <SearchInput value={q} onChange={setQ} placeholder="Find a person or department" label="Search hierarchy" />
            <Checkbox label="Expand all" checked={expandAll} onChange={(e) => setExpandAll(e.target.checked)} />
          </>
        }
      />
      {departments.length === 0 ? (
        <Card><EmptyState title="No matches" /></Card>
      ) : (
        <ul role="tree" aria-label="Organisation" className="space-y-4">
          {departments.map((d) => (
            <li key={String(d.id)} role="treeitem" aria-expanded aria-level={1}>
              <Card>
                <div className="flex flex-wrap items-center gap-3 border-b border-line px-4 py-3">
                  <Building2 className="size-4 text-ink-3" aria-hidden />
                  {d.id ? (
                    <Link to={"/admin/departments/" + d.id + "/dashboard"} className="text-sm font-semibold text-ink hover:underline">
                      {d.name}
                    </Link>
                  ) : (
                    <span className="text-sm font-semibold text-ink">{d.name}</span>
                  )}
                  <span className="text-xs text-ink-3">
                    {d.member_count} people{d.head && " · head: " + d.head.full_name}
                  </span>
                </div>
                <ul role="group" className="p-2">
                  <NodeList
                    nodes={query ? d.nodes.filter((n) => matches(n, query) || d.name.toLowerCase().includes(query)) : d.nodes}
                    depth={0}
                    q={d.name.toLowerCase().includes(query) ? "" : query}
                    expandAll={expandAll}
                  />
                  {d.nodes.length === 0 && <li className="px-3 py-2 text-xs text-ink-3">No members</li>}
                </ul>
              </Card>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
