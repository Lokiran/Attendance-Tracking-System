using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Ats.Application.Data;

/// <summary>The application database as services see it (implemented by the EF Core context in Infrastructure).</summary>
public interface IAtsDb
{
    DbSet<Department> Departments { get; }

    DbSet<User> Users { get; }

    DbSet<WorkSchedule> WorkSchedules { get; }

    DbSet<Holiday> Holidays { get; }

    DbSet<AttendancePolicy> AttendancePolicies { get; }

    DbSet<AttendanceDaily> AttendanceDaily { get; }

    DbSet<AttendanceUpload> AttendanceUploads { get; }

    DbSet<AttendanceUploadRow> AttendanceUploadRows { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<UserSession> UserSessions { get; }

    DbSet<LoginAttempt> LoginAttempts { get; }

    DbSet<OfficeSite> OfficeSites { get; }

    DbSet<Device> Devices { get; }

    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    bool IsSqlite { get; }

    EntityEntry Entry(object entity);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
