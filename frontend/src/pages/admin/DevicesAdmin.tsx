import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Building2, CheckCircle2, FileUp, Laptop, Pencil, Plus, Users, Wifi, WifiOff } from "lucide-react";
import { useEffect, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Card, CardHeader } from "@/components/ui/Card";
import { DataTable, Pagination, type Column } from "@/components/ui/DataTable";
import { Checkbox, Field, Input, Select, TextArea } from "@/components/ui/Fields";
import { Modal } from "@/components/ui/Modal";
import { PageHeader } from "@/components/ui/PageHeader";
import { PersonPicker } from "@/components/ui/PersonPicker";
import { EmptyState } from "@/components/ui/States";
import { CardGrid, SummaryCard } from "@/components/ui/SummaryCard";
import { useToast } from "@/components/ui/Toast";
import { api } from "@/lib/api";
import type { Device, DeviceImportItem, DeviceImportResult, OfficeSite, Page, RegisterSummary } from "@/lib/types";

const PAGE_SIZE = 25;

function useRefresh() {
  const qc = useQueryClient();
  return () => qc.invalidateQueries({ queryKey: ["devices"] });
}

// ------------------------------------------------------------------ offices

function OfficeForm({ site, open, onClose }: { site: OfficeSite | null; open: boolean; onClose: () => void }) {
  const toast = useToast();
  const refresh = useRefresh();
  const [name, setName] = useState("");
  const [code, setCode] = useState("");
  const [network, setNetwork] = useState("");
  const [address, setAddress] = useState("");
  const [active, setActive] = useState(true);
  useEffect(() => {
    if (!open) return;
    setName(site?.name ?? "");
    setCode(site?.code ?? "");
    setNetwork(site?.network_name ?? "");
    setAddress(site?.address ?? "");
    setActive(site?.is_active ?? true);
  }, [open, site]);
  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), code: code.trim(), network_name: network.trim() || null, address: address.trim() || null };
      return site
        ? api.patch<OfficeSite>(`/admin/sites/${site.id}`, { ...body, is_active: active })
        : api.post<OfficeSite>("/admin/sites", body);
    },
    onSuccess: (s) => { toast.success(`Saved ${s.name}`); refresh(); onClose(); },
    onError: (e) => toast.error(e),
  });
  const valid = name.trim().length >= 2 && /^[A-Za-z0-9_-]{1,16}$/.test(code.trim());
  return (
    <Modal open={open} onClose={onClose} title={site ? `Edit ${site.name}` : "Add office"}
      description="Each office has its own Wi-Fi network; reports are per office."
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button disabled={!valid} loading={save.isPending} onClick={() => save.mutate()}>Save</Button></>}>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Office name" htmlFor="site-name" required><Input id="site-name" value={name} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Short code" htmlFor="site-code" required hint="Letters, digits, - or _ (e.g. BLR-1)"><Input id="site-code" value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} /></Field>
        <Field label="Wi-Fi network name" htmlFor="site-network" hint="As it appears in the office's Wi-Fi logs"><Input id="site-network" value={network} onChange={(e) => setNetwork(e.target.value)} /></Field>
        <Field label="Address" htmlFor="site-address"><Input id="site-address" value={address} onChange={(e) => setAddress(e.target.value)} /></Field>
      </div>
      {site && <div className="mt-4"><Checkbox label="Active" checked={active} onChange={(e) => setActive(e.target.checked)} /></div>}
    </Modal>
  );
}

