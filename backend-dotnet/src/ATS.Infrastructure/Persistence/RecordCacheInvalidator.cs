using System.Runtime.CompilerServices;
using Ats.Application.Attendance;
using Ats.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ats.Infrastructure.Persistence;

/// <summary>
/// Bumps the record cache's write version after every save that added, changed or removed timesheet days or uploads
/// (Python: the after_commit listener of record_cache.py), so cached day records are never served stale.
/// </summary>
public sealed class RecordCacheInvalidator(RecordCache cache) : SaveChangesInterceptor
{
    private static readonly ConditionalWeakTable<DbContext, object> Touched = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
                                                                          CancellationToken cancellationToken = default)
    {
        Mark(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Mark(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Bump(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Bump(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    private static void Mark(DbContext? context)
    {
        if (context is not null && context.ChangeTracker.Entries().Any(e =>
                e.Entity is AttendanceDaily or AttendanceUpload && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            Touched.AddOrUpdate(context, true);
        }
    }

    private void Bump(DbContext? context)
    {
        if (context is not null && Touched.Remove(context))
        {
            cache.BumpWriteVersion();
        }
    }
}
