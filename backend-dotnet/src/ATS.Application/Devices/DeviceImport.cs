using Ats.Application.Audit;
using Ats.Application.Common;
using Ats.Application.Python;
using Ats.Application.Uploads;
using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Devices;

/// <summary>The asset-list / Intune import of the device register (<c>devices.import_devices</c>).</summary>
public sealed partial class DeviceService
{
    private static readonly (string Key, string[] Aliases)[] AssetColumns =
    [
        ("serial", ["serial", "serial number", "serial no", "serial num", "serialnumber", "sn", "s n", "device serial", "device serial number",
                    "asset serial", "asset serial number", "service tag"]),
        ("asset_tag", ["asset tag", "asset id", "asset no", "asset number", "asset code", "tag"]),
        ("employee", ["employee id", "emp id", "employee code", "emp code", "emp no", "employee no", "employee number", "code", "staff id",
                      "associate id"]),
        ("email", ["email", "e mail", "user email", "email address", "primary user upn", "primary user email address", "primary user email",
                   "user principal name", "upn", "assigned user email"]),
        ("name", ["employee name", "emp name", "name", "user", "user name", "assigned to", "assigned user", "owner", "primary user display name",
                  "full name", "employee"]),
        ("mac", ["wi fi mac", "wifi mac", "wi fi mac address", "wifi mac address", "wireless mac", "wlan mac", "mac", "mac address", "mac id"]),
        ("hostname", ["device name", "hostname", "host name", "computer name", "machine name", "laptop name"]),
        ("model", ["model", "device model", "laptop model"]),
    ];

    /// <summary>Normalised header → column key; a later alias overwrites an earlier one, as Python's dict comprehension does.</summary>
    private static readonly Dictionary<string, string> Alias = BuildAlias();

