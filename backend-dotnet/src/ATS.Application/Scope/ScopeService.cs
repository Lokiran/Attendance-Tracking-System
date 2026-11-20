using Ats.Application.Common;
using Ats.Application.Data;
using Ats.Domain;
using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Scope;

/// <summary>
/// Reporting-scope authorisation (<c>app/services/scope.py</c>). A MANAGER sees their whole reporting subtree (direct
/// and indirect reports), never peers or unrelated teams; being a department head grants nothing extra. ADMIN sees
/// everyone, EMPLOYEE only themselves. Every protected query goes through these helpers.
/// </summary>
public sealed class ScopeService(IAtsDb db)
{
    /// <summary>Everyone below <paramref name="rootId"/> (recursive CTE; UNION also stops reporting cycles).</summary>
    public async Task<HashSet<int>> SubtreeIdsAsync(int rootId, CancellationToken ct = default)
    {
        var ids = await db.Database.SqlQuery<int>($"""
            WITH RECURSIVE reporting_tree(id) AS (
                SELECT users.id FROM users WHERE users.manager_id = {rootId}
                UNION
                SELECT child.id FROM users AS child JOIN reporting_tree ON child.manager_id = reporting_tree.id
            )
            SELECT reporting_tree.id AS "Value" FROM reporting_tree
            """).ToListAsync(ct).ConfigureAwait(false);
        return [.. ids];
    }

    /// <summary>The user's manager, their manager, and so on (bounded walk that stops on any cycle).</summary>
    public async Task<List<int>> ManagerChainIdsAsync(int userId, CancellationToken ct = default)
    {
        var chain = new List<int>();
        var seen = new HashSet<int> { userId };
        var current = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.ManagerId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        while (current?.ManagerId is { } managerId && !seen.Contains(managerId))
        {
            chain.Add(managerId);
            seen.Add(managerId);
            current = await db.Users.AsNoTracking().Where(u => u.Id == managerId).Select(u => new { u.ManagerId })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        return chain;
    }

    public async Task<bool> CanViewEmployeeAsync(User viewer, int employeeId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (viewer.Id == employeeId || viewer.Role == Role.Admin)
        {
            return true;
        }

        return viewer.Role == Role.Manager && (await SubtreeIdsAsync(viewer.Id, ct).ConfigureAwait(false)).Contains(employeeId);
    }

    /// <summary>The employee, or 404 "Employee not found" for missing and out-of-scope alike (ids cannot be probed).</summary>
    public async Task<User> GetViewableEmployeeAsync(User viewer, int employeeId, CancellationToken ct = default)
    {
        var employee = await db.Users.Include(u => u.Department).Include(u => u.Manager)
            .FirstOrDefaultAsync(u => u.Id == employeeId, ct).ConfigureAwait(false);
        if (employee is null || !await CanViewEmployeeAsync(viewer, employeeId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException("Employee not found");
        }

        return employee;
    }

    /// <summary>
    /// Employees the viewer may report on, optionally narrowed to a manager's subtree or a department, a name or
    /// employee-ID search and a maximum number of results (search-as-you-type pickers). Ordered by name, then id.
    /// </summary>
    public async Task<List<User>> ResolveScopeAsync(User viewer, int? managerId = null, int? departmentId = null,
                                                    bool includeInactive = false, bool trackedOnly = true,
                                                    string? search = null, int? limit = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (viewer.Role == Role.Employee)
        {
            throw new ForbiddenException("Team data is only available to managers and administrators");
        }

        IQueryable<User> query = db.Users.Include(u => u.Department).Include(u => u.Manager).AsSplitQuery();
        if (viewer.Role == Role.Manager)
        {
            var allowed = await SubtreeIdsAsync(viewer.Id, ct).ConfigureAwait(false);
            if (managerId is { } m && m != viewer.Id)
            {
                if (!allowed.Contains(m))
                {
                    throw new NotFoundException("Manager not found");
                }

                allowed = await SubtreeIdsAsync(m, ct).ConfigureAwait(false);
            }

            query = query.Where(u => allowed.Contains(u.Id));
        }
        else if (managerId is { } m)
        {
            if (!await db.Users.AnyAsync(u => u.Id == m, ct).ConfigureAwait(false))
            {
                throw new NotFoundException("Manager not found");
            }

            var ids = await SubtreeIdsAsync(m, ct).ConfigureAwait(false);
            query = query.Where(u => ids.Contains(u.Id));
        }

        if (departmentId is { } d)
        {
            query = query.Where(u => u.DepartmentId == d);
        }

        if (!includeInactive)
        {
            query = query.Where(u => u.IsActive);
        }

        if (trackedOnly)
        {
            query = query.Where(u => u.AttendanceTracked);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().ToLowerInvariant()}%";
#pragma warning disable CA1304, CA1311 // translated to SQL lower() (Python: func.lower), not run in .NET
            query = query.Where(u => EF.Functions.Like(u.FullName.ToLower(), pattern)
                                     || EF.Functions.Like(u.EmployeeCode.ToLower(), pattern));
#pragma warning restore CA1304, CA1311
        }

        query = query.OrderBy(u => u.FullName).ThenBy(u => u.Id);
        if (limit is { } n)
        {
            query = query.Take(n);
        }

        return await query.ToListAsync(ct).ConfigureAwait(false);
    }
}
