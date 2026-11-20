using System.Text;
using System.Text.Json.Nodes;
using Ats.Application.Common;
using Ats.Application.Data;
using Ats.Domain;
using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ats.Infrastructure.Persistence;

/// <summary>
/// The application database. The mapping follows the schema Alembic created (head 0006) exactly: snake_case
/// columns, the SQLAlchemy naming convention for keys and indexes, enums as their stored strings, JSON columns,
/// and on SQLite the same text formats for timestamps. The provider (PostgreSQL or SQLite) comes from the options.
/// </summary>
public class AtsDbContext : DbContext, IAtsDb
{
    private readonly IClock _clock;

    public AtsDbContext(DbContextOptions<AtsDbContext> options, IClock? clock = null)
        : this((DbContextOptions)options, clock)
    {
    }

    protected AtsDbContext(DbContextOptions options, IClock? clock)
        : base(options)
    {
        _clock = clock ?? new SystemClock();
    }

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<User> Users => Set<User>();

    public DbSet<WorkSchedule> WorkSchedules => Set<WorkSchedule>();

    public DbSet<Holiday> Holidays => Set<Holiday>();

    public DbSet<AttendancePolicy> AttendancePolicies => Set<AttendancePolicy>();

    public DbSet<AttendanceDaily> AttendanceDaily => Set<AttendanceDaily>();

    public DbSet<AttendanceUpload> AttendanceUploads => Set<AttendanceUpload>();

    public DbSet<AttendanceUploadRow> AttendanceUploadRows => Set<AttendanceUploadRow>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

    public DbSet<OfficeSite> OfficeSites => Set<OfficeSite>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<WifiEvent> WifiEvents => Set<WifiEvent>();

    public DbSet<PresenceEvent> PresenceEvents => Set<PresenceEvent>();

    public DbSet<AttendanceDailyCorrection> AttendanceDailyCorrections => Set<AttendanceDailyCorrection>();

    public DbSet<AttendanceSession> AttendanceSessions => Set<AttendanceSession>();

    public DbSet<AttendanceCorrection> AttendanceCorrections => Set<AttendanceCorrection>();

    public DbSet<LeaveRecord> LeaveRecords => Set<LeaveRecord>();

    public bool IsSqlite => Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";

    // ------------------------------------------------------------------ timestamps (SQLAlchemy defaults / onupdate)

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimes();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTimes()
    {
        var now = _clock.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity is ICreatedAt created && created.CreatedAt == default)
                {
                    created.CreatedAt = now;
                }

                if (entry.Entity is ITimestamped stamped)
                {
                    if (stamped.CreatedAt == default)
                    {
                        stamped.CreatedAt = now;
                    }

                    if (stamped.UpdatedAt == default)
                    {
                        stamped.UpdatedAt = now;
                    }
                }