function OfficesCard() {
  const sites = useQuery({ queryKey: ["devices", "sites"], queryFn: () => api.get<OfficeSite[]>("/admin/sites") });
  const [editing, setEditing] = useState<OfficeSite | null>(null);
  const [open, setOpen] = useState(false);
  const columns: Column<OfficeSite>[] = [
    { key: "name", header: "Office", cell: (s) => <span className="font-medium text-ink">{s.name}</span> },
    { key: "code", header: "Code", cell: (s) => <span className="tabular">{s.code}</span> },
    { key: "net", header: "Wi-Fi network", cell: (s) => s.network_name ?? <span className="text-ink-3">Not set yet</span> },
    { key: "state", header: "Status", cell: (s) => (s.is_active ? "Active" : <span className="text-ink-3">Inactive</span>) },
    { key: "a", header: "", cell: (s) => <div className="flex justify-end"><Button size="sm" variant="ghost" icon={<Pencil className="size-3.5" />} aria-label={`Edit ${s.name}`} onClick={() => { setEditing(s); setOpen(true); }} /></div> },
  ];
  return (
    <Card>
      <CardHeader title="Offices" subtitle="Two offices, two Wi-Fi networks: each Wi-Fi log line is tied to its office"
        actions={<Button size="sm" icon={<Plus className="size-4" />} onClick={() => { setEditing(null); setOpen(true); }}>Add office</Button>} />
      <DataTable caption="Offices" columns={columns} rows={sites.data} rowKey={(s) => s.id} loading={sites.isFetching} error={sites.error} onRetry={() => sites.refetch()}
        empty={<EmptyState title="No offices yet" description="Add both offices; their Wi-Fi network names can be filled in once the equipment is installed." />} />
      <OfficeForm site={editing} open={open} onClose={() => setOpen(false)} />
    </Card>
  );
}

// ------------------------------------------------------------------ devices

function DeviceForm({ device, open, onClose }: { device: Device | null; open: boolean; onClose: () => void }) {
  const toast = useToast();
  const refresh = useRefresh();
  const [serial, setSerial] = useState("");
  const [mac, setMac] = useState("");
  const [owner, setOwner] = useState("");
  const [ownerLabel, setOwnerLabel] = useState("");
  const [assetTag, setAssetTag] = useState("");
  const [hostname, setHostname] = useState("");
  const [model, setModel] = useState("");
  const [notes, setNotes] = useState("");
  const [active, setActive] = useState(true);
  useEffect(() => {
    if (!open) return;
    setSerial(device?.serial_number ?? "");
    setMac(device?.mac_address ?? "");
    setOwner(device?.user ? String(device.user.id) : "");
    setOwnerLabel(device?.user ? `${device.user.full_name} (${device.user.employee_code})` : "");
    setAssetTag(device?.asset_tag ?? "");
    setHostname(device?.hostname ?? "");
    setModel(device?.model ?? "");
    setNotes(device?.notes ?? "");
    setActive(device?.is_active ?? true);
  }, [open, device]);
  const save = useMutation({
    mutationFn: () => {
      const body = {
        serial_number: serial.trim() || null, mac_address: mac.trim() || null, user_id: owner ? Number(owner) : null,
        asset_tag: assetTag.trim() || null, hostname: hostname.trim() || null, model: model.trim() || null, notes: notes.trim() || null,
      };
      return device ? api.patch<Device>(`/admin/devices/${device.id}`, { ...body, is_active: active }) : api.post<Device>("/admin/devices", body);
    },
    onSuccess: (d) => { toast.success(`Saved ${d.serial_number ?? d.mac_address}`); refresh(); onClose(); },
    onError: (e) => toast.error(e),
  });
  return (
    <Modal open={open} onClose={onClose} size="lg" title={device ? "Edit device" : "Add device"}
      description="The serial number links the asset list to the Wi-Fi MAC address; either can be filled in first."
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button disabled={!serial.trim() && !mac.trim()} loading={save.isPending} onClick={() => save.mutate()}>Save</Button></>}>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Serial number" htmlFor="dev-serial"><Input id="dev-serial" value={serial} onChange={(e) => setSerial(e.target.value)} /></Field>
        <Field label="Wi-Fi MAC address" htmlFor="dev-mac" hint="e.g. AA:BB:CC:DD:EE:FF; leave blank until known"><Input id="dev-mac" value={mac} onChange={(e) => setMac(e.target.value)} /></Field>
        <Field label="Belongs to" htmlFor="dev-owner">
          <PersonPicker id="dev-owner" source="admin" value={owner} selectedLabel={ownerLabel} onChange={(id, label) => { setOwner(id); setOwnerLabel(label); }} />
        </Field>
        <Field label="Asset tag" htmlFor="dev-tag"><Input id="dev-tag" value={assetTag} onChange={(e) => setAssetTag(e.target.value)} /></Field>
        <Field label="Computer name" htmlFor="dev-host"><Input id="dev-host" value={hostname} onChange={(e) => setHostname(e.target.value)} /></Field>
        <Field label="Model" htmlFor="dev-model"><Input id="dev-model" value={model} onChange={(e) => setModel(e.target.value)} /></Field>
      </div>
      <div className="mt-4"><Field label="Notes" htmlFor="dev-notes"><TextArea id="dev-notes" rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} /></Field></div>
      {device && <div className="mt-4"><Checkbox label="In use (untick when the laptop is returned or replaced)" checked={active} onChange={(e) => setActive(e.target.checked)} /></div>}
    </Modal>
  );
}

