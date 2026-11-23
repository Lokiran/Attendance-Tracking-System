using Ats.Application.Audit;
using Ats.Application.Auth;
using Ats.Application.Common;
using Ats.Application.Data;
using Ats.Application.Reference;
using Ats.Application.Scope;
using Ats.Domain;
using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Ats.Application.Admin;

/// <summary>
/// Employee and department administration with hierarchy and privilege safeguards (<c>app/services/admin_users.py</c>).
/// The <c>changes</c> dictionaries hold only the fields the client sent (Pydantic's <c>exclude_unset</c>), keyed by
/// their JSON names; a null value means "sent as null".
/// </summary>
public sealed class AdminUserService(IAtsDb db, ScopeService scope, AuthService auth, IPasswordHasher hasher, IClock clock,
                                     Configuration.AtsSettings settings)
{
    public static Dictionary<string, object?> UserSnapshot(User u)
    {
        ArgumentNullException.ThrowIfNull(u);
        return new()
        {
            ["employee_code"] = u.EmployeeCode, ["full_name"] = u.FullName, ["email"] = u.Email, ["role"] = u.Role,
            ["job_title"] = u.JobTitle, ["department_id"] = u.DepartmentId, ["manager_id"] = u.ManagerId, ["is_active"] = u.IsActive,
            ["attendance_tracked"] = u.AttendanceTracked, ["date_joined"] = u.DateJoined,
        };
    }

    private DateOnly Today => Domain.Rules.OfficeTime.LocalDate(clock.UtcNow, settings.TimeZone);

    private Task<int> ActiveAdminCountAsync(CancellationToken ct) => db.Users.CountAsync(u => u.Role == Role.Admin && u.IsActive, ct);

    private Task<int> DirectReportCountAsync(int userId, CancellationToken ct) => db.Users.CountAsync(u => u.ManagerId == userId && u.IsActive, ct);

    private async Task ValidateManagerAsync(int? userId, int? managerId, CancellationToken ct)
    {
        if (managerId is not { } mid)
        {
            return;
        }

        if (userId is not null && mid == userId)
        {
            throw new ValidationFailedException("An employee cannot be their own manager");
        }

        var manager = await db.Users.FindAsync([mid], ct).ConfigureAwait(false) ?? throw new ValidationFailedException("Selected manager does not exist");
        if (!manager.IsActive)
        {
            throw new ValidationFailedException("Selected manager is inactive");
        }

        if (manager.Role is not (Role.Manager or Role.Admin))
        {
            throw new ValidationFailedException("Selected manager must have the MANAGER or ADMIN role");
        }

        if (userId is { } uid && (await scope.ManagerChainIdsAsync(mid, ct).ConfigureAwait(false)).Contains(uid))
        {
            throw new ValidationFailedException("This assignment would create a circular reporting relationship");
        }
    }

    private async Task ValidateDepartmentAsync(int? departmentId, CancellationToken ct)
    {
        if (departmentId is { } id && await db.Departments.FindAsync([id], ct).ConfigureAwait(false) is null)
        {
            throw new ValidationFailedException("Selected department does not exist");
        }
    }

    /// <summary>Save; a unique-key race becomes 409 with the email / employee ID wording (<c>_commit_unique</c>).</summary>
    private async Task SaveUniqueAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e)
        {
            var message = (e.InnerException?.Message ?? e.Message).ToLowerInvariant();
            db.ChangeTracker.Clear();
            if (message.Contains("email", StringComparison.Ordinal))
            {
                throw new ConflictException("A user with this email already exists");
            }

            if (message.Contains("employee_code", StringComparison.Ordinal))
            {
                throw new ConflictException("A user with this employee ID already exists");
            }

            throw new ConflictException("The change conflicts with existing data");
        }
    }

    /// <summary>The user with the department and manager loaded (for <see cref="UserAdminOut"/>).</summary>
    public async Task<User> LoadAsync(int userId, CancellationToken ct = default) =>
        await db.Users.Include(u => u.Department).Include(u => u.Manager).FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false)
        ?? throw new NotFoundException("User not found");

#pragma warning disable CA1304, CA1311, CA1862 // ToUpper / ToLower are translated to SQL upper() / lower(), as in Python
    private Task<bool> CodeTakenAsync(string upperCode, int? exceptId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.EmployeeCode.ToUpper() == upperCode && (exceptId == null || u.Id != exceptId), ct);
