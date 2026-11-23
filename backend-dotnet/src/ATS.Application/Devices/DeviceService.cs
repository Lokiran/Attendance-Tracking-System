using System.Globalization;
using System.Text.RegularExpressions;
using Ats.Application.Admin;
using Ats.Application.Audit;
using Ats.Application.Common;
using Ats.Application.Data;
using Ats.Application.Python;
using Ats.Application.Uploads;
using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Devices;

public sealed record OfficeSiteOut(int Id, string Name, string Code, string? NetworkName, string? Address, bool IsActive)
{
    public static OfficeSiteOut Of(OfficeSite s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Id, s.Name, s.Code, s.NetworkName, s.Address, s.IsActive);
    }
}

public sealed record DeviceOwner(int Id, string EmployeeCode, string FullName);

public sealed record DeviceOut(int Id, string? SerialNumber, string? MacAddress, bool RandomMac, string? AssetTag, string? Hostname, string? Model,
                               DeviceOwner? User, bool IsActive, string Source, string? Notes, DateTimeOffset UpdatedAt)
{
    /// <summary>The device with <see cref="Device.User"/> loaded.</summary>
    public static DeviceOut Of(Device d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new(d.Id, d.SerialNumber, d.MacAddress, DeviceService.IsRandomMac(d.MacAddress), d.AssetTag, d.Hostname, d.Model,
                   d.User is { } u ? new DeviceOwner(u.Id, u.EmployeeCode, u.FullName) : null, d.IsActive, d.Source, d.Notes, d.UpdatedAt);
    }
}

public sealed record RegisterSummary(int Devices, int WithMac, int WithoutMac, int Unassigned, int RandomMac, int PeopleTracked,
                                     int PeopleWithDevice, int Sites);

public sealed record DeviceImportItem(int Row, string Action, string? SerialNumber, string? MacAddress, string? Hostname, string? Owner,
                                      string? OwnerCode, string? OwnerWritten, List<string> Changes, List<string> Problems);

public sealed record UnknownOwner(string Written, int Rows);

public sealed record DeviceImportResult(bool Applied, string? Sheet, Dictionary<string, string> Columns, int Rows, int BlankRows, int Created,
                                        int Updated, int Unchanged, int Errors, int WithMac, int RandomMac, List<UnknownOwner> UnknownOwners,
                                        List<DeviceImportItem> Items);

/// <summary>
/// Office sites and the device register (<c>app/services/devices.py</c>). The office Wi-Fi logs show devices, never people;
/// this register says whose device each one is, keyed by serial number. An asset list fills serial → employee; an Intune
/// export fills serial → MAC address. Imports match rows by serial number, then MAC address; a blank cell never clears a
/// value; an import is checked first and only written when applied.
/// </summary>
public sealed partial class DeviceService(IAtsDb db)
{
    public const int HeaderSearchRows = 20;
    public const int MaxRows = 5000;

    private static readonly HashSet<string> JunkSerials =
        ["", "NA", "N/A", "NONE", "NULL", "-", "0", "TO BE FILLED BY O.E.M.", "DEFAULT STRING", "SYSTEM SERIAL NUMBER", "INVALID"];

    // ------------------------------------------------------------------ identifiers

    /// <summary>"AA-BB-CC-DD-EE-FF", "aabb.ccdd.eeff", "AABBCCDDEEFF" → "aa:bb:cc:dd:ee:ff"; blank → null; else a row problem.</summary>
    public static string? NormalizeMac(object? raw)
    {
        var text = PyText.CellText(raw);
        if (text.Length == 0)
        {
            return null;
        }

        var digits = MacSeparators().Replace(text, "").ToLowerInvariant();
        if (!TwelveHex().IsMatch(digits) || digits is "000000000000" or "ffffffffffff")
        {
            throw new RowValueException($"{PyFormat.Repr(text)} is not a MAC address (12 hex digits, like AA:BB:CC:DD:EE:FF)");
        }

        return string.Join(':', Enumerable.Range(0, 6).Select(i => digits.Substring(i * 2, 2)));
    }