const ACTION_LABEL: Record<DeviceImportItem["action"], string> = { create: "New", update: "Updated", unchanged: "No change", error: "Not imported" };
const FIELD_LABEL: Record<string, string> = { serial_number: "serial", mac_address: "MAC", hostname: "computer name", model: "model", asset_tag: "asset tag", user_id: "owner" };

function ImportDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const toast = useToast();
  const refresh = useRefresh();
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<DeviceImportResult | null>(null);
  useEffect(() => { if (!open) { setFile(null); setResult(null); } }, [open]);
  const run = useMutation({
    mutationFn: (apply: boolean) => api.upload<DeviceImportResult>("/admin/devices/import", file!, { apply }),
    onSuccess: (r) => {
      setResult(r);
      if (r.applied) { toast.success(`Saved: ${r.created} new, ${r.updated} updated`); refresh(); }
    },
    onError: (e) => toast.error(e),
  });
  const columns: Column<DeviceImportItem>[] = [
    { key: "row", header: "Row", align: "right", cell: (i) => <span className="tabular">{i.row}</span> },
    { key: "serial", header: "Serial", cell: (i) => i.serial_number ?? "—" },
    { key: "mac", header: "MAC", cell: (i) => <span className="tabular text-xs">{i.mac_address ?? "—"}</span> },
    { key: "owner", header: "Belongs to", cell: (i) => i.owner ?? <span className="text-ink-3">{i.owner_written ? `"${i.owner_written}"` : "—"}</span> },
    { key: "act", header: "Result", cell: (i) => (
      <span className="text-xs">
        <span className={i.action === "error" ? "font-medium text-critical-text" : "text-ink"}>{ACTION_LABEL[i.action]}</span>
        {i.changes.length > 0 && <span className="text-ink-3"> · {i.changes.map((c) => FIELD_LABEL[c] ?? c).join(", ")}</span>}
        {i.problems.map((p) => <span key={p} className="block text-ink-2">{p}</span>)}
      </span>) },
  ];
  const shown = result?.items.filter((i) => i.action !== "unchanged" || i.problems.length) ?? [];
  const canApply = !!result && !result.applied && result.created + result.updated > 0;
  return (
    <Modal open={open} onClose={onClose} size="lg" title="Import a device list"
      description="An asset list (serial number and employee) or an Intune export (serial number, Wi-Fi MAC, primary user). Either order works: rows are matched by serial number, and a blank cell never clears a value."
      footer={result?.applied
        ? <Button onClick={onClose}>Done</Button>
        : <><Button variant="ghost" onClick={onClose}>Cancel</Button>
          {result ? <Button disabled={!canApply} loading={run.isPending} onClick={() => run.mutate(true)}>Save {result.created + result.updated} device(s)</Button>
            : <Button disabled={!file} loading={run.isPending} icon={<FileUp className="size-4" />} onClick={() => run.mutate(false)}>Check file</Button>}</>}>
      {!result && (
        <Field label="File" htmlFor="dev-file" hint="Excel (.xlsx, .xls) or CSV. Columns are found by name: Serial number, Employee ID / Email / Name, Wi-Fi MAC, Device name, Model, Asset tag.">
          <Input id="dev-file" type="file" accept=".xlsx,.xlsm,.xls,.csv,.tsv,.txt" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        </Field>
      )}
      {result && (
        <div className="space-y-4">
          <p className="text-sm text-ink">
            {result.applied ? <CheckCircle2 className="mr-1.5 inline size-4 align-[-3px]" style={{ color: "var(--good)" }} aria-hidden /> : null}
            {result.applied ? "Saved. " : "Checked, nothing saved yet. "}
            <span className="tabular">{result.rows}</span> row(s){result.sheet ? ` on sheet "${result.sheet}"` : ""}: <b>{result.created}</b> new, <b>{result.updated}</b> updated, {result.unchanged} unchanged, <b className={result.errors ? "text-critical-text" : undefined}>{result.errors}</b> not imported.
          </p>
          <p className="text-xs text-ink-3">Columns read: {Object.entries(result.columns).map(([k, v]) => `${v} (${k})`).join(" · ")}</p>
          {result.random_mac > 0 && (
            <p className="flex gap-1.5 text-xs text-ink-2"><AlertTriangle className="mt-px size-3.5 shrink-0" style={{ color: "var(--warning)" }} aria-hidden />
              {result.random_mac} private (random) Wi-Fi address(es): these laptops need "Random hardware addresses" turned off, then the address read again.</p>
          )}
          {result.unknown_owners.length > 0 && (
            <p className="text-xs text-ink-2">No employee found for: {result.unknown_owners.map((u) => u.written).join(", ")}. Those devices are kept unassigned; assign them here or add the people first.</p>
          )}
          {shown.length > 0 && <div className="max-h-80 overflow-auto rounded-lg border border-line"><DataTable caption="Rows" columns={columns} rows={shown} rowKey={(i) => i.row} /></div>}
        </div>
      )}
    </Modal>
  );
}

