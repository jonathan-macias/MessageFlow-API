using MessageFlow.Application.Abstractions;
using MessageFlow.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MessageFlow.Infrastructure.Persistence;

/// <summary>
/// Rellena CreatedAtUtc/UpdatedAtUtc/CreatedBy/UpdatedBy (§27) en todas las entidades
/// auditables antes de persistir. El usuario proviene de ICurrentUser; en background
/// queda "system".
/// </summary>
public sealed class AuditSaveChangesInterceptor(TimeProvider timeProvider, ICurrentUser currentUser)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyAudit(eventData.Context?.ChangeTracker);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyAudit(eventData.Context?.ChangeTracker);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyAudit(ChangeTracker? changeTracker)
    {
        if (changeTracker is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var user = currentUser.UserId ?? "system";

        foreach (var entry in changeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAtUtc = now;
                    entry.Entity.CreatedBy = user;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = now;
                    entry.Entity.UpdatedBy = user;
                    break;
            }
        }
    }
}