    /// <summary>A private / random Wi-Fi address (locally administered bit set): it does not identify a laptop.</summary>
    public static bool IsRandomMac(string? mac) =>
        !string.IsNullOrEmpty(mac) && (int.Parse(mac[1].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 2) != 0;

    public static string? NormalizeSerial(object? raw)
    {
        var text = PyText.SplitJoin(PyText.CellText(raw)).ToUpperInvariant();
        return JunkSerials.Contains(text) ? null : PyText.Slice(text, 64);
    }

    private static string? Clean(object? raw, int limit)
    {
        var text = PyText.Slice(PyText.SplitJoin(PyText.CellText(raw)), limit);
        return text.Length > 0 ? text : null;
    }

    private static string? Stripped(string? text) => PyText.Strip(text ?? "") is { Length: > 0 } s ? s : null;

    // ------------------------------------------------------------------ office sites

    private static Dictionary<string, object?> SiteSnapshot(OfficeSite s) => new()
    {
        ["name"] = s.Name, ["code"] = s.Code, ["network_name"] = s.NetworkName, ["address"] = s.Address, ["is_active"] = s.IsActive,
    };

    public async Task<List<OfficeSiteOut>> SitesAsync(CancellationToken ct = default) =>
        [.. (await db.OfficeSites.AsNoTracking().OrderByDescending(s => s.IsActive).ThenBy(s => s.Name).ToListAsync(ct).ConfigureAwait(false))
            .Select(OfficeSiteOut.Of)];

    private async Task CheckSiteUniqueAsync(string? name, string? code, int? exclude, CancellationToken ct)
    {
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower(), as in Python
        if (name is not null)
        {
            var lower = name.ToLowerInvariant();
            if (await db.OfficeSites.AnyAsync(s => s.Name.ToLower() == lower && (exclude == null || s.Id != exclude), ct).ConfigureAwait(false))
            {
                throw new ConflictException($"Another office already uses the name {PyFormat.Repr(name)}");
            }
        }

        if (code is not null)
        {
            var lower = code.ToLowerInvariant();
            if (await db.OfficeSites.AnyAsync(s => s.Code.ToLower() == lower && (exclude == null || s.Id != exclude), ct).ConfigureAwait(false))
            {
                throw new ConflictException($"Another office already uses the code {PyFormat.Repr(code)}");
            }
        }
#pragma warning restore CA1304, CA1311, CA1862
    }

    public async Task<OfficeSiteOut> CreateSiteAsync(User actor, string name, string code, string? networkName, string? address, RequestContext ctx,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(code);
        (name, code) = (PyText.Strip(name), PyText.Strip(code).ToUpperInvariant());
        await CheckSiteUniqueAsync(name, code, null, ct).ConfigureAwait(false);
        var site = new OfficeSite { Name = name, Code = code, NetworkName = Clean(networkName, 64), Address = Stripped(address), IsActive = true };
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.OfficeSites.Add(site);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        AuditTrail.Record(db, actor.Id, "OFFICE_SITE_CREATED", "office_site", site.Id, after: SiteSnapshot(site), ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return OfficeSiteOut.Of(site);
    }

    public async Task<OfficeSiteOut> UpdateSiteAsync(User actor, int siteId, IReadOnlyDictionary<string, object?> data, RequestContext ctx,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(data);
        var site = await db.OfficeSites.FindAsync([siteId], ct).ConfigureAwait(false) ?? throw new NotFoundException("Office not found");
        var before = SiteSnapshot(site);
        var name = data.GetValueOrDefault("name") is string n ? PyText.Strip(n) : null;
        var code = data.GetValueOrDefault("code") is string c ? PyText.Strip(c).ToUpperInvariant() : null;
        await CheckSiteUniqueAsync(name, code, site.Id, ct).ConfigureAwait(false);
        if (name is not null)
        {
            site.Name = name; // required fields are never cleared
        }

        if (code is not null)
        {
            site.Code = code;
        }

        if (data.TryGetValue("network_name", out var network))
        {
            site.NetworkName = Clean(network, 64);
        }

        if (data.TryGetValue("address", out var address))
        {
            site.Address = Stripped(address as string);
        }

        if (data.GetValueOrDefault("is_active") is bool active)
        {
            site.IsActive = active;
        }

        var (b, a) = AuditTrail.Changes(before, SiteSnapshot(site));
        if (a.Count > 0)
        {
            AuditTrail.Record(db, actor.Id, "OFFICE_SITE_UPDATED", "office_site", site.Id, b, a, ctx);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return OfficeSiteOut.Of(site);
    }

    // ------------------------------------------------------------------ devices

    private static Dictionary<string, object?> Snapshot(Device d) => new()
    {
        ["serial_number"] = d.SerialNumber, ["asset_tag"] = d.AssetTag, ["mac_address"] = d.MacAddress, ["hostname"] = d.Hostname,
        ["model"] = d.Model, ["user_id"] = d.UserId, ["is_active"] = d.IsActive, ["notes"] = d.Notes,
    };

    /// <summary>Request fields → stored form (only the fields sent). A bad MAC address is a 422.</summary>
    private static Dictionary<string, object?> Normalized(IReadOnlyDictionary<string, object?> data)
    {
        var output = new Dictionary<string, object?>(data);
        try
        {
            if (output.TryGetValue("mac_address", out var mac))
            {
                output["mac_address"] = NormalizeMac(mac);
            }
        }
        catch (RowValueException e)
        {
            throw new ValidationFailedException(e.Message);
        }

        if (output.TryGetValue("serial_number", out var serial))
        {
            output["serial_number"] = NormalizeSerial(serial);
        }

        foreach (var (f, limit) in new[] { ("asset_tag", 64), ("hostname", 64), ("model", 120) })
        {
            if (output.TryGetValue(f, out var v))
            {
                output[f] = Clean(v, limit);
            }
        }

        if (output.TryGetValue("notes", out var notes))
        {
            output["notes"] = Stripped(notes as string);
        }

        return output;
    }

    private async Task CheckDeviceUniqueAsync(string? serial, string? mac, int? exclude, CancellationToken ct)
    {
        foreach (var (value, label, bySerial) in new[] { (serial, "serial number", true), (mac, "MAC address", false) })
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var other = await db.Devices.Include(d => d.User)
                .FirstOrDefaultAsync(d => (bySerial ? d.SerialNumber == value : d.MacAddress == value) && (exclude == null || d.Id != exclude), ct)
                .ConfigureAwait(false);
            if (other is not null)
            {
                var owner = other.User is { } u ? $" ({u.FullName})" : "";
                throw new ConflictException($"The {label} {value} is already registered to another device{owner}");
            }
        }
    }

    private async Task CheckOwnerAsync(int? userId, CancellationToken ct)
    {
        if (userId is { } id && await db.Users.FindAsync([id], ct).ConfigureAwait(false) is null)
        {
            throw new ValidationFailedException("Employee does not exist");
        }
    }

    private async Task<DeviceOut> DeviceOutAsync(int id, CancellationToken ct) =>
        DeviceOut.Of(await db.Devices.AsNoTracking().Include(d => d.User).FirstAsync(d => d.Id == id, ct).ConfigureAwait(false));

    public async Task<DeviceOut> CreateDeviceAsync(User actor, IReadOnlyDictionary<string, object?> request, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var data = Normalized(request);
        var serial = data.GetValueOrDefault("serial_number") as string;
        var mac = data.GetValueOrDefault("mac_address") as string;
        if (string.IsNullOrEmpty(serial) && string.IsNullOrEmpty(mac))
        {
            throw new ValidationFailedException("Enter the serial number, the MAC address, or both");
        }

        var userId = data.GetValueOrDefault("user_id") as int?;
        await CheckOwnerAsync(userId, ct).ConfigureAwait(false);
        await CheckDeviceUniqueAsync(serial, mac, null, ct).ConfigureAwait(false);
        var device = new Device
        {
            SerialNumber = serial, AssetTag = data.GetValueOrDefault("asset_tag") as string, MacAddress = mac,
            Hostname = data.GetValueOrDefault("hostname") as string, Model = data.GetValueOrDefault("model") as string, UserId = userId,
            Notes = data.GetValueOrDefault("notes") as string, IsActive = true, Source = "MANUAL",
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        AuditTrail.Record(db, actor.Id, "DEVICE_CREATED", "device", device.Id, after: Snapshot(device), ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return await DeviceOutAsync(device.Id, ct).ConfigureAwait(false);
    }

    public async Task<DeviceOut> UpdateDeviceAsync(User actor, int deviceId, IReadOnlyDictionary<string, object?> request, RequestContext ctx,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var device = await db.Devices.Include(d => d.User).FirstOrDefaultAsync(d => d.Id == deviceId, ct).ConfigureAwait(false)
                     ?? throw new NotFoundException("Device not found");
        var ownerBefore = device.User;
        var data = Normalized(request);
        var serial = data.TryGetValue("serial_number", out var s) ? s as string : device.SerialNumber;
        var mac = data.TryGetValue("mac_address", out var m) ? m as string : device.MacAddress;
        if (string.IsNullOrEmpty(serial) && string.IsNullOrEmpty(mac))
        {
            throw new ValidationFailedException("A device needs a serial number or a MAC address");
        }

        if (data.TryGetValue("user_id", out var owner))
        {
            await CheckOwnerAsync(owner as int?, ct).ConfigureAwait(false);
        }

        await CheckDeviceUniqueAsync(data.GetValueOrDefault("serial_number") as string, data.GetValueOrDefault("mac_address") as string, device.Id, ct)
            .ConfigureAwait(false);
        var before = Snapshot(device);
        if (data.ContainsKey("serial_number"))
        {
            device.SerialNumber = serial;
        }

        if (data.ContainsKey("mac_address"))
        {
            device.MacAddress = mac;
        }

        if (data.TryGetValue("asset_tag", out var tag))
        {
            device.AssetTag = tag as string;
        }

        if (data.TryGetValue("hostname", out var host))
        {
            device.Hostname = host as string;
        }

        if (data.TryGetValue("model", out var model))
        {
            device.Model = model as string;
        }

        if (data.ContainsKey("user_id"))
        {
            device.UserId = owner as int?;
        }

        if (data.TryGetValue("notes", out var notes))
        {
            device.Notes = notes as string;
        }

        if (data.GetValueOrDefault("is_active") is bool active)
        {
            device.IsActive = active;
        }

        var (b, a) = AuditTrail.Changes(before, Snapshot(device));
        if (a.Count > 0)
        {
            AuditTrail.Record(db, actor.Id, "DEVICE_UPDATED", "device", device.Id, b, a, ctx);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        // TODO(parity) P14: Python loads the owner with the device (lazy="joined") and does not expire it on commit, so the
        // response shows the owner from before the change; the stored owner (and the next read) is the new one.
        return DeviceOut.Of(device) with
        {
            User = ownerBefore is { } u ? new DeviceOwner(u.Id, u.EmployeeCode, u.FullName) : null,
        };
    }

    public async Task<PagedResult<DeviceOut>> ListDevicesAsync(string? search, bool? assigned, bool? hasMac, bool? active, int page, int pageSize,
                                                               CancellationToken ct = default)
    {
        var query = db.Devices.AsNoTracking().Include(d => d.User).AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            var like = $"%{search.Trim().ToLowerInvariant()}%";
            var macLike = "%" + MacSeparators().Replace(search.Trim().ToLowerInvariant(), "") + "%";
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower() / replace(), as in Python
            query = query.Where(d => EF.Functions.Like(d.SerialNumber!.ToLower(), like) || EF.Functions.Like(d.AssetTag!.ToLower(), like)
                                     || EF.Functions.Like(d.Hostname!.ToLower(), like) || EF.Functions.Like(d.Model!.ToLower(), like)
                                     || EF.Functions.Like(d.MacAddress!.Replace(":", ""), macLike)
                                     || EF.Functions.Like(d.User!.FullName.ToLower(), like) || EF.Functions.Like(d.User!.EmployeeCode.ToLower(), like));
#pragma warning restore CA1304, CA1311, CA1862
        }

        if (assigned is { } isAssigned)
        {
            query = isAssigned ? query.Where(d => d.UserId != null) : query.Where(d => d.UserId == null);
        }

        if (hasMac is { } withMac)
        {
            query = withMac ? query.Where(d => d.MacAddress != null) : query.Where(d => d.MacAddress == null);
        }

        if (active is { } isActive)
        {
            query = query.Where(d => d.IsActive == isActive);
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderBy(d => d.User!.FullName == null).ThenBy(d => d.User!.FullName).ThenBy(d => d.SerialNumber)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<DeviceOut>([.. rows.Select(DeviceOut.Of)], total, page, pageSize);
    }

    /// <summary>Counts for the register's header: how ready it is for Wi-Fi matching.</summary>
    public async Task<RegisterSummary> RegisterSummaryAsync(CancellationToken ct = default)
    {
        var active = db.Devices.Where(d => d.IsActive);
        var total = await active.CountAsync(ct).ConfigureAwait(false);
        var withMac = await active.CountAsync(d => d.MacAddress != null, ct).ConfigureAwait(false);
        var unassigned = await active.CountAsync(d => d.UserId == null, ct).ConfigureAwait(false);
        var macs = await active.Where(d => d.MacAddress != null).Select(d => d.MacAddress).ToListAsync(ct).ConfigureAwait(false);
        var tracked = db.Users.Where(u => u.IsActive && u.AttendanceTracked).Select(u => u.Id);
        var people = await tracked.CountAsync(ct).ConfigureAwait(false);
        var covered = await active.Where(d => d.MacAddress != null && d.UserId != null && tracked.Contains(d.UserId.Value))
            .Select(d => d.UserId).Distinct().CountAsync(ct).ConfigureAwait(false);
        var sites = await db.OfficeSites.CountAsync(s => s.IsActive, ct).ConfigureAwait(false);
        return new RegisterSummary(total, withMac, total - withMac, unassigned, macs.Count(IsRandomMac), people, covered, sites);
    }

    [GeneratedRegex(@"[\s:.\-]")]
    private static partial Regex MacSeparators();

    [GeneratedRegex("^[0-9a-f]{12}$")]
    private static partial Regex TwelveHex();
}
