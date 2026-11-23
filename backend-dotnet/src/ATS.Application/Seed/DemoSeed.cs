using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ats.Application.Attendance;
using Ats.Application.Auth;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Ats.Application.Uploads;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Seed;

public sealed class SeedException(string message) : Exception(message)
{
    public SeedException()
        : this("Seed failed")
    {
    }

    public SeedException(string message, Exception inner)
        : this(message) => _ = inner;
}

/// <summary>
/// Repeatable demo data for local development, a fictional company "Nexa Technologies" (<c>app/seed.py</c>). Users are
/// upserted by email, so re-running never duplicates people. Attendance comes from monthly demo timesheets imported
/// through the real upload pipeline (preview + commit); a month already imported is skipped unless <c>resetDemo</c>.
/// Passwords come from SEED_ADMIN_PASSWORD / SEED_DEMO_PASSWORD; nothing is hard-coded. The random demo days use the
/// same generator and seeds as Python (<see cref="PyRandom"/>), so both backends load the same data.
/// </summary>
public sealed class DemoSeed(IAtsDb db, AtsSettings settings, IClock clock, IPasswordHasher hasher, WfoUploadService uploads)
{
    public const int HistoryDays = 75;
    public const string Domain = "nexa.example";
    public const string DemoPrefix = "demo_timesheet_";
    private static readonly RequestContext SeedContext = new("seed", null);

    private static readonly (string Name, string Description)[] Departments =
    [
        ("AI Engineering", "Machine learning platforms and applied AI products."),
        ("Software Engineering", "Core product engineering and platform services."),
        ("Human Resources", "People operations, hiring and employee experience."),
        ("Operations", "Facilities, IT operations and business operations."),
    ];

    private sealed record Person(string Code, string Name, Role Role, string Title, string? Department, string? Manager, int JoinedDaysAgo = HistoryDays,
                                 bool Tracked = true);

    private static readonly Person[] People =
    [
        new("ADM001", "System Administrator", Role.Admin, "System Administrator", null, null, 900, Tracked: false),
        new("MGR004", "Vikram Desai", Role.Manager, "Chief Operating Officer", "Operations", null),
        new("MGR001", "Priya Sharma", Role.Manager, "AI Engineering Manager", "AI Engineering", "MGR004"),
        new("MGR002", "Anil Kumar", Role.Manager, "Software Engineering Manager", "Software Engineering", "MGR004"),
        new("MGR003", "Meera Nair", Role.Manager, "HR Manager", "Human Resources", null),
        new("EMP001", "Rahul Reddy", Role.Employee, "ML Engineer", "AI Engineering", "MGR001"),
        new("EMP002", "Sneha Rao", Role.Employee, "Data Scientist", "AI Engineering", "MGR001"),
        new("EMP008", "Farah Khan", Role.Employee, "Junior ML Engineer", "AI Engineering", "MGR001", 20),
        new("EMP003", "Arjun Kumar", Role.Employee, "Backend Engineer", "Software Engineering", "MGR002"),
        new("EMP004", "Ananya Sharma", Role.Employee, "Frontend Engineer", "Software Engineering", "MGR002"),
        new("EMP007", "Rohan Gupta", Role.Employee, "Site Reliability Engineer (night shift)", "Software Engineering", "MGR002"),
        new("EMP006", "Divya Menon", Role.Employee, "HR Generalist", "Human Resources", "MGR003"),
        new("EMP005", "Kiran Patel", Role.Employee, "Operations Analyst", "Operations", "MGR004"),
        new("EMP009", "Neha Joshi", Role.Employee, "Data Engineer", "AI Engineering", "MGR001"),
        new("EMP010", "Vivek Singh", Role.Employee, "QA Engineer", "Software Engineering", "MGR002"),
    ];

    private static readonly (string Department, string Head)[] Heads =
        [("AI Engineering", "MGR001"), ("Software Engineering", "MGR002"), ("Human Resources", "MGR003"), ("Operations", "MGR004")];

