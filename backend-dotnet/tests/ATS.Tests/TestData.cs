using System.Globalization;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Tests.Auth;

namespace Ats.Tests;

/// <summary>Test data like the Python suite's <c>factory</c> (conftest.py): imported timesheet days and covering uploads.</summary>
internal static class TestData
{
    /// <summary>The Python suite's frozen clock: Tuesday 10 March 2026, 12:00 IST.</summary>
    public static readonly DateTimeOffset Tuesday = new(2026, 3, 10, 6, 30, 0, TimeSpan.Zero);

    /// <summary>12:00 IST on a given day.</summary>
    public static DateTimeOffset NoonIst(DateOnly d) => new(d.Year, d.Month, d.Day, 6, 30, 0, TimeSpan.Zero);

    /// <summary>One imported timesheet day, as a committed upload leaves it (<c>factory.timesheet</c>).</summary>
    public static void Timesheet(AuthHost host, User user, DateOnly d, WorkMode? mode = WorkMode.Office, int? minutes = null,
                                 DateTimeOffset? firstIn = null, DateTimeOffset? lastOut = null)
    {
        using var db = host.Db();
        db.AttendanceDaily.Add(new AttendanceDaily
        {
            EmployeeId = user.Id, WorkDate = d, OfficeMinutes = 0, WfhMinutes = 0, Corrected = false, Conflicts = [], Status = DailyStatus.Absent,
            UploadWorkMode = mode, UploadMinutes = minutes, FirstIn = firstIn, LastOut = lastOut,
        });
        db.SaveChanges();
    }

    /// <summary>A committed timesheet covering start..end for the people (<c>factory.cover</c>): their missing scheduled days are absent.</summary>
    public static void Cover(AuthHost host, DateOnly start, DateOnly end, params User[] people)
    {
        using var db = host.Db();
        var upload = new AttendanceUpload
        {
            Filename = $"timesheet_{start:yyyy-MM-dd}.csv", FileSha256 = new string('0', 64), FileSize = 1, Status = UploadStatus.Committed,
            RowCount = people.Length, CommittedAt = new DateTimeOffset(end.Year, end.Month, end.Day, 23, 0, 0, TimeSpan.Zero), PeriodStart = start, PeriodEnd = end,
        };
        db.AttendanceUploads.Add(upload);
        db.SaveChanges();
        var n = 2;
        foreach (var p in people)
        {
            db.AttendanceUploadRows.Add(new AttendanceUploadRow
            {
                UploadId = upload.Id, RowNumber = n++, EmployeeRef = p.Id.ToString(CultureInfo.InvariantCulture), EmployeeId = p.Id, WorkDate = start,
            });
        }

        db.SaveChanges();
    }

    /// <summary>A timesheet day with hours from 09:30 IST (<c>worked</c> in test_wfo_api.py).</summary>
    public static void Worked(AuthHost host, User user, DateOnly d, double hours = 8.5, WorkMode? location = null)
    {
        var start = new DateTimeOffset(d.Year, d.Month, d.Day, 4, 0, 0, TimeSpan.Zero);
        Timesheet(host, user, d, location, (int)(hours * 60), start, start.AddMinutes((int)(hours * 60)));
    }
}
