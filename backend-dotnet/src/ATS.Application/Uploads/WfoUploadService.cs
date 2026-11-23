using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ats.Application.Attendance;
using Ats.Application.Audit;
using Ats.Application.Auth;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Ats.Application.Python;
using Ats.Application.Wfo;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Uploads;

/// <summary>
/// Timesheet imports (<c>app/services/wfo_upload.py</c>): parse, preview, add unknown people, commit or discard.
/// <list type="bullet">
/// <item>Files: anything <see cref="TimesheetFiles"/> reads, at most 5 MB and 20,000 rows. Headers and values are matched
/// through the WFO mapping. Required columns: employee (code or email) and date. Without a work-mode or source column the
/// file is a presence list (every listed day is an Office day, or WFH if chosen).</item>
/// <item>Preview stores the parsed rows and what the commit would produce; nothing in attendance changes until commit.</item>
/// <item>Commit upserts <c>attendance_daily</c> per (employee, date). A day with an admin override is skipped unless
/// <c>overwrite_overrides</c> (then the override is cleared and audited).</item>
/// </list>
/// </summary>
public sealed partial class WfoUploadService(IAtsDb db, WfoService wfo, AttendanceData data, WfoMapping mapping, IClock clock,
                                             AtsSettings settings, IPasswordHasher hasher)
{
    public const int MaxRows = 20_000;
    public const string UnknownPrefix = "unknown employee ";

    /// <summary>Placeholder address domain for people added from a file (edit it later).</summary>
    public const string ImportEmailDomain = "imported.example.com";

    private static readonly WorkMode[] NotWorkingModes = [WorkMode.Leave, WorkMode.Holiday, WorkMode.Weekoff];

    /// <summary>How an employee reference is matched: case-insensitive, runs of spaces count as one.</summary>
    public static string RefKey(string? reference) => PyText.SplitJoin(reference ?? "").ToLowerInvariant();

    // ------------------------------------------------------------------ parsing rows

    private sealed record Person(int Id, DateOnly Joined, string FullName, string? CodeLower);

    internal sealed class UnknownEntry(string? reference)
    {
        public string? Ref { get; } = reference;

        public OrderedDictionary<string, int> Names { get; } = new(StringComparer.Ordinal);

        public int Rows { get; set; }

        public DateOnly? First { get; set; }
    }

    internal sealed record ParseResult(List<AttendanceUploadRow> Rows, OrderedDictionary<string, string> Columns, bool PresenceList,
                                       OrderedDictionary<string, UnknownEntry> Unknown, List<JsonObject> NameMismatches, DateReader Dates,
                                       int BlankRows, List<JsonObject> MatchedByName);

    internal async Task<ParseResult> ParseRowsAsync(List<List<object?>> table, DateOnly today, WorkMode presenceMode, CancellationToken ct)
    {
        var found = TimesheetFiles.FindHeader(table, mapping);
        if (found is null)
        {
            var first = table.FirstOrDefault(r => r.Any(c => PyText.CellText(c).Length > 0)) ?? [];
            var seen = string.Join(", ", first.Select(PyText.CellText).Where(s => s.Length > 0));
            throw new ValidationFailedException("Could not find an employee and a date column in the first rows. Found: "
                                                + (seen.Length > 0 ? seen : "nothing"));
        }

        var (headerAt, columns) = found.Value;
        var header = table[headerAt];
        var body = table.Skip(headerAt + 1).ToList();
        if (body.Count > MaxRows)
        {
            throw new ValidationFailedException($"At most {MaxRows} rows per file");
        }

        // Without a work-mode or source column every listed day is presence in the chosen mode.
        var presenceList = !columns.ContainsKey("work_mode") && !columns.ContainsKey("source");
        object? Cell(List<object?> r, string key) => columns.TryGetValue(key, out var i) && i < r.Count ? r[i] : null;

        var people = await db.Users.AsNoTracking().OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.EmployeeCode, u.Email, u.DateJoined, u.FullName }).ToListAsync(ct).ConfigureAwait(false);
        var byRef = new Dictionary<string, Person>(StringComparer.Ordinal);
        var byName = new Dictionary<string, List<Person>>(StringComparer.Ordinal);
        foreach (var u in people)
        {
            // People added from a timesheet have a guessed joining date (the 1st of their first month): an earlier month is
            // accepted and moves it back. A joining date HR entered is a real limit.
            var email = u.Email?.ToLowerInvariant();
            var guessed = (email ?? "").EndsWith("@" + ImportEmailDomain, StringComparison.Ordinal);
            var entry = new Person(u.Id, guessed ? DateOnly.MinValue : u.DateJoined, u.FullName, u.EmployeeCode?.ToLowerInvariant());
            byRef[RefKey(entry.CodeLower)] = entry;
            byRef[RefKey(email)] = entry;
            var nameKey = WfoMapping.Normalize(u.FullName);
            if (!byName.TryGetValue(nameKey, out var list))
            {
                byName[nameKey] = list = [];
            }

            list.Add(entry);
        }

        var matchedByName = new OrderedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var dates = DateReader.ForValues(body.Select(r => Cell(r, "date")));
        var unknown = new OrderedDictionary<string, UnknownEntry>(StringComparer.Ordinal);
        var mismatches = new OrderedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var output = new List<AttendanceUploadRow>();
        var blank = 0;
        var n = headerAt + 1;
        foreach (var r in body)
        {
            n++;
            var refText = PyText.CellText(Cell(r, "employee"));
            var dateValue = Cell(r, "date");
            if (refText.Length == 0 && PyText.CellText(dateValue).Length == 0)
            {
                blank++; // empty row, or only cells outside the columns we read (e.g. a pivot table beside the data)
                continue;
            }

            refText = PyText.SplitJoin(refText);
            var employeeRef = refText.Length > 0 ? PyText.Slice(refText, 255) : null;
            var name = PyText.Slice(PyText.SplitJoin(PyText.CellText(Cell(r, "name"))), 120);
            var employeeName = name.Length > 0 ? name : null;
            var p = new AttendanceUploadRow { RowNumber = n, EmployeeRef = employeeRef };
            try
            {
                p.WorkDate = dates.Read(dateValue);
                var key = RefKey(refText);
                if (key.Length == 0)
                {
                    throw new RowValueException("employee is empty");
                }

                var known = byRef.GetValueOrDefault(key);
                // Without a name column the "ID" may itself be a name: match it against names too rather than add a duplicate.
                var nameHint = employeeName ?? (!columns.ContainsKey("name") ? refText : null);
                if (known is null && nameHint is not null && byName.TryGetValue(WfoMapping.Normalize(nameHint), out var same) && same.Count == 1)
                {
                    // The same person under another ID than last month: link when exactly one person has that name.
                    known = same[0];
                    if (!matchedByName.TryGetValue(key, out var hit))
                    {
                        matchedByName[key] = hit = new JsonObject
                        {
                            ["employee_ref"] = employeeRef, ["name"] = employeeName, ["app_code"] = known.CodeLower?.ToUpperInvariant(),
                            ["app_name"] = known.FullName, ["rows"] = 0,
                        };
                    }

                    hit["rows"] = hit["rows"]!.GetValue<int>() + 1;
                }

                DateOnly joined;
                if (known is null)
                {
                    if (!unknown.TryGetValue(key, out var entry))
                    {
                        unknown[key] = entry = new UnknownEntry(employeeRef);
                    }

                    entry.Rows++;
                    if (employeeName is not null)
                    {
                        entry.Names[employeeName] = entry.Names.GetValueOrDefault(employeeName) + 1;
                    }

                    if (p.WorkDate is { } wd && (entry.First is null || wd < entry.First))
                    {
                        entry.First = wd;
                    }

                    joined = DateOnly.MinValue; // a person added from the file joins before their first date in it
                }
                else
                {
                    p.EmployeeId = known.Id;
                    joined = known.Joined;
                    if (employeeName is not null && WfoMapping.Normalize(employeeName) != WfoMapping.Normalize(known.FullName)
                        && !mismatches.ContainsKey(employeeRef!))
                    {
                        mismatches[employeeRef!] = new JsonObject
                        {
                            ["employee_ref"] = employeeRef, ["file_name"] = employeeName, ["app_name"] = known.FullName,
                        };
                    }
                }

                if (p.WorkDate is not { } workDate)
                {
                    throw new RowValueException($"date {PyFormat.Repr(PyText.CellText(dateValue))} is not a date (use 31/03/2026 or 2026-03-31)");
                }

                var rawMode = PyText.Slice(PyText.CellText(Cell(r, "work_mode")), 64);
                p.RawWorkMode = rawMode.Length > 0 ? rawMode : null;
                if (p.RawWorkMode is not null)
                {
                    p.WorkMode = mapping.WorkMode(p.RawWorkMode)
                                 ?? throw new RowValueException($"unknown work mode {PyFormat.Repr(p.RawWorkMode)} (accepted values are in config/wfo_mapping.json)");
                }
                else if (presenceList)
                {
                    p.WorkMode = presenceMode;
                }

                var rawSource = PyText.Slice(PyText.CellText(Cell(r, "source")), 64);
                p.RawSource = rawSource.Length > 0 ? rawSource : null;
                p.IsBadge = mapping.IsBadge(p.RawSource);
                p.Minutes = UploadValues.ParseHours(Cell(r, "hours"));
                var timeIn = UploadValues.ParseTime(Cell(r, "in_time"));
                var timeOut = UploadValues.ParseTime(Cell(r, "out_time"));
                if (timeIn is { } tIn)
                {
                    p.FirstIn = OfficeTime.ToUtc(workDate, tIn, settings.TimeZone);
                    if (timeOut is { } tOut)
                    {
                        var outDay = tOut <= tIn ? workDate.AddDays(1) : workDate; // overnight
                        p.LastOut = OfficeTime.ToUtc(outDay, tOut, settings.TimeZone);
                        p.Minutes ??= (int)Math.Floor((p.LastOut.Value - p.FirstIn.Value).Ticks / (double)TimeSpan.TicksPerMinute);
                    }
                }

                if (workDate < joined)
                {
                    throw new RowValueException($"date is before {(known is not null ? known.FullName : "the employee")} joined "
                                                + $"({joined.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}): check the employee ID, or "
                                                + "change the joining date on the Employees page");
                }

                if (workDate > today && (p.WorkMode is not { } mode || !NotWorkingModes.Contains(mode)))
                {
                    throw new RowValueException("future dates are only accepted for leave, holiday or weekly off");
                }

                if (p.WorkMode is null && p.Minutes is null && !p.IsBadge)
                {
                    throw new RowValueException("nothing to import on this row (no work mode, hours, times or badge source)");
                }

                if (known is null)
                {
                    // Last check, so the row is otherwise complete and becomes valid once the person is added.
                    throw new RowValueException(UnknownPrefix + $"{PyFormat.Repr(employeeRef!)} (not in the app yet)");
                }
            }
            catch (RowValueException e)
            {
                p.Error = PyText.Slice(e.Message, 255);
            }

            output.Add(p);
        }

        var columnNames = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, i) in columns)
        {
            columnNames[k] = PyText.CellText(header[i]);
        }

        return new ParseResult(output, columnNames, presenceList, unknown, [.. mismatches.Values], dates, blank, [.. matchedByName.Values]);
    }

    // ------------------------------------------------------------------ evidence per employee-day

    /// <summary>What a file says about one employee-day (only the sources present in the file are changed).</summary>
    internal sealed class DayUpdate
    {
        public bool HasUpload { get; set; }

        public WorkMode? UploadMode { get; set; }

        public int? UploadMinutes { get; set; }

        public bool HasBadge { get; set; }

        public int BadgeMinutes { get; set; }

        public DateTimeOffset? FirstIn { get; set; }

        public DateTimeOffset? LastOut { get; set; }

        public List<int> Rows { get; } = [];
    }

    /// <summary>Combine valid rows per (employee, date): the updates in first-seen order, and the number of duplicate rows.</summary>
    internal static (OrderedDictionary<(int, DateOnly), DayUpdate> Updates, int Duplicates) Aggregate(IEnumerable<AttendanceUploadRow> rows)
    {
        var updates = new OrderedDictionary<(int, DateOnly), DayUpdate>();
        var duplicates = 0;
        foreach (var r in rows)
        {
            if (r.Error is not null || r.EmployeeId is not { } eid || r.WorkDate is not { } d)
            {
                continue;
            }

            if (!updates.TryGetValue((eid, d), out var u))
            {
                updates[(eid, d)] = u = new DayUpdate();
            }

            u.Rows.Add(r.RowNumber);
            if (r.IsBadge)
            {
                u.HasBadge = true;
                u.BadgeMinutes = Math.Max(u.BadgeMinutes, r.Minutes ?? 0);
                u.FirstIn = Min(u.FirstIn, r.FirstIn);
                u.LastOut = Max(u.LastOut, r.LastOut);
                if (r.WorkMode is not null)
                {
                    (u.HasUpload, u.UploadMode) = (true, r.WorkMode);
                }
            }
            else
            {
                if (u.HasUpload)
                {
                    duplicates++; // later rows for the same day win
                }

                u.HasUpload = true;
                if (r.WorkMode is not null)
                {
                    u.UploadMode = r.WorkMode;
                }

                if (r.Minutes is not null)
                {
                    u.UploadMinutes = r.Minutes;
                }

                if (r.FirstIn is not null && u.FirstIn is null)
                {
                    (u.FirstIn, u.LastOut) = (r.FirstIn, r.LastOut);
                }
            }
        }

        return (updates, duplicates);
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : (a < b ? a : b);

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : (a > b ? a : b);

    /// <summary>
    /// Hours of the day after this file. A file that only lists the day keeps the hours an earlier file gave for the same work
    /// mode, instead of blanking them.
    /// </summary>
    internal static int? KeptMinutes(DayUpdate u, AttendanceDaily? row) =>
        u.UploadMinutes is null && row?.UploadMinutes is not null && row.UploadWorkMode == u.UploadMode ? row.UploadMinutes : u.UploadMinutes;

    private async Task<Dictionary<(int, DateOnly), AttendanceDaily>> ExistingAsync(IEnumerable<(int, DateOnly)> keys, CancellationToken ct)
    {
        var output = new Dictionary<(int, DateOnly), AttendanceDaily>();
        foreach (var group in keys.GroupBy(k => k.Item1))
        {
            var wanted = group.Select(k => k.Item2).ToHashSet();
            var (from, to) = (wanted.Min(), wanted.Max());
            foreach (var row in await db.AttendanceDaily.Where(a => a.EmployeeId == group.Key && a.WorkDate >= from && a.WorkDate <= to)
                         .ToListAsync(ct).ConfigureAwait(false))
            {
                if (wanted.Contains(row.WorkDate))
                {
                    output[(group.Key, row.WorkDate)] = row;
                }
            }
        }

        return output;
    }

    private static Dictionary<int, List<DateOnly>> DatesByEmployee(IEnumerable<(int, DateOnly)> keys)
    {
        var output = new Dictionary<int, List<DateOnly>>();
        foreach (var (eid, d) in keys)
        {
            if (!output.TryGetValue(eid, out var list))
            {
                output[eid] = list = [];
            }

            list.Add(d);
        }

        return output;
    }

    private async Task<Dictionary<int, User>> UsersAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0 ? [] : await db.Users.Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Added from a timesheet (placeholder email): the joining date was a guess, not HR's.</summary>
    public static bool IsGuessedJoin(User user) =>
        (user ?? throw new ArgumentNullException(nameof(user))).Email.EndsWith("@" + ImportEmailDomain, StringComparison.Ordinal);

    /// <summary>
    /// New joining dates for people added from a timesheet whose days start before their guessed joining date: the 1st of
    /// the month of their earliest day.
    /// </summary>
    internal static Dictionary<int, DateOnly> EarlierJoins(Dictionary<int, User> users, Dictionary<int, List<DateOnly>> datesByEmployee)
    {
        var output = new Dictionary<int, DateOnly>();
        foreach (var (eid, dates) in datesByEmployee)
        {
            if (users.TryGetValue(eid, out var u) && dates.Count > 0 && dates.Min() is var first && first < u.DateJoined && IsGuessedJoin(u))
            {
                output[eid] = new DateOnly(first.Year, first.Month, 1);
            }
        }

        return output;
    }

    /// <summary>Classify every affected day as the commit would (without writing).</summary>
    private async Task<(OrderedDictionary<(int, DateOnly), ClassifiedDay> Results, Dictionary<int, User> Users)> ClassifyAsync(
        OrderedDictionary<(int, DateOnly), DayUpdate> updates, Dictionary<(int, DateOnly), AttendanceDaily> existing, bool overwrite,
        CancellationToken ct)
    {
        var results = new OrderedDictionary<(int, DateOnly), ClassifiedDay>();
        var byEmployee = DatesByEmployee(updates.Keys);
        var users = await UsersAsync(byEmployee.Keys, ct).ConfigureAwait(false);
        var joinedFrom = EarlierJoins(users, byEmployee);
        foreach (var (eid, dates) in byEmployee)
        {
            var overrides = new Dictionary<(int, DateOnly), Func<DayEvidence, DayEvidence>>();
            foreach (var d in dates)
            {
                var row = existing.GetValueOrDefault((eid, d));
                if (row?.OverrideWorkMode is not null && !overwrite)
                {
                    continue; // protected: stays as it is
                }

                var u = updates[(eid, d)];
                var kept = KeptMinutes(u, row);
                overrides[(eid, d)] = e =>
                {
                    if (u.HasUpload)
                    {
                        e = e with { UploadMode = u.UploadMode, UploadMinutes = kept };
                    }

                    if (u.HasBadge)
                    {
                        e = e with { BadgeMinutes = u.BadgeMinutes };
                    }

                    return overwrite ? e with { Override = null } : e;
                };
            }

            var ed = (await wfo.ClassifyRangeAsync([users[eid]], dates.Min(), dates.Max(), overrides, joinedFrom, ct).ConfigureAwait(false))[eid];
            foreach (var c in ed.Days)
            {
                if (updates.ContainsKey((eid, c.Date)))
                {
                    results[(eid, c.Date)] = c;
                }
            }
        }

        return (results, users);
    }

    private async Task<JsonObject> SummaryAsync(List<AttendanceUploadRow> rows, JsonNode columns, bool overwrite, CancellationToken ct)
    {
        var (updates, duplicates) = Aggregate(rows);
        var existing = await ExistingAsync(updates.Keys, ct).ConfigureAwait(false);
        var (results, users) = await ClassifyAsync(updates, existing, overwrite, ct).ConfigureAwait(false);
        var policy = Policies.Wfo(await Policies.GetAsync(db, ct).ConfigureAwait(false));
        var modes = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        var conflicts = new List<(DateOnly Date, string FullName, JsonObject Item)>();
        var officeBelowMinimum = 0;
        foreach (var (key, c) in results)
        {
            var mode = c.Class.WorkMode is { } m ? EnumText.Of(m) : "NONE";
            modes[mode] = modes.GetValueOrDefault(mode) + 1;
            if (c.Class.WorkMode == WorkMode.Office && c.Class.HoursKnown && c.Class.OfficeMinutes < policy.MinOfficeMinutes)
            {
                officeBelowMinimum++;
            }

            if (c.Class.Conflicts.Count > 0)
            {
                var u = users[key.Item1];
                conflicts.Add((key.Item2, u.FullName, new JsonObject
                {
                    ["employee_code"] = u.EmployeeCode, ["full_name"] = u.FullName, ["date"] = PyFormat.IsoDate(key.Item2),
                    ["conflicts"] = new JsonArray([.. c.Class.Conflicts.Select(x => (JsonNode?)x)]),
                    ["rows"] = new JsonArray([.. updates[key].Rows.Select(x => (JsonNode?)x)]),
                }));
            }
        }

        var protectedCount = updates.Keys.Count(k => existing.GetValueOrDefault(k)?.OverrideWorkMode is not null);
        var sortedConflicts = conflicts.OrderBy(x => x.Date).ThenBy(x => x.FullName, StringComparer.Ordinal).Select(x => x.Item).ToList();
        // Unknown-employee rows are listed separately (and can be fixed in one step): other problems come first.
        var errors = rows.Where(r => r.Error is not null)
            .OrderBy(r => r.Error!.StartsWith(UnknownPrefix, StringComparison.Ordinal)).ThenBy(r => r.RowNumber).ToList();
        var byWorkMode = new JsonObject();
        foreach (var (k, v) in modes)
        {
            byWorkMode[k] = v;
        }

        var dates = updates.Keys.Select(k => k.Item2).ToList();
        var moves = EarlierJoins(users, DatesByEmployee(updates.Keys));
        return new JsonObject
        {
            ["columns"] = columns.DeepClone(),
            ["rows"] = rows.Count, ["valid_rows"] = rows.Count - errors.Count, ["error_rows"] = errors.Count,
            ["badge_rows"] = rows.Count(r => r.Error is null && r.IsBadge),
            ["duplicate_rows"] = duplicates,
            ["employee_days"] = updates.Count, ["employees"] = updates.Keys.Select(k => k.Item1).Distinct().Count(),
            ["new_days"] = updates.Keys.Count(k => !existing.ContainsKey(k)),
            ["by_work_mode"] = byWorkMode,
            ["office"] = modes.GetValueOrDefault("OFFICE"), ["wfh"] = modes.GetValueOrDefault("WFH"),
            ["office_below_minimum"] = officeBelowMinimum,
            ["conflict_count"] = sortedConflicts.Count, ["conflicts"] = new JsonArray([.. sortedConflicts.Take(200)]),
            ["protected_overrides"] = protectedCount,
            ["errors"] = new JsonArray([.. errors.Take(200).Select(r => (JsonNode)new JsonObject
            {
                ["row"] = r.RowNumber, ["employee"] = r.EmployeeRef, ["error"] = r.Error,
            })]),
            ["date_from"] = dates.Count > 0 ? PyFormat.IsoDate(dates.Min()) : null,
            ["date_to"] = dates.Count > 0 ? PyFormat.IsoDate(dates.Max()) : null,
            ["joining_dates_moved"] = new JsonArray([.. moves.OrderBy(x => users[x.Key].FullName.ToLowerInvariant(), StringComparer.Ordinal).Take(200)
                .Select(x => (JsonNode)new JsonObject
                {
                    ["employee_code"] = users[x.Key].EmployeeCode, ["full_name"] = users[x.Key].FullName,
                    ["from"] = PyFormat.IsoDate(users[x.Key].DateJoined), ["to"] = PyFormat.IsoDate(x.Value),
                })]),
        };
    }

    // ------------------------------------------------------------------ preview / create employees / commit / discard

    /// <summary>Why an unknown reference cannot be added as a new employee (null if it can).</summary>
    private static string? Addable(string reference)
    {
        if (reference.Contains('@', StringComparison.Ordinal))
        {
            return "this is an email address: add the person on the Employees page with their employee ID";
        }

        return reference.Length is < 2 or > 32 || !CodePattern().IsMatch(reference)
            ? "not usable as an employee ID (2-32 letters, digits, spaces, '.', '-' or '_')"
            : null;
    }

    private static JsonArray UnknownList(ParseResult result)
    {
        var items = new List<(int Rows, string Ref, JsonObject Item)>();
        foreach (var e in result.Unknown.Values)
        {
            var names = e.Names.Select((kv, i) => (kv.Key, kv.Value, i)).OrderByDescending(x => x.Value).ThenBy(x => x.i).Select(x => x.Key).ToList();
            var reason = Addable(e.Ref!);
            items.Add((e.Rows, e.Ref!, new JsonObject
            {
                ["employee_ref"] = e.Ref, ["name"] = names.Count > 0 ? names[0] : null,
                ["other_names"] = new JsonArray([.. names.Skip(1).Take(4).Select(x => (JsonNode?)x)]),
                ["rows"] = e.Rows, ["first_date"] = e.First is { } f ? PyFormat.IsoDate(f) : null,
                ["can_add"] = reason is null, ["reason"] = reason,
            }));
        }

        return new JsonArray([.. items.OrderBy(x => -x.Rows).ThenBy(x => x.Ref.ToLowerInvariant(), StringComparer.Ordinal).Select(x => (JsonNode)x.Item)]);
    }

    private static JsonArray DateNotes(DateReader dates)
    {
        var months = string.Join(", ", dates.Months.OrderBy(m => m.Year).ThenBy(m => m.Month)
            .Select(m => new DateOnly(m.Year, m.Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture)));
        var notes = new JsonArray();
        if (dates.MonthFirst > 0)
        {
            notes.Add($"{dates.MonthFirst} date(s) had day and month swapped (like 09-01-2026, or an Excel date showing 9 January for 1 "
                      + $"September) and were read in {months}, the month(s) the rest of the file is in.");
        }

        if (dates.AmbiguousDayFirst > 0)
        {
            notes.Add($"{dates.AmbiguousDayFirst} date(s) like 05/06/2026 could be read either way and were read day first (5 June).");
        }

        return notes;
    }

    private static JsonObject ToJson(OrderedDictionary<string, string> map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map)
        {
            o[k] = v;
        }

        return o;
    }

    public async Task<UploadOut> PreviewAsync(User admin, string filename, byte[] content, RequestContext ctx, string? sheet = null,
                                              WorkMode presenceMode = WorkMode.Office, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(content);
        if (presenceMode is not (WorkMode.Office or WorkMode.Wfh))
        {
            throw new ValidationFailedException("A presence list can only be read as Office or WFH days");
        }

        var table = TimesheetFiles.ReadWorkbook(filename, content, mapping, sheet);
        var result = await ParseRowsAsync(table.Rows, data.Today, presenceMode, ct).ConfigureAwait(false);
        var rows = result.Rows;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var upload = new AttendanceUpload
        {
            Filename = PyText.Slice(filename, 255), FileSha256 = Convert.ToHexStringLower(SHA256.HashData(content)), FileSize = content.Length,
            Status = UploadStatus.Preview, UploadedById = admin.Id, RowCount = rows.Count, ErrorCount = rows.Count(p => p.Error is not null),
        };
        db.AttendanceUploads.Add(upload);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var r in rows)
        {
            r.UploadId = upload.Id;
        }

        db.AttendanceUploadRows.AddRange(rows);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var summary = new JsonObject
        {
            ["sheet"] = table.Sheet,
            ["sheets"] = new JsonArray([.. table.Sheets.Select(s => (JsonNode)new JsonObject { ["name"] = s.Name, ["usable"] = s.Usable, ["rows"] = s.Rows })]),
            ["presence_list"] = result.PresenceList,
            ["presence_mode"] = result.PresenceList ? EnumText.Of(presenceMode) : null,
            ["unknown_employees"] = UnknownList(result),
            ["name_mismatches"] = new JsonArray([.. result.NameMismatches.Take(200)]),
            ["matched_by_name"] = new JsonArray([.. result.MatchedByName.Take(200)]),
            ["hours_from"] = table.HoursFrom is { } h
                ? new JsonObject { ["sheets"] = new JsonArray([.. h.Sheets.Select(x => (JsonNode?)x)]), ["days"] = h.Days }
                : null,
            ["date_notes"] = DateNotes(result.Dates),
            ["blank_rows"] = result.BlankRows,
        };
        foreach (var (k, v) in await SummaryAsync(rows, ToJson(result.Columns), overwrite: false, ct).ConfigureAwait(false))
        {
            summary[k] = v?.DeepClone();
        }

        upload.Summary = summary;
        AuditTrail.Record(db, admin.Id, "ATTENDANCE_UPLOAD_PREVIEWED", "attendance_upload", upload.Id,
                          after: new Dictionary<string, object?>
                          {
                              ["filename"] = upload.Filename, ["sheet"] = table.Sheet, ["rows"] = upload.RowCount, ["errors"] = upload.ErrorCount,
                          }, ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return await OutAsync(upload, ct).ConfigureAwait(false);
    }

    private async Task<string> UniqueEmailAsync(string reference, HashSet<string> taken, CancellationToken ct)
    {
        var stem = NotEmailSafe().Replace(reference.ToLowerInvariant(), ".").Trim('.');
        stem = stem.Length > 0 ? stem : "employee";
        for (var i = 0; i < 1000; i++)
        {
            var email = $"{stem}{(i == 0 ? "" : $".{i + 1}")}@{ImportEmailDomain}";
            if (!taken.Contains(email) && !await db.Users.AnyAsync(u => u.Email == email, ct).ConfigureAwait(false))
            {
                taken.Add(email);
                return email;
            }
        }

        throw new ConflictException($"Could not find a free placeholder email for {PyFormat.Repr(reference)}");
    }

    /// <summary>The upload, locked for the rest of the transaction (PostgreSQL <c>FOR UPDATE</c>), or 404.</summary>
    private async Task<AttendanceUpload> LockAsync(int uploadId, CancellationToken ct)
    {
        var upload = db.IsSqlite
            ? await db.AttendanceUploads.FirstOrDefaultAsync(u => u.Id == uploadId, ct).ConfigureAwait(false)
            : await db.AttendanceUploads.FromSqlInterpolated($"SELECT * FROM attendance_uploads WHERE id = {uploadId} FOR UPDATE")
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return upload ?? throw new NotFoundException("Upload not found");
    }

    private static void EnsurePreview(AttendanceUpload upload)
    {
        if (upload.Status != UploadStatus.Preview)
        {
            throw new ConflictException($"This upload is already {EnumText.Of(upload.Status).ToLowerInvariant()}");
        }
    }

    private Task<List<AttendanceUploadRow>> RowsOfAsync(int uploadId, CancellationToken ct) =>
        db.AttendanceUploadRows.Where(r => r.UploadId == uploadId).OrderBy(r => r.Id).ToListAsync(ct);

    private static Dictionary<string, object?> UserSnapshot(User u) => new()
    {
        ["employee_code"] = u.EmployeeCode, ["full_name"] = u.FullName, ["email"] = u.Email, ["role"] = u.Role, ["job_title"] = u.JobTitle,
        ["department_id"] = u.DepartmentId, ["manager_id"] = u.ManagerId, ["is_active"] = u.IsActive,
        ["attendance_tracked"] = u.AttendanceTracked, ["date_joined"] = u.DateJoined,
    };

    /// <summary>
    /// Add the unknown people of a previewed upload as employees (ID and name from the file, a placeholder email, no usable
    /// password, joined on the 1st of the month of their first date in the file), then re-check the upload's rows.
    /// </summary>
    public async Task<UploadOut> CreateEmployeesAsync(User admin, int uploadId, IReadOnlyList<string>? refs, RequestContext ctx,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var upload = await LockAsync(uploadId, ct).ConfigureAwait(false);
        EnsurePreview(upload);
        var summary = (JsonObject?)upload.Summary?.DeepClone() ?? [];
        var wanted = refs?.Select(RefKey).ToHashSet(StringComparer.Ordinal);
        var unknownList = summary["unknown_employees"] as JsonArray ?? [];
        var todo = unknownList.OfType<JsonObject>()
            .Where(u => u["can_add"]?.GetValue<bool>() == true && (wanted is null || wanted.Contains(RefKey(u["employee_ref"]?.GetValue<string>()))))
            .ToList();
        if (todo.Count == 0)
        {
            throw new ValidationFailedException("There is nobody to add from this upload");
        }

        var today = data.Today;
        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in todo)
        {
            var code = u["employee_ref"]!.GetValue<string>(); // kept as written in the timesheet; matching ignores case
            var upper = code.ToUpperInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL upper(), as Python's func.upper
            var existing = await db.Users.FirstOrDefaultAsync(x => x.EmployeeCode.ToUpper() == upper, ct).ConfigureAwait(false);
#pragma warning restore CA1304, CA1311, CA1862
            if (existing is not null)
            {
                created[RefKey(code)] = existing.Id; // added since the preview: just link the rows
                continue;
            }

            var first = u["first_date"]?.GetValue<string>() is { } fd ? DateOnly.ParseExact(fd, "yyyy-MM-dd", CultureInfo.InvariantCulture) : today;
            var name = PyText.Strip(u["name"]?.GetValue<string>() is { Length: > 0 } n ? n : code);
            var monthStart = new DateOnly(first.Year, first.Month, 1);
            var user = new User
            {
                EmployeeCode = code, FullName = PyText.Slice(name.Length >= 2 ? name : code, 120),
                Email = await UniqueEmailAsync(code, taken, ct).ConfigureAwait(false),
                PasswordHash = hasher.Hash(Tokens.Generate()), // nobody knows it: sign-in needs a reset or SSO
                Role = Role.Employee, IsActive = true, AttendanceTracked = true,
                DateJoined = monthStart < today ? monthStart : today, // employed for the whole month of the file
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var after = UserSnapshot(user);
            after["via_upload"] = upload.Id;
            AuditTrail.Record(db, admin.Id, "USER_CREATED", "user", user.Id, after: after, ctx: ctx);
            created[RefKey(code)] = user.Id;
        }

        var rows = await RowsOfAsync(upload.Id, ct).ConfigureAwait(false);
        foreach (var r in rows)
        {
            if (r.Error is not null && r.Error.StartsWith(UnknownPrefix, StringComparison.Ordinal) && created.TryGetValue(RefKey(r.EmployeeRef), out var id))
            {
                (r.EmployeeId, r.Error) = (id, null);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        upload.ErrorCount = rows.Count(r => r.Error is not null);
        summary["unknown_employees"] = new JsonArray([.. unknownList.OfType<JsonObject>()
            .Where(u => !created.ContainsKey(RefKey(u["employee_ref"]?.GetValue<string>()))).Select(u => (JsonNode)u.DeepClone())]);
        foreach (var (k, v) in await SummaryAsync(rows, summary["columns"]?.DeepClone() ?? new JsonObject(), overwrite: false, ct).ConfigureAwait(false))
        {
            summary[k] = v?.DeepClone();
        }

        summary["employees_added"] = (summary["employees_added"]?.GetValue<int>() ?? 0) + created.Count;
        upload.Summary = summary;
        AuditTrail.Record(db, admin.Id, "ATTENDANCE_UPLOAD_EMPLOYEES_ADDED", "attendance_upload", upload.Id,
                          after: new Dictionary<string, object?> { ["employees"] = created.Count, ["errors"] = upload.ErrorCount }, ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return await OutAsync(upload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dates a committed timesheet covers: from the 1st of the month of its first date to the end of the month of its last
    /// date, but not beyond the day before it was committed (a November sheet imported on the 10th covers 1-9 November).
    /// </summary>
    public static (DateOnly Start, DateOnly End) TimesheetPeriod(DateOnly dateFrom, DateOnly dateTo, DateOnly committed)
    {
        var start = new DateOnly(dateFrom.Year, dateFrom.Month, 1);
        var monthEnd = OfficeTime.MonthEnd(dateTo);
        var capped = monthEnd < committed.AddDays(-1) ? monthEnd : committed.AddDays(-1);
        var end = capped > dateTo ? capped : dateTo;
        return (start, end > start ? end : start);
    }

    public async Task<UploadOut> CommitAsync(User admin, int uploadId, bool overwriteOverrides, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var upload = await LockAsync(uploadId, ct).ConfigureAwait(false);
        EnsurePreview(upload);
        var rows = await RowsOfAsync(upload.Id, ct).ConfigureAwait(false);
        var (updates, _) = Aggregate(rows);
        var existing = await ExistingAsync(updates.Keys, ct).ConfigureAwait(false);
        var people = await UsersAsync(updates.Keys.Select(k => k.Item1), ct).ConfigureAwait(false);
        var moved = EarlierJoins(people, DatesByEmployee(updates.Keys));
        foreach (var (eid, newJoin) in moved)
        {
            var u = people[eid];
            AuditTrail.Record(db, admin.Id, "USER_UPDATED", "user", u.Id, new Dictionary<string, object?> { ["date_joined"] = u.DateJoined },
                              new Dictionary<string, object?> { ["date_joined"] = newJoin, ["via_upload"] = upload.Id }, ctx);
            u.DateJoined = newJoin;
        }

        int applied = 0, createdDays = 0, skipped = 0, overwritten = 0;
        var touched = new OrderedDictionary<int, HashSet<DateOnly>>();
        var now = clock.UtcNow;
        foreach (var ((eid, d), u) in updates)
        {
            var row = existing.GetValueOrDefault((eid, d));
            if (row?.OverrideWorkMode is not null)
            {
                if (!overwriteOverrides)
                {
                    skipped++;
                    continue;
                }

                AuditTrail.Record(db, admin.Id, "WORK_MODE_OVERRIDE_CLEARED", "attendance_daily", $"{eid}:{d:yyyy-MM-dd}",
                                  new Dictionary<string, object?> { ["override_work_mode"] = row.OverrideWorkMode, ["override_note"] = row.OverrideNote },
                                  new Dictionary<string, object?> { ["override_work_mode"] = null, ["via_upload"] = upload.Id }, ctx);
                (row.OverrideWorkMode, row.OverrideNote, row.OverriddenById, row.OverriddenAt) = (null, null, null, null);
                overwritten++;
            }

            if (row is null)
            {
                row = new AttendanceDaily
                {
                    EmployeeId = eid, WorkDate = d, OfficeMinutes = 0, WfhMinutes = 0, Corrected = false, Conflicts = [], Status = DailyStatus.Absent,
                };
                db.AttendanceDaily.Add(row);
                createdDays++;
            }

            if (u.HasUpload)
            {
                var kept = KeptMinutes(u, row);
                (row.UploadWorkMode, row.UploadMinutes) = (u.UploadMode, kept);
            }

            if (u.HasBadge)
            {
                row.BadgeMinutes = u.BadgeMinutes;
            }

            if (u.FirstIn is { } firstIn)
            {
                (row.FirstIn, row.LastOut) = (firstIn, u.LastOut is { } lastOut && lastOut >= firstIn ? lastOut : null);
            }

            row.UploadId = upload.Id;
            row.ComputedAt = now;
            if (!touched.TryGetValue(eid, out var set))
            {
                touched[eid] = set = [];
            }

            set.Add(d);
            applied++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var policyRow = await Policies.GetAsync(db, ct).ConfigureAwait(false);
        var users = await UsersAsync(touched.Keys, ct).ConfigureAwait(false);
        foreach (var (eid, dates) in touched)
        {
            var ed = (await wfo.ClassifyRangeAsync([users[eid]], dates.Min(), dates.Max(), ct: ct).ConfigureAwait(false))[eid];
            wfo.Store(ed, policyRow, dates);
        }

        upload.Status = UploadStatus.Committed;
        upload.CommittedById = admin.Id;
        upload.CommittedAt = now;
        upload.OverwriteOverrides = overwriteOverrides;
        if (updates.Count > 0)
        {
            var dates = updates.Keys.Select(k => k.Item2).ToList();
            (upload.PeriodStart, upload.PeriodEnd) = TimesheetPeriod(dates.Min(), dates.Max(), OfficeTime.LocalDate(now, settings.TimeZone));
        }

        var commitSummary = new JsonObject
        {
            ["applied_days"] = applied, ["created_days"] = createdDays, ["skipped_overrides"] = skipped, ["overrides_overwritten"] = overwritten,
            ["error_rows"] = upload.ErrorCount, ["joining_dates_moved"] = moved.Count,
            ["period_start"] = upload.PeriodStart is { } ps ? PyFormat.IsoDate(ps) : null,
            ["period_end"] = upload.PeriodEnd is { } pe ? PyFormat.IsoDate(pe) : null,
        };
        upload.CommitSummary = commitSummary;
        var after = new Dictionary<string, object?> { ["filename"] = upload.Filename };
        foreach (var (k, v) in commitSummary)
        {
            after[k] = v?.DeepClone();
        }

        AuditTrail.Record(db, admin.Id, "ATTENDANCE_UPLOAD_COMMITTED", "attendance_upload", upload.Id, after: after, ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return await OutAsync(upload, ct).ConfigureAwait(false);
    }

    public async Task<UploadOut> DiscardAsync(User admin, int uploadId, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var upload = await db.AttendanceUploads.FindAsync([uploadId], ct).ConfigureAwait(false) ?? throw new NotFoundException("Upload not found");
        EnsurePreview(upload);
        upload.Status = UploadStatus.Discarded;
        AuditTrail.Record(db, admin.Id, "ATTENDANCE_UPLOAD_DISCARDED", "attendance_upload", upload.Id, ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await OutAsync(upload, ct).ConfigureAwait(false);
    }

    public async Task<List<UploadOut>> RecentAsync(int limit = 50, CancellationToken ct = default)
    {
        var uploads = await db.AttendanceUploads.AsNoTracking().OrderByDescending(u => u.Id).Take(limit).ToListAsync(ct).ConfigureAwait(false);
        var ids = uploads.Where(u => u.UploadedById is not null).Select(u => u.UploadedById!.Value).Distinct().ToList();
        var names = await db.Users.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct).ConfigureAwait(false);
        return [.. uploads.Select(u => UploadOut.Of(u, u.UploadedById is { } id ? names.GetValueOrDefault(id) : null))];
    }

    public async Task<UploadOut> GetAsync(int uploadId, CancellationToken ct = default) =>
        await OutAsync(await db.AttendanceUploads.FindAsync([uploadId], ct).ConfigureAwait(false) ?? throw new NotFoundException("Upload not found"), ct)
            .ConfigureAwait(false);

    private async Task<UploadOut> OutAsync(AttendanceUpload upload, CancellationToken ct)
    {
        string? name = null;
        if (upload.UploadedById is { } id)
        {
            name = (await db.Users.FindAsync([id], ct).ConfigureAwait(false))?.FullName;
        }

        return UploadOut.Of(upload, name);
    }

    /// <summary>Same rule as the Employees page.</summary>
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9 ._-]*[A-Za-z0-9])?$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotEmailSafe();
}