                if (entry.Entity is AttendancePolicy { UpdatedAt.Ticks: 0 } policy)
                {
                    policy.UpdatedAt = now;
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                // onupdate: only when the code did not set updated_at itself.
                var updated = entry.Metadata.FindProperty("UpdatedAt");
                if (updated is not null && !entry.Property("UpdatedAt").IsModified)
                {
                    entry.Property("UpdatedAt").CurrentValue = now;
                }
            }
        }
    }

    // ------------------------------------------------------------------ model

    /// <summary>Alembic created indexes only where the models declare them, not on every foreign key.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Conventions.Remove<Microsoft.EntityFrameworkCore.Metadata.Conventions.ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var b = modelBuilder;
        if (!IsSqlite)
        {
            b.UseSerialColumns(); // SQLAlchemy created SERIAL keys (sequences), not identity columns
        }

        b.Entity<Department>(e =>
        {
            Table(e, "departments");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasColumnType(Text);
            e.HasOne(x => x.Head).WithMany().HasForeignKey(x => x.DepartmentHeadId)
                .HasConstraintName("fk_departments_head_users").OnDelete(DeleteBehavior.SetNull);
            // uq_departments_name_lower is an expression index on lower(name): created by the migration SQL.
        });

        b.Entity<User>(e =>
        {
            Table(e, "users");
            e.Property(x => x.EmployeeCode).HasMaxLength(32);
            e.Property(x => x.FullName).HasMaxLength(120);
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.PasswordHash).HasMaxLength(255);
            EnumColumn(e.Property(x => x.Role), 16);
            e.Property(x => x.JobTitle).HasMaxLength(120);
            e.Property(x => x.GraphUserId).HasMaxLength(64);
            e.Property(x => x.WifiIdentity).HasMaxLength(255);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId)
                .HasConstraintName("fk_users_department_id_departments").OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerId)
                .HasConstraintName("fk_users_manager_id_users").OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.EmployeeCode).IsUnique().HasDatabaseName("uq_users_employee_code");
            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("uq_users_email");
            e.HasIndex(x => x.ManagerId).HasDatabaseName("ix_users_manager_id");
            e.HasIndex(x => x.DepartmentId).HasDatabaseName("ix_users_department_id");
            e.HasIndex(x => x.GraphUserId).IsUnique().HasDatabaseName("uq_users_graph_user_id");
            e.HasIndex(x => x.WifiIdentity).IsUnique().HasDatabaseName("uq_users_wifi_identity");
        });

        b.Entity<WorkSchedule>(e =>
        {
            Table(e, "work_schedules");
            e.Property(x => x.Name).HasMaxLength(100);
            JsonColumn(e.Property(x => x.WorkingDays), StoredFormats.IntListConverter, StoredFormats.IntListComparer);
            UserFk(e, x => x.EmployeeId, "work_schedules", "employee_id", DeleteBehavior.Cascade);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId)
                .HasConstraintName("fk_work_schedules_department_id_departments").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.EmployeeId).HasDatabaseName("ix_work_schedules_employee");
            e.HasIndex(x => x.DepartmentId).HasDatabaseName("ix_work_schedules_department");
        });

        b.Entity<Holiday>(e =>
        {
            Table(e, "holidays");
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId)
                .HasConstraintName("fk_holidays_department_id_departments").OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HolidayDate).HasDatabaseName("ix_holidays_holiday_date");
            // uq_holidays_date_department (holiday_date, COALESCE(department_id, 0)): migration SQL.
        });

        b.Entity<AttendancePolicy>(e =>
        {
            Table(e, "attendance_policy");
            e.Property(x => x.Notes).HasColumnType(Text);
            JsonColumn(e.Property(x => x.WfhPresenceStates), StoredFormats.StringListConverter, StoredFormats.StringListComparer);
            UserFk(e, x => x.UpdatedById, "attendance_policy", "updated_by_id", DeleteBehavior.SetNull);
        });

        b.Entity<AttendanceUpload>(e =>
        {
            Table(e, "attendance_uploads");
            e.Property(x => x.Filename).HasMaxLength(255);
            e.Property(x => x.FileSha256).HasMaxLength(64);
            EnumColumn(e.Property(x => x.Status), 16);
            ObjectColumn(e.Property(x => x.Summary));
            ObjectColumn(e.Property(x => x.CommitSummary));
            UserFk(e, x => x.UploadedById, "attendance_uploads", "uploaded_by_id", DeleteBehavior.SetNull);
            UserFk(e, x => x.CommittedById, "attendance_uploads", "committed_by_id", DeleteBehavior.SetNull);
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_attendance_uploads_created");
        });

        b.Entity<AttendanceUploadRow>(e =>
        {
            Table(e, "attendance_upload_rows");
            e.Property(x => x.EmployeeRef).HasMaxLength(255);
            e.Property(x => x.RawWorkMode).HasMaxLength(64);
            EnumColumn(e.Property(x => x.WorkMode), 16);
            e.Property(x => x.RawSource).HasMaxLength(64);
            e.Property(x => x.Error).HasMaxLength(255);
            e.HasOne<AttendanceUpload>().WithMany().HasForeignKey(x => x.UploadId)
                .HasConstraintName("fk_attendance_upload_rows_upload_id_attendance_uploads").OnDelete(DeleteBehavior.Cascade);
            UserFk(e, x => x.EmployeeId, "attendance_upload_rows", "employee_id", DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UploadId, x.RowNumber }).HasDatabaseName("ix_attendance_upload_rows_upload");
            e.HasIndex(x => new { x.EmployeeId, x.UploadId }).HasDatabaseName("ix_attendance_upload_rows_employee");
        });

        b.Entity<AttendanceDaily>(e =>
        {
            Table(e, "attendance_daily");
            EnumColumn(e.Property(x => x.Status), 16);
            EnumColumn(e.Property(x => x.WorkMode), 16);
            EnumColumn(e.Property(x => x.WfoSource), 20);
            JsonColumn(e.Property(x => x.Conflicts), StoredFormats.StringListConverter, StoredFormats.StringListComparer);
            EnumColumn(e.Property(x => x.UploadWorkMode), 16);
            EnumColumn(e.Property(x => x.OverrideWorkMode), 16);
            e.Property(x => x.OverrideNote).HasMaxLength(500);
            UserFk(e, x => x.EmployeeId, "attendance_daily", "employee_id", DeleteBehavior.Cascade);
            UserFk(e, x => x.OverriddenById, "attendance_daily", "overridden_by_id", DeleteBehavior.SetNull);
            e.HasOne<AttendanceUpload>().WithMany().HasForeignKey(x => x.UploadId)
                .HasConstraintName("fk_attendance_daily_upload_id_attendance_uploads").OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.EmployeeId, x.WorkDate }).IsUnique().HasDatabaseName("uq_attendance_daily_employee_date");
            e.HasIndex(x => new { x.WorkDate, x.Status }).HasDatabaseName("ix_attendance_daily_date_status");
        });

        b.Entity<AttendanceDailyCorrection>(e =>
        {
            Table(e, "attendance_daily_corrections");
            EnumColumn(e.Property(x => x.RequestedStatus), 16);
            e.Property(x => x.Reason).HasMaxLength(1000);
            ObjectColumn(e.Property(x => x.OriginalValues));
            EnumColumn(e.Property(x => x.Status), 16);
            e.Property(x => x.ReviewerComment).HasColumnType(Text);
            UserFk(e, x => x.EmployeeId, "attendance_daily_corrections", "employee_id", DeleteBehavior.Cascade);
            UserFk(e, x => x.ReviewedById, "attendance_daily_corrections", "reviewed_by_id", DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.EmployeeId, x.WorkDate }).HasDatabaseName("ix_daily_corrections_employee_date");
            e.HasIndex(x => x.Status).HasDatabaseName("ix_daily_corrections_status");
        });

        b.Entity<AuditLog>(e =>
        {
            Table(e, "audit_logs");
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.EntityId).HasMaxLength(64);
            // Python stores a missing value as the JSON text 'null', .NET as SQL NULL; both read as null (P11).
            ObjectColumn(e.Property(x => x.BeforeValues));
            ObjectColumn(e.Property(x => x.AfterValues));
            e.Property(x => x.RequestId).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            UserFk(e, x => x.ActorUserId, "audit_logs", "actor_user_id", DeleteBehavior.SetNull);
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_audit_created_at");
            e.HasIndex(x => new { x.EntityType, x.EntityId }).HasDatabaseName("ix_audit_entity");
            e.HasIndex(x => x.ActorUserId).HasDatabaseName("ix_audit_actor");
        });

        b.Entity<UserSession>(e =>
        {
            Table(e, "user_sessions");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.CsrfToken).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(255);
            UserFk(e, x => x.UserId, "user_sessions", "user_id", DeleteBehavior.Cascade);
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("uq_user_sessions_token_hash");
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_user_sessions_user");
        });

        b.Entity<LoginAttempt>(e =>
        {
            Table(e, "login_attempts");
            e.Property(x => x.Email).HasMaxLength(255);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => new { x.Email, x.CreatedAt }).HasDatabaseName("ix_login_attempts_email_time");
            e.HasIndex(x => new { x.IpAddress, x.CreatedAt }).HasDatabaseName("ix_login_attempts_ip_time");
        });

        b.Entity<OfficeSite>(e =>
        {
            Table(e, "office_sites");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Code).HasMaxLength(16);
            e.Property(x => x.NetworkName).HasMaxLength(64);
            e.Property(x => x.Address).HasColumnType(Text);
            // uq_office_sites_name_lower / uq_office_sites_code_lower: expression indexes in the migration SQL.
        });

        b.Entity<Device>(e =>
        {
            Table(e, "devices");
            e.Property(x => x.SerialNumber).HasMaxLength(64);
            e.Property(x => x.AssetTag).HasMaxLength(64);
            e.Property(x => x.MacAddress).HasMaxLength(17);
            e.Property(x => x.Hostname).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(120);
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.Notes).HasColumnType(Text);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .HasConstraintName("fk_devices_user_id_users").OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.SerialNumber).IsUnique().HasDatabaseName("uq_devices_serial_number");
            e.HasIndex(x => x.MacAddress).IsUnique().HasDatabaseName("uq_devices_mac_address");
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_devices_user_id");
        });

        b.Entity<WifiEvent>(e =>
        {
            Table(e, "wifi_events");
            EnumColumn(e.Property(x => x.EventType), 16);
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.AccessPoint).HasMaxLength(64);
            UserFk(e, x => x.EmployeeId, "wifi_events", "employee_id", DeleteBehavior.Cascade);
            e.HasOne<OfficeSite>().WithMany().HasForeignKey(x => x.SiteId)
                .HasConstraintName("fk_wifi_events_site_id_office_sites").OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId)
                .HasConstraintName("fk_wifi_events_device_id_devices").OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.EmployeeId, x.TsBucket }).IsUnique().HasDatabaseName("uq_wifi_events_employee_bucket");
            e.HasIndex(x => x.TsBucket).HasDatabaseName("ix_wifi_events_ts_bucket");
        });

        b.Entity<PresenceEvent>(e =>
        {
            Table(e, "presence_events");
            e.Property(x => x.Availability).HasMaxLength(32);
            e.Property(x => x.Activity).HasMaxLength(32);
            e.Property(x => x.Source).HasMaxLength(32);
            UserFk(e, x => x.EmployeeId, "presence_events", "employee_id", DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EmployeeId, x.TsBucket }).IsUnique().HasDatabaseName("uq_presence_events_employee_bucket");
            e.HasIndex(x => x.TsBucket).HasDatabaseName("ix_presence_events_ts_bucket");
        });

        b.Entity<AttendanceSession>(e =>
        {
            Table(e, "attendance_sessions");
            EnumColumn(e.Property(x => x.Status), 20);
            EnumColumn(e.Property(x => x.Source), 16);
            EnumColumn(e.Property(x => x.WorkLocation), 8);
            e.Property(x => x.Notes).HasMaxLength(500);
            e.Property(x => x.VoidReason).HasColumnType(Text);
            UserFk(e, x => x.EmployeeId, "attendance_sessions", "employee_id", DeleteBehavior.Cascade);
            e.HasIndex(x => x.EmployeeId).IsUnique().HasFilter("status = 'OPEN'").HasDatabaseName("uq_attendance_one_open_session");
            e.HasIndex(x => new { x.EmployeeId, x.CheckInAt }).HasDatabaseName("ix_attendance_employee_checkin");
            e.HasIndex(x => x.CheckInAt).HasDatabaseName("ix_attendance_checkin");
        });

        b.Entity<AttendanceCorrection>(e =>
        {
            Table(e, "attendance_corrections");
            EnumColumn(e.Property(x => x.RequestType), 20);
            ObjectColumn(e.Property(x => x.OriginalValues));
            e.Property(x => x.Reason).HasMaxLength(1000);
            EnumColumn(e.Property(x => x.Status), 16);
            e.Property(x => x.ReviewerComment).HasColumnType(Text);
            UserFk(e, x => x.EmployeeId, "attendance_corrections", "employee_id", DeleteBehavior.Cascade);
            UserFk(e, x => x.ReviewedById, "attendance_corrections", "reviewed_by_id", DeleteBehavior.SetNull);
            e.HasOne<AttendanceSession>().WithMany().HasForeignKey(x => x.OriginalSessionId)
                .HasConstraintName(IsSqlite
                    ? "fk_attendance_corrections_original_session_id_attendance_sessions"
                    : "fk_attendance_corrections_original_session_id_attendanc_e605") // PostgreSQL: SQLAlchemy shortened it to 63 chars
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AttendanceSession>().WithMany().HasForeignKey(x => x.ResultingSessionId)
                .HasConstraintName(IsSqlite
                    ? "fk_attendance_corrections_resulting_session_id_attendance_sessions"
                    : "fk_attendance_corrections_resulting_session_id_attendan_ca71")
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.EmployeeId, x.AttendanceDate }).HasDatabaseName("ix_corrections_employee_date");
            e.HasIndex(x => x.Status).HasDatabaseName("ix_corrections_status");
        });

        b.Entity<LeaveRecord>(e =>
        {
            Table(e, "leave_records");
            EnumColumn(e.Property(x => x.LeaveType), 16);
            EnumColumn(e.Property(x => x.Status), 16);
            e.Property(x => x.Reason).HasColumnType(Text);
            e.Property(x => x.ReviewerComment).HasColumnType(Text);
            UserFk(e, x => x.EmployeeId, "leave_records", "employee_id", DeleteBehavior.Cascade);
            UserFk(e, x => x.ReviewedById, "leave_records", "reviewed_by_id", DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.EmployeeId, x.StartDate, x.EndDate }).HasDatabaseName("ix_leave_employee_dates");
        });

        ApplyConventions(b);
    }

    private string Text => IsSqlite ? "TEXT" : "text";

    private static void Table<T>(EntityTypeBuilder<T> e, string table)
        where T : class
    {
        e.ToTable(table);
        e.HasKey("Id").HasName("pk_" + table);
    }

    private static void UserFk<T>(EntityTypeBuilder<T> e, System.Linq.Expressions.Expression<Func<T, object?>> fk,
                                  string table, string column, DeleteBehavior onDelete)
        where T : class =>
        e.HasOne<User>().WithMany().HasForeignKey(fk).HasConstraintName($"fk_{table}_{column}_users").OnDelete(onDelete);

    private static void EnumColumn<TEnum>(PropertyBuilder<TEnum> p, int length)
        where TEnum : struct, Enum =>
        p.HasConversion(EnumStrings.Converter<TEnum>()).HasMaxLength(length);

    private static void EnumColumn<TEnum>(PropertyBuilder<TEnum?> p, int length)
        where TEnum : struct, Enum =>
        p.HasConversion(EnumStrings.Converter<TEnum>()).HasMaxLength(length);

    private void JsonColumn<T>(PropertyBuilder<T> p, Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter converter,
                               Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer comparer)
    {
        p.HasConversion(converter, comparer).HasColumnType(IsSqlite ? "JSON" : "json");
    }

    private void ObjectColumn(PropertyBuilder<JsonObject?> p) =>
        p.HasConversion(StoredFormats.ObjectConverter, StoredFormats.ObjectComparer)
            .HasColumnType(IsSqlite ? "JSON" : "json");

    /// <summary>snake_case column names everywhere, and the provider-specific storage of times.</summary>
    private void ApplyConventions(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (IsSqlite)
                {
                    if (clr == typeof(DateTimeOffset))
                    {
                        property.SetValueConverter(StoredFormats.SqliteDateTimeConverter);
                        property.SetColumnType("DATETIME");
                    }
                    else if (clr == typeof(TimeOnly))
                    {
                        property.SetValueConverter(StoredFormats.SqliteTimeConverter);
                        property.SetColumnType("TIME");
                    }
                }
                else if (clr == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(StoredFormats.PostgresUtcConverter);
                    property.SetColumnType("timestamp with time zone");
                }
            }
        }
    }

    internal static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