    private static Dictionary<string, string> BuildAlias()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, aliases) in AssetColumns)
        {
            foreach (var a in aliases)
            {
                map[WfoMapping.NormalizeHeader(a)] = key;
            }
        }

        return map;
    }

    private static (List<List<object?>> Rows, string? Sheet) ReadRows(string filename, byte[] content)
    {
        var name = filename.ToLowerInvariant();
        if (content.Length > TimesheetFiles.MaxUploadBytes)
        {
            throw new ValidationFailedException($"The file is larger than {TimesheetFiles.MaxUploadBytes / (1024 * 1024)} MB");
        }

        if (content.Length == 0)
        {
            throw new ValidationFailedException("The file is empty");
        }

        if (name.EndsWith(".csv", StringComparison.Ordinal) || name.EndsWith(".tsv", StringComparison.Ordinal) || name.EndsWith(".txt", StringComparison.Ordinal))
        {
            return (PyCsv.Read(content), null);
        }

        if (name.EndsWith(".xlsx", StringComparison.Ordinal) || name.EndsWith(".xlsm", StringComparison.Ordinal) || name.EndsWith(".xls", StringComparison.Ordinal))
        {
            var sheets = name.EndsWith(".xls", StringComparison.Ordinal) ? TimesheetFiles.XlsSheets(content) : TimesheetFiles.XlsxSheets(content);
            foreach (var (sheet, rows) in sheets)
            {
                if (FindHeader(rows) is not null)
                {
                    return (rows, sheet);
                }
            }

            return sheets.Count > 0 ? (sheets.GetAt(0).Value, sheets.GetAt(0).Key) : ([], null);
        }

        throw new ValidationFailedException($"Upload the device list as {TimesheetFiles.AcceptedText.Replace(" or PDF", "", StringComparison.Ordinal)}");
    }

    private static (int Row, OrderedDictionary<string, int> Columns)? FindHeader(List<List<object?>> rows)
    {
        for (var i = 0; i < Math.Min(rows.Count, HeaderSearchRows); i++)
        {
            var cols = new OrderedDictionary<string, int>(StringComparer.Ordinal);
            for (var j = 0; j < rows[i].Count; j++)
            {
                if (Alias.TryGetValue(WfoMapping.NormalizeHeader(PyText.CellText(rows[i][j])), out var key))
                {
                    cols.TryAdd(key, j);
                }
            }

            if ((cols.ContainsKey("serial") || cols.ContainsKey("mac"))
                && (cols.ContainsKey("employee") || cols.ContainsKey("email") || cols.ContainsKey("name") || cols.ContainsKey("mac")))
            {
                return (i, cols);
            }
        }

        return null;
    }

    private sealed class People(Dictionary<string, User> byCode, Dictionary<string, User> byEmail, Dictionary<string, List<User>> byName)
    {
        public static async Task<People> LoadAsync(Data.IAtsDb db, CancellationToken ct)
        {
            var users = await db.Users.OrderBy(u => u.Id).ToListAsync(ct).ConfigureAwait(false);
            var byCode = new Dictionary<string, User>(StringComparer.Ordinal);
            var byEmail = new Dictionary<string, User>(StringComparer.Ordinal);
            var byName = new Dictionary<string, List<User>>(StringComparer.Ordinal);
            foreach (var u in users)
            {
                byCode[PyText.SplitJoin(u.EmployeeCode).ToLowerInvariant()] = u;
                byEmail[u.Email.ToLowerInvariant()] = u;
                var key = WfoMapping.Normalize(u.FullName);
                if (!byName.TryGetValue(key, out var list))
                {
                    byName[key] = list = [];
                }

                list.Add(u);
            }

            return new People(byCode, byEmail, byName);
        }

        /// <summary>(the person, how they were written): null when nobody matches or a name is shared.</summary>
        public (User? Owner, string? Written) Find(string? employee, string? email, string? name)
        {
            foreach (var reference in new[] { employee, email })
            {
                if (string.IsNullOrEmpty(reference))
                {
                    continue;
                }

                var key = PyText.SplitJoin(reference).ToLowerInvariant();
                var hit = key.Contains('@', StringComparison.Ordinal) ? byEmail.GetValueOrDefault(key) : byCode.GetValueOrDefault(key);
                if (hit is not null)
                {
                    return (hit, reference);
                }
            }

            if (!string.IsNullOrEmpty(name) && byName.TryGetValue(WfoMapping.Normalize(name), out var same) && same.Count == 1)
            {
                return (same[0], name);
            }

            return (null, employee ?? email ?? name);
        }
    }

    private sealed class PlanRow(int row)
    {
        public int Row { get; } = row;

        public string? Serial { get; set; }

        public string? Mac { get; set; }

        public string? Hostname { get; set; }

        public string? Model { get; set; }

        public string? AssetTag { get; set; }

        public string? OwnerWritten { get; set; }

        public User? Owner { get; set; }

        public string Action { get; set; } = "unchanged";

        public SortedDictionary<string, object?> Changes { get; } = new(StringComparer.Ordinal);

        public List<string> Problems { get; set; } = [];

        public Device? Device { get; set; }
    }

    private async Task<(List<PlanRow> Plan, Dictionary<string, string> Columns, int Blank)> PlanAsync(List<List<object?>> rows, CancellationToken ct)
    {
        var found = FindHeader(rows);
        if (found is null)
        {
            var first = rows.FirstOrDefault(r => r.Any(c => PyText.CellText(c).Length > 0)) ?? [];
            var seen = string.Join(", ", first.Select(PyText.CellText).Where(s => s.Length > 0));
            throw new ValidationFailedException("Could not find a serial number or MAC address column, and an employee column, in the first rows. Found: "
                                                + (seen.Length > 0 ? seen : "nothing"));
        }

        var (at, cols) = found.Value;
        var body = rows.Skip(at + 1).ToList();
        if (body.Count > MaxRows)
        {
            throw new ValidationFailedException($"At most {MaxRows} rows per file");
        }

        var people = await People.LoadAsync(db, ct).ConfigureAwait(false);
        var devices = await db.Devices.Include(d => d.User).ToListAsync(ct).ConfigureAwait(false);
        var bySerial = new Dictionary<string, Device>(StringComparer.Ordinal);
        var byMac = new Dictionary<string, Device>(StringComparer.Ordinal);
        foreach (var d in devices.OrderBy(d => d.Id))
        {
            if (d.SerialNumber is { } s)
            {
                bySerial[s] = d;
            }

            if (d.MacAddress is { } m)
            {
                byMac[m] = d;
            }
        }

        var seenSerial = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenMac = new Dictionary<string, int>(StringComparer.Ordinal);
        var output = new List<PlanRow>();
        var blank = 0;
        object? Cell(List<object?> r, string key) => cols.TryGetValue(key, out var i) && i < r.Count ? r[i] : null;
        var n = at + 1;
        foreach (var r in body)
        {
            n++;
            if (!cols.Keys.Any(k => PyText.CellText(Cell(r, k)).Length > 0))
            {
                blank++;
                continue;
            }

            var p = new PlanRow(n)
            {
                Serial = NormalizeSerial(Cell(r, "serial")), Hostname = Clean(Cell(r, "hostname"), 64), Model = Clean(Cell(r, "model"), 120),
                AssetTag = Clean(Cell(r, "asset_tag"), 64),
            };
            output.Add(p);
            void Error(string problem) => (p.Action, p.Problems) = ("error", [problem]);
            try
            {
                p.Mac = NormalizeMac(Cell(r, "mac"));
            }
            catch (RowValueException e)
            {
                Error(e.Message);
                continue;
            }

            if (p.Serial is null && p.Mac is null)
            {
                Error("no serial number or MAC address");
                continue;
            }

            if (p.Serial is { } serial && seenSerial.TryGetValue(serial, out var firstSerial))
            {
                Error($"serial number repeated (first on row {firstSerial})");
                continue;
            }

            if (p.Mac is { } mac && seenMac.TryGetValue(mac, out var firstMac))
            {
                Error($"MAC address repeated (first on row {firstMac})");
                continue;
            }

            var (employee, email, name) = (Clean(Cell(r, "employee"), 255), Clean(Cell(r, "email"), 255), Clean(Cell(r, "name"), 255));
            (p.Owner, p.OwnerWritten) = people.Find(employee, email, name);
            if (p.Owner is null && p.OwnerWritten is not null)
            {
                p.Problems.Add($"no employee matches {PyFormat.Repr(p.OwnerWritten)}: the device is kept unassigned");
            }

            if (IsRandomMac(p.Mac))
            {
                p.Problems.Add("private (random) Wi-Fi address: it can change and will not identify the laptop; turn off random hardware addresses and read the address again");
            }

            var existing = p.Serial is not null ? bySerial.GetValueOrDefault(p.Serial) : null;
            if (existing is null && p.Mac is not null)
            {
                existing = byMac.GetValueOrDefault(p.Mac);
                if (existing?.SerialNumber is { } other && p.Serial is not null && other != p.Serial)
                {
                    Error($"MAC address already belongs to serial {other}");
                    continue;
                }
            }

            if (p.Mac is not null && existing is not null && byMac.GetValueOrDefault(p.Mac) is { } owner && owner != existing)
            {
                Error($"MAC address already belongs to serial {owner.SerialNumber ?? owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                continue;
            }

            if (p.Serial is not null)
            {
                seenSerial[p.Serial] = n;
            }

            if (p.Mac is not null)
            {
                seenMac[p.Mac] = n;
            }

            p.Device = existing;
            if (existing is null)
            {
                p.Action = "create";
                continue;
            }

            var wanted = new (string Field, object? Value, object? Current)[]
            {
                ("serial_number", p.Serial, existing.SerialNumber), ("mac_address", p.Mac, existing.MacAddress), ("hostname", p.Hostname, existing.Hostname),
                ("model", p.Model, existing.Model), ("asset_tag", p.AssetTag, existing.AssetTag), ("user_id", p.Owner?.Id, existing.UserId),
            };
            foreach (var (field, value, current) in wanted)
            {
                if (value is not null && !Equals(current, value)) // a blank cell never clears a value
                {
                    p.Changes[field] = value;
                }
            }

            if (p.Changes.ContainsKey("user_id") && existing.UserId is not null)
            {
                var was = existing.User is { } u ? u.FullName : existing.UserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                p.Problems.Add($"owner changes from {was} to {p.Owner!.FullName}");
            }

            p.Action = p.Changes.Count > 0 ? "update" : "unchanged";
        }

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, i) in cols)
        {
            columns[k] = PyText.CellText(rows[at][i]);
        }

        return (output, columns, blank);
    }

    private static DeviceImportResult Result(List<PlanRow> plan, Dictionary<string, string> columns, int blank, string? sheet, bool applied)
    {
        var unknown = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in plan.Where(p => p.Owner is null && p.OwnerWritten is not null && p.Action != "error"))
        {
            unknown[p.OwnerWritten!] = unknown.GetValueOrDefault(p.OwnerWritten!) + 1;
        }

        var ok = plan.Where(p => p.Action != "error").ToList();
        return new DeviceImportResult(
            applied, sheet, columns, plan.Count, blank, plan.Count(p => p.Action == "create"), plan.Count(p => p.Action == "update"),
            plan.Count(p => p.Action == "unchanged"), plan.Count(p => p.Action == "error"),
            ok.Count(p => p.Mac is not null || p.Device?.MacAddress is not null), ok.Count(p => IsRandomMac(p.Mac)),
            [.. unknown.Select((kv, i) => (kv, i)).OrderByDescending(x => x.kv.Value).ThenBy(x => x.i).Take(200).Select(x => new UnknownOwner(x.kv.Key, x.kv.Value))],
            [.. plan.OrderBy(p => p.Action != "error").ThenBy(p => p.Problems.Count == 0).ThenBy(p => p.Row).Take(500)
                .Select(p => new DeviceImportItem(p.Row, p.Action, p.Serial, p.Mac, p.Hostname, p.Owner?.FullName, p.Owner?.EmployeeCode, p.OwnerWritten,
                                                  [.. p.Changes.Keys], p.Problems))]);
    }

    /// <summary>Check (<paramref name="apply"/> false: nothing is written) or write an asset list / Intune export.</summary>
    public async Task<DeviceImportResult> ImportAsync(User actor, string filename, byte[] content, bool apply, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(content);
        var (rows, sheet) = ReadRows(filename, content);
        var (plan, columns, blank) = await PlanAsync(rows, ct).ConfigureAwait(false);
        if (!apply)
        {
            return Result(plan, columns, blank, sheet, applied: false);
        }

        foreach (var p in plan)
        {
            if (p.Action == "create")
            {
                p.Device = new Device
                {
                    SerialNumber = p.Serial, MacAddress = p.Mac, Hostname = p.Hostname, Model = p.Model, AssetTag = p.AssetTag, UserId = p.Owner?.Id,
                    IsActive = true, Source = "IMPORT",
                };
                db.Devices.Add(p.Device);
            }
            else if (p.Action == "update")
            {
                foreach (var (field, value) in p.Changes)
                {
                    switch (field)
                    {
                        case "serial_number": p.Device!.SerialNumber = (string?)value; break;
                        case "mac_address": p.Device!.MacAddress = (string?)value; break;
                        case "hostname": p.Device!.Hostname = (string?)value; break;
                        case "model": p.Device!.Model = (string?)value; break;
                        case "asset_tag": p.Device!.AssetTag = (string?)value; break;
                        case "user_id": p.Device!.UserId = (int?)value; break;
                    }
                }
            }
        }

        var result = Result(plan, columns, blank, sheet, applied: true);
        AuditTrail.Record(db, actor.Id, "DEVICES_IMPORTED", "device", null, after: new Dictionary<string, object?>
        {
            ["filename"] = PyText.Slice(filename, 255), ["rows"] = result.Rows, ["created"] = result.Created, ["updated"] = result.Updated,
            ["unchanged"] = result.Unchanged, ["errors"] = result.Errors,
        }, ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return result;
    }
}