type Filter = "all" | "unassigned" | "nomac";

function DevicesCard() {
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [retired, setRetired] = useState(false);
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<Device | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  useEffect(() => { const t = setTimeout(() => { setQuery(search.trim()); setPage(1); }, 250); return () => clearTimeout(t); }, [search]);
  const params = {
    page, page_size: PAGE_SIZE, search: query || undefined, active: !retired,
    assigned: filter === "unassigned" ? false : undefined, has_mac: filter === "nomac" ? false : undefined,
  };
  const list = useQuery({ queryKey: ["devices", "list", params], queryFn: () => api.get<Page<Device>>("/admin/devices", params), placeholderData: (p) => p });
  const columns: Column<Device>[] = [
    { key: "serial", header: "Serial number", cell: (d) => <span className="font-medium text-ink">{d.serial_number ?? "—"}{d.asset_tag && <span className="block text-xs font-normal text-ink-3">{d.asset_tag}</span>}</span> },
    { key: "owner", header: "Belongs to", cell: (d) => (d.user ? <span>{d.user.full_name}<span className="block text-xs text-ink-3">{d.user.employee_code}</span></span> : <span className="text-ink-3">Unassigned</span>) },
    { key: "mac", header: "Wi-Fi MAC", cell: (d) => (d.mac_address
      ? <span className="tabular inline-flex items-center gap-1.5 text-xs">{d.random_mac
        ? <AlertTriangle className="size-3.5" style={{ color: "var(--warning)" }} aria-label="Private (random) address" />
        : <Wifi className="size-3.5" style={{ color: "var(--good)" }} aria-hidden />}{d.mac_address}</span>
      : <span className="inline-flex items-center gap-1.5 text-xs text-ink-3"><WifiOff className="size-3.5" aria-hidden />Not yet</span>) },
    { key: "host", header: "Computer name", cell: (d) => d.hostname ?? "—" },
    { key: "model", header: "Model", cell: (d) => d.model ?? "—" },
    { key: "a", header: "", cell: (d) => <div className="flex justify-end"><Button size="sm" variant="ghost" icon={<Pencil className="size-3.5" />} aria-label={`Edit ${d.serial_number ?? d.mac_address}`} onClick={() => { setEditing(d); setFormOpen(true); }} /></div> },
  ];
  return (
    <Card>
      <CardHeader title="Devices" subtitle="Company laptops and who has each one"
        actions={<>
          <Button size="sm" variant="secondary" icon={<FileUp className="size-4" />} onClick={() => setImportOpen(true)}>Import list</Button>
          <Button size="sm" icon={<Plus className="size-4" />} onClick={() => { setEditing(null); setFormOpen(true); }}>Add device</Button>
        </>} />
      <div className="flex flex-wrap items-end gap-3 border-b border-line px-4 py-3 sm:px-5">
        <Field label="Search" htmlFor="dev-search"><Input id="dev-search" className="w-64" placeholder="Serial, MAC, name or employee ID" value={search} onChange={(e) => setSearch(e.target.value)} /></Field>
        <Field label="Show" htmlFor="dev-filter">
          <Select id="dev-filter" value={filter} onChange={(e) => { setFilter(e.target.value as Filter); setPage(1); }}>
            <option value="all">All devices</option>
            <option value="unassigned">Not assigned to anyone</option>
            <option value="nomac">No MAC address yet</option>
          </Select>
        </Field>
        <div className="pb-2"><Checkbox label="Returned / replaced" checked={retired} onChange={(e) => { setRetired(e.target.checked); setPage(1); }} /></div>
      </div>
      <DataTable caption="Devices" columns={columns} rows={list.data?.items} rowKey={(d) => d.id} loading={list.isFetching} error={list.error} onRetry={() => list.refetch()}
        empty={<EmptyState title="No devices" description="Import the asset list (serial number and employee) to start; MAC addresses can follow later." />} />
      {list.data && list.data.total > PAGE_SIZE && <div className="border-t border-line px-4 py-2"><Pagination page={page} pageSize={PAGE_SIZE} total={list.data.total} onPage={setPage} /></div>}
      <DeviceForm device={editing} open={formOpen} onClose={() => setFormOpen(false)} />
      <ImportDialog open={importOpen} onClose={() => setImportOpen(false)} />
    </Card>
  );
}

