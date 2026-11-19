# Wi-Fi office presence

Goal: office days come from the office Wi-Fi instead of a monthly timesheet. Decided 2026-11-17 (Option A:
Wi-Fi logs plus a device register). Supersedes the Wi-Fi part of [automatic-attendance.md](automatic-attendance.md).

Legend: `[x]` done and verified · `[ ]` open · `[~]` blocked (reason given)

## Situation

- About 100–110 people, 2 offices, a separate Wi-Fi network in each; some people mostly work from home.
- **One shared Wi-Fi password**, so the logs show devices (MAC addresses), never people. A device register says
  whose laptop each device is. Laptops only.
- Assets are tracked by **serial number**. MAC addresses are not known yet; laptops are managed in Intune.
- The offices are on a shared (co-working) network today; the company's own Wi-Fi equipment arrives the week of
  2026-11-23. Its make and log format are not known yet.

## Rules (agreed)

- An office day = **at least 4 hours** on an office's Wi-Fi (4–8 hours is normal); less is a short office day that
  does not count toward the 12-day target. This is the existing minimum-office-minutes rule.
- **Reports per office.**
- A scheduled day with no Wi-Fi is **"Not in office"**; absent / WFH / leave are not told apart for now.
- Corrections: HR's existing single-day override (audited) until a process is agreed.

## Stage 1: Wi-Fi data by upload

### Phase 1: offices and device register ✅ (2026-11-17)

- [x] Migration 0006: `office_sites` (name, code, Wi-Fi network name) and `devices` (serial number, asset tag, MAC
      address, computer name, model, owner, in use); `wifi_events.site_id` / `device_id`; MAC addresses in
      `users.wifi_identity` copied into `devices` (column kept, unused)
- [x] Serial numbers and MAC addresses normalised (any separator; junk serials such as "To be filled by O.E.M."
      ignored); private / random MAC addresses flagged
- [x] Admin API: offices, devices (list, search, filters, create, edit, retire), register summary, audited
- [x] Import of an asset list or an Intune export (CSV / Excel), checked first, then saved: matched by serial number
      (then MAC); people by employee ID, email or a unique name; a blank cell never clears a value; problems listed
      by row
- [x] Admin page "Offices & devices" with the import dialog
- [x] Tests: 16 backend (identifiers, offices, devices, the two-file import, access), migration up / down / data
      carry-over, 2 frontend

### Phase 2: Wi-Fi log import and daily office minutes

- [~] Waiting for a sample of the new equipment's logs (both offices, a month with timesheets)
- [ ] Wi-Fi log reader (columns by name: MAC, computer name, time or connect / disconnect, network or access point)
- [ ] Preview: days per person and office, unregistered devices, problems; then commit
- [ ] Sessions per person per day (gaps such as lunch or a sleeping laptop bridged), first in, last out, minutes per
      office into `attendance_daily` (and a per-office table); WFO figures refreshed
- [ ] "Not in office" for scheduled days with no Wi-Fi, for people with a registered laptop; "No data" otherwise

### Phase 3: per-office reports

- [ ] Office filter and columns on dashboards, the month chart, WFO pages and the PDF / Excel / CSV exports
- [ ] Wi-Fi vs timesheet comparison report for the same month

## Stage 2: automatic collection

- [ ] Collector per office (or the vendor's cloud API) sending device, office and time over HTTPS with a key per
      office; nightly calculation; collector health ("No data" and an alert when an office goes quiet)
- [ ] Depends on IT: equipment, read-only API / syslog / DHCP export, a machine per office, hosting

## Stage 3: pilot

- [ ] One month alongside the timesheet, compared person by person, then Wi-Fi becomes the main source

## Later

- Individual Wi-Fi sign-in (WPA2/WPA3-Enterprise with Microsoft accounts): logs name people, the register becomes
  optional
- Register synced from Intune (Graph `DeviceManagementManagedDevices.Read.All`)

## Waiting on

| From | What |
|---|---|
| Company | The asset list (serial number → employee), to import now |
| IT | Intune export with Serial number and Wi-Fi MAC; real (not random) MAC addresses on laptops |
| IT, after installation | Equipment make / model per office, Wi-Fi network names, a sample of the connection logs |
| HR | Approval and notice to staff (DPDP Act 2023); raw data retention (assumed 90 days); managers' live view |
