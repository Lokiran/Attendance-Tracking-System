import { useQuery } from "@tanstack/react-query";
import { clsx } from "clsx";
import { Search, X } from "lucide-react";
import { useEffect, useId, useState } from "react";

import { api } from "@/lib/api";
import type { AdminUser, Page, TeamMember } from "@/lib/types";

import { Input } from "./Fields";

const LIMIT = 20;

interface Person {
  id: number;
  name: string;
  detail: string;
}

/**
 * Search-as-you-type person picker. It never downloads the whole organisation: each keystroke
 * (debounced) asks the server for at most 20 matches by name or employee ID.
 *
 * source "team": people in the signed-in manager's / admin's scope (/team/members).
 * source "admin": active user accounts (/admin/users, admin only).
 */
export function PersonPicker({ id, value, onChange, source, selectedLabel, placeholder = "Search by name or employee ID", disabled, includeInactive }: {
  id?: string;
  value: string;
  onChange: (id: string, label: string) => void;
  source: "team" | "admin";
  /** Name to show for an already-selected value (e.g. when editing). */
  selectedLabel?: string;
  placeholder?: string;
  disabled?: boolean;
  includeInactive?: boolean;
}) {
  const fallbackId = useId();
  const inputId = id ?? fallbackId;
  const listId = inputId + "-list";
  const [text, setText] = useState("");
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const [label, setLabel] = useState(selectedLabel ?? "");
  useEffect(() => setLabel(selectedLabel ?? ""), [selectedLabel]);
  useEffect(() => {
    const t = setTimeout(() => setQuery(text.trim()), 250);
    return () => clearTimeout(t);
  }, [text]);

  const results = useQuery({
    queryKey: ["person-picker", source, query, includeInactive ?? false],
    enabled: open,
    staleTime: 30_000,
    queryFn: async (): Promise<Person[]> => {
      if (source === "team") {
        const rows = await api.get<TeamMember[]>("/team/members", { search: query || undefined, limit: LIMIT, include_inactive: includeInactive || undefined });
        return rows.map((m) => ({ id: m.id, name: m.full_name, detail: [m.employee_code, m.department_name].filter(Boolean).join(" · ") }));
      }
      const page = await api.get<Page<AdminUser>>("/admin/users", { search: query || undefined, is_active: true, page_size: LIMIT });
      return page.items.map((u) => ({ id: u.id, name: u.full_name, detail: [u.employee_code, u.department?.name].filter(Boolean).join(" · ") }));
    },
  });
  const items = results.data ?? [];

  const choose = (p: Person) => {
    onChange(String(p.id), p.name);
    setLabel(p.name);
    setText("");
    setOpen(false);
  };

  if (value && !open) {
    return (
      <div className="flex h-9 items-center justify-between gap-2 rounded-lg border border-line bg-surface px-3 text-sm">
        <span className="truncate text-ink" id={inputId}>{label || "Selected person"}</span>
        {!disabled && (
          <button type="button" className="rounded p-0.5 text-ink-3 hover:bg-surface-2 hover:text-ink" aria-label={"Clear " + (label || "selection")}
            onClick={() => { onChange("", ""); setLabel(""); setOpen(true); }}>
            <X className="size-4" aria-hidden />
          </button>
        )}
      </div>
    );
  }

  return (
    <div className="relative">
      <Search className="pointer-events-none absolute left-2.5 top-2.5 size-4 text-ink-3" aria-hidden />
      <Input
        id={inputId}
        role="combobox"
        aria-expanded={open}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={open && items[active] ? `${listId}-${items[active].id}` : undefined}
        autoComplete="off"
        className="pl-8"
        placeholder={placeholder}
        disabled={disabled}
        value={text}
        onChange={(e) => { setText(e.target.value); setOpen(true); setActive(0); }}
        onFocus={() => setOpen(true)}
        onBlur={() => setTimeout(() => setOpen(false), 120)}
        onKeyDown={(e) => {
          if (e.key === "ArrowDown") { e.preventDefault(); setOpen(true); setActive((a) => Math.min(a + 1, items.length - 1)); }
          else if (e.key === "ArrowUp") { e.preventDefault(); setActive((a) => Math.max(a - 1, 0)); }
          else if (e.key === "Enter" && open && items[active]) { e.preventDefault(); choose(items[active]); }
          else if (e.key === "Escape") setOpen(false);
        }}
      />
      {open && (
        <ul id={listId} role="listbox" className="absolute z-20 mt-1 max-h-64 w-full overflow-auto rounded-lg border border-line bg-surface py-1 text-sm shadow-card">
          {results.isLoading ? (
            <li className="px-3 py-2 text-ink-3">Searching…</li>
          ) : results.isError ? (
            <li className="px-3 py-2 text-critical-text">Couldn't search. Try again.</li>
          ) : items.length === 0 ? (
            <li className="px-3 py-2 text-ink-3">No one matches “{query}”.</li>
          ) : (
            <>
              {items.map((p, i) => (
                <li
                  key={p.id}
                  id={`${listId}-${p.id}`}
                  role="option"
                  aria-selected={i === active}
                  className={clsx("cursor-pointer px-3 py-1.5", i === active ? "bg-brand-soft" : "hover:bg-surface-2")}
                  onMouseDown={(e) => { e.preventDefault(); choose(p); }}
                  onMouseEnter={() => setActive(i)}
                >
                  <span className="block text-ink">{p.name}</span>
                  {p.detail && <span className="block text-xs text-ink-3">{p.detail}</span>}
                </li>
              ))}
              {items.length === LIMIT && <li className="px-3 py-1.5 text-xs text-ink-3">Showing the first {LIMIT}. Type more to narrow down.</li>}
            </>
          )}
        </ul>
      )}
    </div>
  );
}