#pragma warning restore CA1304, CA1311, CA1862

    public async Task<UserAdminOut> CreateUserAsync(User actor, IReadOnlyDictionary<string, object?> data, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(data);
        var email = ((string)data["email"]!).Trim().ToLowerInvariant();
        var code = ((string)data["employee_code"]!).Trim().ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct).ConfigureAwait(false))
        {
            throw new ConflictException("A user with this email already exists");
        }

        if (await CodeTakenAsync(code, null, ct).ConfigureAwait(false))
        {
            throw new ConflictException("A user with this employee ID already exists");
        }

        var password = (string)data["password"]!;
        PasswordPolicy.Validate(password);
        var departmentId = (int?)data.GetValueOrDefault("department_id");
        var managerId = (int?)data.GetValueOrDefault("manager_id");
        await ValidateDepartmentAsync(departmentId, ct).ConfigureAwait(false);
        await ValidateManagerAsync(null, managerId, ct).ConfigureAwait(false);
        var user = new User
        {
            EmployeeCode = code, FullName = ((string)data["full_name"]!).Trim(), Email = email, PasswordHash = hasher.Hash(password),
            Role = (Role?)data.GetValueOrDefault("role") ?? Role.Employee, JobTitle = (string?)data.GetValueOrDefault("job_title"),
            DepartmentId = departmentId, ManagerId = managerId, IsActive = true,
            AttendanceTracked = (bool?)data.GetValueOrDefault("attendance_tracked") ?? true,
            DateJoined = (DateOnly?)data.GetValueOrDefault("date_joined") ?? Today,
        };
        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            db.Users.Add(user);
            await SaveUniqueAsync(ct).ConfigureAwait(false);
            AuditTrail.Record(db, actor.Id, "USER_CREATED", "user", user.Id, after: UserSnapshot(user), ctx: ctx);
            await SaveUniqueAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return UserAdminOut.Of(await LoadAsync(user.Id, ct).ConfigureAwait(false));
    }

    public async Task<UserAdminOut> UpdateUserAsync(User actor, int userId, IReadOnlyDictionary<string, object?> changes, RequestContext ctx,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(changes);
        var user = await db.Users.FindAsync([userId], ct).ConfigureAwait(false) ?? throw new NotFoundException("User not found");
        var before = UserSnapshot(user);
        if (changes.GetValueOrDefault("email") is string rawEmail)
        {
            var email = rawEmail.Trim().ToLowerInvariant();
            if (email != user.Email && await db.Users.AnyAsync(u => u.Email == email, ct).ConfigureAwait(false))
            {
                throw new ConflictException("A user with this email already exists");
            }

            user.Email = email;
        }

        if (changes.GetValueOrDefault("employee_code") is string rawCode)
        {
            var code = rawCode.Trim().ToUpperInvariant();
            if (code != user.EmployeeCode && await CodeTakenAsync(code, user.Id, ct).ConfigureAwait(false))
            {
                throw new ConflictException("A user with this employee ID already exists");
            }

            user.EmployeeCode = code;
        }

        if (changes.GetValueOrDefault("full_name") is string fullName)
        {
            user.FullName = fullName.Trim();
        }

        if (changes.TryGetValue("job_title", out var jobTitle))
        {
            user.JobTitle = (jobTitle as string)?.Trim();
        }

        if (changes.GetValueOrDefault("date_joined") is DateOnly joined)
        {
            user.DateJoined = joined;
        }

        if (changes.GetValueOrDefault("attendance_tracked") is bool tracked)
        {
            user.AttendanceTracked = tracked;
        }

        if (changes.TryGetValue("department_id", out var dept))
        {
            await ValidateDepartmentAsync((int?)dept, ct).ConfigureAwait(false);
            user.DepartmentId = (int?)dept;
        }

        if (changes.TryGetValue("manager_id", out var manager))
        {
            await ValidateManagerAsync(user.Id, (int?)manager, ct).ConfigureAwait(false);
            user.ManagerId = (int?)manager;
        }

        if (changes.GetValueOrDefault("role") is Role newRole && newRole != user.Role)
        {
            if (user.Id == actor.Id)
            {
                throw new ValidationFailedException("You cannot change your own role");
            }

            if (user.Role == Role.Admin && user.IsActive && await ActiveAdminCountAsync(ct).ConfigureAwait(false) <= 1)
            {
                throw new ValidationFailedException("Cannot demote the last active administrator");
            }

            if (newRole == Role.Employee && await DirectReportCountAsync(user.Id, ct).ConfigureAwait(false) > 0)
            {
                throw new ValidationFailedException("Reassign this person's direct reports before changing their role to EMPLOYEE");
            }

            user.Role = newRole;
        }

        if (changes.GetValueOrDefault("is_active") is bool active && active != user.IsActive)
        {
            if (!active)
            {
                if (user.Id == actor.Id)
                {
                    throw new ValidationFailedException("You cannot deactivate your own account");
                }

                if (user.Role == Role.Admin && await ActiveAdminCountAsync(ct).ConfigureAwait(false) <= 1)
                {
                    throw new ValidationFailedException("Cannot deactivate the last active administrator");
                }

                if (await DirectReportCountAsync(user.Id, ct).ConfigureAwait(false) > 0)
                {
                    throw new ValidationFailedException("Reassign this person's direct reports before deactivating them");
                }

                await auth.RevokeAllSessionsAsync(user.Id, ct: ct).ConfigureAwait(false);
            }

            user.IsActive = active;
        }

        var (b, a) = AuditTrail.Changes(before, UserSnapshot(user));
        if (a.Count > 0)
        {
            var action = "USER_UPDATED";
            if (a.ContainsKey("role"))
            {
                action = "USER_ROLE_CHANGED";
            }
            else if (a.ContainsKey("manager_id") && a.Count == 1)
            {
                action = "USER_MANAGER_ASSIGNED";
            }
            else if (a.ContainsKey("is_active") && a.Count == 1)
            {
                action = a["is_active"] is true ? "USER_ACTIVATED" : "USER_DEACTIVATED";
            }

            AuditTrail.Record(db, actor.Id, action, "user", user.Id, b, a, ctx);
        }

        await SaveUniqueAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear(); // reload the manager / department after foreign-key changes (Python: db.refresh)
        return UserAdminOut.Of(await LoadAsync(user.Id, ct).ConfigureAwait(false));
    }

    public async Task ResetPasswordAsync(User actor, int userId, string password, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var user = await db.Users.FindAsync([userId], ct).ConfigureAwait(false) ?? throw new NotFoundException("User not found");
        PasswordPolicy.Validate(password);
        user.PasswordHash = hasher.Hash(password);
        await auth.RevokeAllSessionsAsync(user.Id, ct: ct).ConfigureAwait(false);
        AuditTrail.Record(db, actor.Id, "USER_PASSWORD_RESET", "user", user.Id, ctx: ctx);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PagedResult<UserAdminOut>> ListUsersAsync(string? search, Role? role, int? departmentId, int? managerId, bool? isActive,
                                                         int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking().Include(u => u.Department).Include(u => u.Manager).AsQueryable();
        if (!string.IsNullOrEmpty(search))
        {
            var like = $"%{search.Trim().ToLowerInvariant()}%";
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower(), as in Python
            query = query.Where(u => EF.Functions.Like(u.FullName.ToLower(), like) || EF.Functions.Like(u.Email.ToLower(), like)
                                     || EF.Functions.Like(u.EmployeeCode.ToLower(), like));
#pragma warning restore CA1304, CA1311, CA1862
        }

        if (role is { } r)
        {
            query = query.Where(u => u.Role == r);
        }

        if (departmentId is { } d)
        {
            query = query.Where(u => u.DepartmentId == d);
        }

        if (managerId is { } m)
        {
            query = query.Where(u => u.ManagerId == m);
        }

        if (isActive is { } active)
        {
            query = query.Where(u => u.IsActive == active);
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderBy(u => u.FullName).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<UserAdminOut>([.. rows.Select(UserAdminOut.Of)], total, page, pageSize);
    }

    public async Task<List<UserAdminOut>> ManagersAsync(CancellationToken ct = default) =>
        [.. (await db.Users.AsNoTracking().Include(u => u.Department).Include(u => u.Manager)
                .Where(u => (u.Role == Role.Manager || u.Role == Role.Admin) && u.IsActive).OrderBy(u => u.FullName)
                .ToListAsync(ct).ConfigureAwait(false)).Select(UserAdminOut.Of)];

    // ---------------------------------------------------------------- departments

    private static Dictionary<string, object?> DeptSnapshot(Department d) => new()
    {
        ["name"] = d.Name, ["description"] = d.Description, ["department_head_id"] = d.DepartmentHeadId,
    };

    private async Task<string> ValidateDeptAsync(string name, int? headId, int? deptId, CancellationToken ct)
    {
        name = Strip(name);
        if (name.Length == 0)
        {
            throw new ValidationFailedException("Department name is required");
        }

        var lower = name.ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL lower(), as in Python
        var clash = await db.Departments.Where(x => x.Name.ToLower() == lower).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
#pragma warning restore CA1304, CA1311, CA1862
        if (clash is not null && clash != deptId)
        {
            throw new ConflictException("A department with this name already exists");
        }

        if (headId is { } h && (await db.Users.FindAsync([h], ct).ConfigureAwait(false)) is not { IsActive: true })
        {
            throw new ValidationFailedException("Department head must be an active user");
        }

        return name;
    }

    private static string Strip(string s) => Uploads.PyText.Strip(s);

    /// <summary>The department as the API returns it (head loaded, active members counted).</summary>
    public async Task<DepartmentOut> DepartmentOutAsync(int deptId, CancellationToken ct = default)
    {
        var d = await db.Departments.AsNoTracking().Include(x => x.Head).FirstAsync(x => x.Id == deptId, ct).ConfigureAwait(false);
        var members = await db.Users.CountAsync(u => u.DepartmentId == deptId && u.IsActive, ct).ConfigureAwait(false);
        return ReferenceService.Department(d, members);
    }

    public async Task<DepartmentOut> CreateDepartmentAsync(User actor, string name, string? description, int? headId, RequestContext ctx,
                                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        name = await ValidateDeptAsync(name, headId, null, ct).ConfigureAwait(false);
        var dept = new Department { Name = name, Description = description, DepartmentHeadId = headId };
        await using (var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            db.Departments.Add(dept);
            await SaveUniqueAsync(ct).ConfigureAwait(false);
            AuditTrail.Record(db, actor.Id, "DEPARTMENT_CREATED", "department", dept.Id, after: DeptSnapshot(dept), ctx: ctx);
            await SaveUniqueAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        return await DepartmentOutAsync(dept.Id, ct).ConfigureAwait(false);
    }

    public async Task<DepartmentOut> UpdateDepartmentAsync(User actor, int deptId, IReadOnlyDictionary<string, object?> changes, RequestContext ctx,
                                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(changes);
        var dept = await db.Departments.FindAsync([deptId], ct).ConfigureAwait(false) ?? throw new NotFoundException("Department not found");
        var before = DeptSnapshot(dept);
        var newName = changes.GetValueOrDefault("name") as string;
        var head = changes.TryGetValue("department_head_id", out var h) ? (int?)h : dept.DepartmentHeadId;
        dept.Name = await ValidateDeptAsync(string.IsNullOrEmpty(newName) ? dept.Name : newName, head, dept.Id, ct).ConfigureAwait(false);
        if (changes.TryGetValue("description", out var description))
        {
            dept.Description = (string?)description;
        }

        if (changes.ContainsKey("department_head_id"))
        {
            dept.DepartmentHeadId = head;
        }

        var (b, a) = AuditTrail.Changes(before, DeptSnapshot(dept));
        if (a.Count > 0)
        {
            AuditTrail.Record(db, actor.Id, "DEPARTMENT_UPDATED", "department", dept.Id, b, a, ctx);
        }

        await SaveUniqueAsync(ct).ConfigureAwait(false);
        return await DepartmentOutAsync(dept.Id, ct).ConfigureAwait(false);
    }

    public async Task DeleteDepartmentAsync(User actor, int deptId, RequestContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var dept = await db.Departments.FindAsync([deptId], ct).ConfigureAwait(false) ?? throw new NotFoundException("Department not found");
        if (await db.Users.AnyAsync(u => u.DepartmentId == deptId, ct).ConfigureAwait(false))
        {
            throw new ConflictException("Move all employees out of this department before deleting it");
        }

        AuditTrail.Record(db, actor.Id, "DEPARTMENT_DELETED", "department", dept.Id, before: DeptSnapshot(dept), ctx: ctx);
        db.Departments.Remove(dept);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