    /// <summary>
    /// Deterministic demo identities for automatic attendance: a device MAC in the locally administered range (02:..., never
    /// a real vendor address) and an Entra object id (UUID v5).
    /// </summary>
    public static (string Mac, string ObjectId) DemoIdentities(string code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        var mac = $"02:a7:5e:{hash[0]:x2}:{hash[1]:x2}:{hash[2]:x2}";
        return (mac, Uuid5(new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8"), $"https://nexa.example/ats-demo/{code}"));
    }

    /// <summary>RFC 4122 name-based UUID (SHA-1), as Python's <c>uuid.uuid5</c>.</summary>
    public static string Uuid5(Guid ns, string name)
    {
        var nsBytes = ns.ToByteArray(bigEndian: true);
#pragma warning disable CA5350 // SHA-1 is what UUID v5 is defined with; not used for security
        var hash = SHA1.HashData([.. nsBytes, .. Encoding.UTF8.GetBytes(name)]);
#pragma warning restore CA5350
        var b = hash[..16];
        b[6] = (byte)((b[6] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b, bigEndian: true).ToString();
    }

    private static string EmailOf(Person p, string adminEmail)
    {
        if (p.Role == Role.Admin)
        {
            return adminEmail;
        }

        var parts = p.Name.ToLowerInvariant().Split(' ', 2);
        return $"{parts[0]}.{parts[1].Replace(" ", "", StringComparison.Ordinal)}@{Domain}";
    }

    private static DateOnly WeekdayOnOrBefore(DateOnly d)
    {
        while (OfficeTime.IsoWeekday(d) > 5)
        {
            d = d.AddDays(-1);
        }

        return d;
    }

    /// <summary>The seed's result, printed as Python prints its dict.</summary>
    public sealed record Result(int Users, int TimesheetsImported, DateOnly Today)
    {
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{{'users': {Users}, 'timesheets_imported': {TimesheetsImported}, 'today': '{Today:yyyy-MM-dd}'}}");
    }

    public async Task<Result> RunAsync(Func<string, string?> env, bool resetDemo = false, bool resetPasswords = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (settings.IsProduction)
        {
            throw new SeedException("Refusing to load demo data when APP_ENV=production. Use the `create-admin` command.");
        }

        var adminEmail = (env("SEED_ADMIN_EMAIL") ?? "").Trim().ToLowerInvariant();
        var adminPassword = env("SEED_ADMIN_PASSWORD") ?? "";
        var demoPassword = env("SEED_DEMO_PASSWORD") ?? "";
        if (adminEmail.Length == 0 || adminPassword.Length == 0 || demoPassword.Length == 0)
        {
            throw new SeedException("Set SEED_ADMIN_EMAIL, SEED_ADMIN_PASSWORD and SEED_DEMO_PASSWORD (see .env.example)");
        }

        PasswordPolicy.Validate(adminPassword);
        PasswordPolicy.Validate(demoPassword);
        var today = OfficeTime.LocalDate(clock.UtcNow, settings.TimeZone);
        Dictionary<string, User> users;
        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            await Policies.GetAsync(db, ct).ConfigureAwait(false);
            users = await UpsertPeopleAsync(adminEmail, adminPassword, demoPassword, resetPasswords, today, ct).ConfigureAwait(false);
            if (resetDemo)
            {
                var ids = users.Values.Select(u => u.Id).ToList();
                await db.AttendanceDaily.Where(a => ids.Contains(a.EmployeeId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.AttendanceUploads.Where(u => u.Filename.StartsWith(DemoPrefix)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            await EnsureSchedulesAsync(users, ct).ConfigureAwait(false);
            await EnsureHolidaysAsync(today, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        var admin = await db.Users.FirstAsync(u => u.Email == adminEmail, ct).ConfigureAwait(false);
        var imported = await ImportTimesheetsAsync(users, admin, today, ct).ConfigureAwait(false);
        return new Result(users.Count, imported, today);
    }

    private async Task<Dictionary<string, User>> UpsertPeopleAsync(string adminEmail, string adminPassword, string demoPassword, bool resetPasswords,
                                                                  DateOnly today, CancellationToken ct)
    {
        var depts = new Dictionary<string, Department>(StringComparer.Ordinal);
        foreach (var (name, description) in Departments)
        {
            var d = await db.Departments.FirstOrDefaultAsync(x => x.Name == name, ct).ConfigureAwait(false);
            if (d is null)
            {
                d = new Department { Name = name, Description = description };
                db.Departments.Add(d);
            }

            depts[name] = d;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var users = new Dictionary<string, User>(StringComparer.Ordinal);
        foreach (var p in People)
        {
            var email = EmailOf(p, adminEmail);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct).ConfigureAwait(false)
                    ?? await db.Users.FirstOrDefaultAsync(x => x.EmployeeCode == p.Code, ct).ConfigureAwait(false);
            var password = p.Role == Role.Admin ? adminPassword : demoPassword;
            if (u is null)
            {
                u = new User
                {
                    EmployeeCode = p.Code, Email = email, PasswordHash = hasher.Hash(password), IsActive = true, FullName = p.Name, Role = p.Role,
                    DateJoined = today.AddDays(-p.JoinedDaysAgo),
                };
                db.Users.Add(u);
            }
            else if (resetPasswords)
            {
                u.PasswordHash = hasher.Hash(password);
            }

            (u.FullName, u.Role, u.JobTitle, u.AttendanceTracked) = (p.Name, p.Role, p.Title, p.Tracked);
            u.DateJoined = today.AddDays(-p.JoinedDaysAgo);
            u.DepartmentId = p.Department is { } dn ? depts[dn].Id : null;
            users[p.Code] = u;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var p in People)
        {
            var u = users[p.Code];
            u.ManagerId = p.Manager is { } m ? users[m].Id : null;
            if (p.Tracked)
            {
                // Fill identities only where empty: never overwrite a real MAC or Entra id set by HR.
                var (mac, oid) = DemoIdentities(p.Code);
                if (u.WifiIdentity is null && !await db.Users.AnyAsync(x => x.WifiIdentity == mac, ct).ConfigureAwait(false))
                {
                    u.WifiIdentity = mac;
                }

                if (u.GraphUserId is null && !await db.Users.AnyAsync(x => x.GraphUserId == oid, ct).ConfigureAwait(false))
                {
                    u.GraphUserId = oid;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        foreach (var (dept, code) in Heads)
        {
            depts[dept].DepartmentHeadId = users[code].Id;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return users;
    }

    private async Task EnsureSchedulesAsync(Dictionary<string, User> users, CancellationToken ct)
    {
        if (await db.WorkSchedules.AnyAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var ops = await db.Departments.FirstAsync(d => d.Name == "Operations", ct).ConfigureAwait(false);
        db.WorkSchedules.AddRange(
            new WorkSchedule
            {
                Name = "General shift", StartTime = new TimeOnly(9, 30), EndTime = new TimeOnly(18, 30), RequiredMinutes = 480, WorkingDays = [1, 2, 3, 4, 5],
                EffectiveFrom = new DateOnly(2024, 1, 1),
            },
            new WorkSchedule
            {
                Name = "Operations shift (Mon-Sat)", DepartmentId = ops.Id, StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(16, 30),
                RequiredMinutes = 450, WorkingDays = [1, 2, 3, 4, 5, 6], EffectiveFrom = new DateOnly(2024, 1, 1),
            },
            new WorkSchedule
            {
                Name = "Night shift", EmployeeId = users["EMP007"].Id, StartTime = new TimeOnly(21, 0), EndTime = new TimeOnly(6, 0), RequiredMinutes = 480,
                WorkingDays = [1, 2, 3, 4, 5], EffectiveFrom = new DateOnly(2024, 1, 1),
            });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureHolidaysAsync(DateOnly today, CancellationToken ct)
    {
        var hr = await db.Departments.FirstAsync(d => d.Name == "Human Resources", ct).ConfigureAwait(false);
        var wanted = new (string Name, DateOnly Date, int? Dept)[]
        {
            ("Founders' Day", WeekdayOnOrBefore(today.AddDays(-17)), null),
            ("Regional Festival", WeekdayOnOrBefore(today.AddDays(-46)), null),
            ("HR Annual Offsite", WeekdayOnOrBefore(today.AddDays(-31)), hr.Id),
            ("Company Foundation Day", today.AddDays(12), null),
        };
        foreach (var (name, date, dept) in wanted)
        {
            if (!await db.Holidays.AnyAsync(h => h.HolidayDate == date && h.DepartmentId == dept, ct).ConfigureAwait(false))
            {
                db.Holidays.Add(new Holiday { Name = name, HolidayDate = date, DepartmentId = dept });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>(first, last) day of each month to import: from <see cref="HistoryDays"/> ago up to yesterday.</summary>
    public static List<(DateOnly First, DateOnly Last)> Months(DateOnly today)
    {
        var earliest = today.AddDays(-HistoryDays);
        var m = new DateOnly(earliest.Year, earliest.Month, 1);
        var yesterday = today.AddDays(-1);
        var output = new List<(DateOnly, DateOnly)>();
        while (m <= yesterday)
        {
            var end = OfficeTime.MonthEnd(m);
            output.Add((m, end < yesterday ? end : yesterday));
            m = m.AddMonths(1);
        }

        return output;
    }

    private static long FloorDiv(long a, long b) => (long)Math.Floor(a / (double)b);

    private static long Mod(long a, long b) => ((a % b) + b) % b;

    /// <summary>
    /// One month's demo timesheet: Employee ID, Name, Date, Work Mode, In, Out. Most people are in the office Monday to
    /// Wednesday and work from home Thursday and Friday (EMP005 only on Tuesdays); about 4% of working days are missing
    /// (absent), 2% are leave, and a few Saturdays are worked.
    /// </summary>
    public async Task<byte[]> TimesheetCsvAsync(Dictionary<string, User> users, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(users);
        var schedules = (await db.WorkSchedules.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).Select(AttendanceData.ScheduleDataOf).ToList();
        var holidays = (await db.Holidays.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).Select(h => (h.HolidayDate, h.DepartmentId)).ToHashSet();
        var sb = new StringBuilder("Employee ID,Employee,Date,Work Mode,In,Out\n");
        foreach (var (code, u) in users)
        {
            if (!u.AttendanceTracked)
            {
                continue;
            }

            var rng = new PyRandom($"nexa-{code}-{start:yyyy-MM}");
            var emp = AttendanceData.EmployeeDataOf(u);
            int[] officeDays = code == "EMP005" ? [1] : [0, 1, 2];
            for (var day = start > u.DateJoined ? start : u.DateJoined; day <= end; day = day.AddDays(1))
            {
                var sched = AttendanceRules.ResolveSchedule(schedules, emp, day);
                if (sched is null)
                {
                    continue;
                }

                var iso = OfficeTime.IsoWeekday(day);
                var holiday = holidays.Contains((day, null)) || holidays.Contains((day, u.DepartmentId));
                var workday = sched.WorkingDays.Contains(iso) && !holiday;
                if (!workday && !(iso == 6 && !holiday && rng.Random() < 0.06))
                {
                    continue;
                }

                var roll = rng.Random();
                var date = day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                if (workday && roll < 0.04)
                {
                    continue; // absent: not in the timesheet
                }

                if (workday && roll < 0.06)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{Csv(u.EmployeeCode)},{Csv(u.FullName)},{date},Leave,,\n");
                    continue;
                }

                var mode = officeDays.Contains(OfficeTime.Weekday(day)) || !workday ? "WFO" : "WFH";
                long startMin = sched.StartTime.Hour * 60 + sched.StartTime.Minute + (long)rng.Gauss(4, 14);
                var endMin = startMin + sched.RequiredMinutes + (long)rng.Gauss(20, 25) + rng.Choice([30, 45, 60]);
                var tIn = string.Create(CultureInfo.InvariantCulture, $"{Mod(FloorDiv(startMin, 60), 24):00}:{Mod(startMin, 60):00}");
                var tOut = string.Create(CultureInfo.InvariantCulture, $"{Mod(FloorDiv(endMin, 60), 24):00}:{Mod(endMin, 60):00}");
                sb.Append(CultureInfo.InvariantCulture, $"{Csv(u.EmployeeCode)},{Csv(u.FullName)},{date},{mode},{tIn},{tOut}\n");
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>A CSV field as Python's csv.writer (minimal quoting) writes it.</summary>
    private static string Csv(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;

    private async Task<int> ImportTimesheetsAsync(Dictionary<string, User> users, User admin, DateOnly today, CancellationToken ct)
    {
        var imported = 0;
        foreach (var (start, end) in Months(today))
        {
            var name = $"{DemoPrefix}{start:yyyy-MM}.csv";
            if (await db.AttendanceUploads.AnyAsync(u => u.Filename == name && u.Status == UploadStatus.Committed, ct).ConfigureAwait(false))
            {
                continue;
            }

            var preview = await uploads.PreviewAsync(admin, name, await TimesheetCsvAsync(users, start, end, ct).ConfigureAwait(false), SeedContext, ct: ct)
                .ConfigureAwait(false);
            await uploads.CommitAsync(admin, preview.Id, overwriteOverrides: false, SeedContext, ct).ConfigureAwait(false);
            imported++;
        }

        return imported;
    }
}