export function DevicesAdmin() {
  const summary = useQuery({ queryKey: ["devices", "summary"], queryFn: () => api.get<RegisterSummary>("/admin/devices/summary") });
  const s = summary.data;
  return (
    <div className="space-y-5">
      <PageHeader title="Offices & devices"
        description="The office Wi-Fi has one shared password, so it sees laptops, not people. This register says whose laptop each one is, by serial number and Wi-Fi MAC address." />
      <CardGrid cols={4}>
        <SummaryCard label="Devices" icon={<Laptop className="size-4" />} loading={!s} value={s?.devices} hint={s && `${s.with_mac} with a Wi-Fi MAC address`} />
        <SummaryCard label="No MAC address yet" icon={<WifiOff className="size-4" />} loading={!s} value={s?.without_mac} hint="cannot be matched to Wi-Fi logs yet" />
        <SummaryCard label="People covered" icon={<Users className="size-4" />} loading={!s} value={s && `${s.people_with_device} of ${s.people_tracked}`} hint="tracked people with a laptop that has a MAC address" />
        <SummaryCard label="Offices" icon={<Building2 className="size-4" />} loading={!s} value={s?.sites}
          hint={s && (s.sites === 0 ? "add both offices" : s.random_mac ? `${s.random_mac} private address(es) to fix`
            : s.unassigned ? `${s.unassigned} device(s) unassigned` : s.devices ? "every device has an owner" : "no devices yet")} />
      </CardGrid>
      <OfficesCard />
      <DevicesCard />
    </div>
  );
}
