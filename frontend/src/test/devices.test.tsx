import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";

import type { Device, DeviceImportResult, OfficeSite, RegisterSummary } from "@/lib/types";

import { me, mockApi, renderApp } from "./utils";

const summary: RegisterSummary = { devices: 3, with_mac: 1, without_mac: 2, unassigned: 1, random_mac: 0, people_tracked: 80, people_with_device: 1, sites: 2 };
const sites: OfficeSite[] = [
  { id: 1, name: "Head Office", code: "HO", network_name: null, address: null, is_active: true },
  { id: 2, name: "Branch Office", code: "BR", network_name: "Corp-BR", address: null, is_active: true },
];
const device = (id: number, over: Partial<Device> = {}): Device => ({
  id, serial_number: "SN-" + id, mac_address: null, random_mac: false, asset_tag: null, hostname: null, model: "ThinkPad",
  user: { id: 5, employee_code: "EMP001", full_name: "Rahul Reddy" }, is_active: true, source: "IMPORT", notes: null,
  updated_at: "2026-11-17T06:00:00Z", ...over,
});
const devices = [
  device(1, { mac_address: "00:1b:63:aa:00:01", hostname: "LT-RAHUL" }),
  device(2, { mac_address: "da:a1:19:aa:00:02", random_mac: true }),
  device(3, { user: null }),
];
const checked: DeviceImportResult = {
  applied: false, sheet: "Assets", columns: { serial: "Serial No.", name: "Employee Name" }, rows: 3, blank_rows: 0,
  created: 2, updated: 0, unchanged: 0, errors: 1, with_mac: 0, random_mac: 0, unknown_owners: [{ written: "NOPE-9", rows: 1 }],
  items: [
    { row: 4, action: "error", serial_number: null, mac_address: null, hostname: null, owner: null, owner_code: null, owner_written: "Arjun", changes: [], problems: ["no serial number or MAC address"] },
    { row: 2, action: "create", serial_number: "SN-9", mac_address: null, hostname: null, owner: "Sneha Rao", owner_code: "EMP002", owner_written: "Sneha Rao", changes: [], problems: [] },
  ],
};

describe("offices and devices", () => {
  it("shows how ready the register is, the offices and each laptop's Wi-Fi status", async () => {
    mockApi({
      "/auth/me": me("ADMIN", { id: 1, full_name: "System Administrator" }),
      "/admin/devices/summary": summary, "/admin/sites": sites,
      "/admin/devices": { items: devices, total: 3, page: 1, page_size: 25 },
    });
    renderApp("/admin/devices");
    expect(await screen.findByRole("heading", { name: "Offices & devices" })).toBeInTheDocument();
    expect(await screen.findByText("1 of 80")).toBeInTheDocument();        // people covered
    const offices = await screen.findByRole("table", { name: "Offices" });
    expect(within(offices).getByText("Not set yet")).toBeInTheDocument();   // Wi-Fi network still unknown
    const table = await screen.findByRole("table", { name: "Devices" });
    expect(within(table).getByText("00:1b:63:aa:00:01")).toBeInTheDocument();
    expect(within(table).getByLabelText("Private (random) address")).toBeInTheDocument();
    expect(within(table).getByText("Not yet")).toBeInTheDocument();
    expect(within(table).getByText("Unassigned")).toBeInTheDocument();
  });

  it("checks an asset list before saving it", async () => {
    const { calls } = mockApi({
      "/auth/me": me("ADMIN", { id: 1, full_name: "System Administrator" }),
      "/admin/devices/summary": summary, "/admin/sites": sites,
      "/admin/devices": { items: [], total: 0, page: 1, page_size: 25 },
      "POST /admin/devices/import": (url: URL) => (url.searchParams.get("apply") === "true" ? { ...checked, applied: true } : checked),
    });
    renderApp("/admin/devices");
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Import list" }));
    await user.upload(screen.getByLabelText("File"), new File(["x"], "assets.xlsx"));
    await user.click(screen.getByRole("button", { name: "Check file" }));
    expect(await screen.findByText(/Checked, nothing saved yet/)).toBeInTheDocument();
    expect(screen.getByText(/No employee found for: NOPE-9/)).toBeInTheDocument();
    expect(screen.getByText("no serial number or MAC address")).toBeInTheDocument();
    expect(calls.filter((c) => c.path === "/admin/devices/import")).toHaveLength(1);

    await user.click(screen.getByRole("button", { name: "Save 2 device(s)" }));
    expect(await screen.findByText(/^Saved\./)).toBeInTheDocument();
    await waitFor(() => expect(calls.filter((c) => c.path === "/admin/devices/import")).toHaveLength(2));
  });
});
