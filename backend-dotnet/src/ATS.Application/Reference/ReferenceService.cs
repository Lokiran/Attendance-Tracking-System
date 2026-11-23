using Ats.Application.Attendance;
using Ats.Application.Auth;
using Ats.Application.Data;
using Ats.Domain;
using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Reference;

// Shapes of backend/app/schemas/admin.py used by the read endpoints of this phase.

public sealed record HolidayOut(int Id, string Name, DateOnly HolidayDate, int? DepartmentId);

public sealed record DepartmentOut(int Id, string Name, string? Description, int? DepartmentHeadId, ManagerBrief? Head, int MemberCount,
                                   DateTimeOffset CreatedAt);

public sealed record PolicyOut(int MinPresentMinutes, bool CountIncompleteAsPresent, int MaxSessionMinutes, int LateGraceMinutes,
                               int CorrectionWindowDays, bool ManagerCanApprove, string? Notes, DateTimeOffset UpdatedAt)
{
    public static PolicyOut Of(AttendancePolicy p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.MinPresentMinutes, p.CountIncompleteAsPresent, p.MaxSessionMinutes, p.LateGraceMinutes, p.CorrectionWindowDays,
                   p.ManagerCanApprove, p.Notes, p.UpdatedAt);
    }
}

/// <summary>Holidays, departments and the policy as the read endpoints return them (<c>routes/admin.py</c>).</summary>
public sealed class ReferenceService(IAtsDb db)
{
    /// <summary>Holidays in the range; non-admins see organisation-wide ones and their own department's.</summary>
    public async Task<List<HolidayOut>> HolidaysAsync(User viewer, DateOnly? start, DateOnly? end, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var query = db.Holidays.AsNoTracking().AsQueryable();
        if (start is { } s)
        {
            query = query.Where(h => h.HolidayDate >= s);
        }

        if (end is { } e)
        {
            query = query.Where(h => h.HolidayDate <= e);
        }

        if (viewer.Role != Role.Admin)
        {
            var dept = viewer.DepartmentId;
            query = query.Where(h => h.DepartmentId == null || h.DepartmentId == dept);
        }

        return await query.OrderBy(h => h.HolidayDate).Select(h => new HolidayOut(h.Id, h.Name, h.HolidayDate, h.DepartmentId))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Departments by name, with their active members counted (one grouped query; Python counts per department, P7).</summary>
    public async Task<List<DepartmentOut>> DepartmentsAsync(CancellationToken ct = default)
    {
        var departments = await db.Departments.AsNoTracking().Include(d => d.Head).OrderBy(d => d.Name).ToListAsync(ct).ConfigureAwait(false);
        var counts = await db.Users.Where(u => u.IsActive && u.DepartmentId != null).GroupBy(u => u.DepartmentId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        return [.. departments.Select(d => Department(d, counts.GetValueOrDefault(d.Id)))];
    }

    public static DepartmentOut Department(Department d, int memberCount)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new DepartmentOut(d.Id, d.Name, d.Description, d.DepartmentHeadId,
                                 d.Head is { } h ? new ManagerBrief(h.Id, h.FullName, h.EmployeeCode) : null, memberCount, d.CreatedAt);
    }

    /// <summary>GET /policy. TODO(parity) P3: a read that saves: it creates the policy row when missing, then commits.</summary>
    public async Task<PolicyOut> PolicyAsync(CancellationToken ct = default)
    {
        var policy = await Policies.GetAsync(db, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return PolicyOut.Of(policy);
    }
}
