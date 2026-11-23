using Ats.Application.Data;
using Ats.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Attendance;

/// <summary>Facts about the committed timesheets (<c>attendance_data.latest_import_through</c>).</summary>
public static class Timesheets
{
    /// <summary>The last date any committed timesheet covers.</summary>
    public static async Task<DateOnly?> LatestImportThroughAsync(IAtsDb db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.AttendanceUploads
            .Where(u => u.Status == UploadStatus.Committed && u.PeriodStart != null && u.PeriodEnd != null)
            .MaxAsync(u => u.PeriodEnd, ct).ConfigureAwait(false);
    }
}
