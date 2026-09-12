using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Flows;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Infrastructure.Persistence.Configurations;
using MessageFlow.Infrastructure.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MessageFlow.Infrastructure.Persistence;

/// <summary>
/// DbContext único del sistema (PostgreSQL). Integra ASP.NET Core Identity
/// (ApplicationUser, roles, claims, logins) en la misma base de datos que
/// los agregados de dominio. Implementa IUnitOfWork para que los handlers
/// confirmen todos los cambios en una sola transacción.
/// </summary>
public sealed class MessageFlowDbContext(DbContextOptions<MessageFlowDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IUnitOfWork
{
    public DbSet<Flow> Flows => Set<Flow>();
    public DbSet<FlowExecution> FlowExecutions => Set<FlowExecution>();
    public DbSet<Dataset> Datasets => Set<Dataset>();
    public DbSet<DatasetColumn> DatasetColumns => Set<DatasetColumn>();
    public DbSet<DatasetRow> DatasetRows => Set<DatasetRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Identity: crea las tablas ASPNetUsers, ASPNetRoles, etc.
        base.OnModelCreating(modelBuilder);

        // Dominio: crea las tablas flows, datasets, etc.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FlowConfiguration).Assembly);

        // Seed de roles por defecto (Admin y User). Se aplican siempre:
        // si la tabla ya tiene datos, EF ignora los duplicados.
        var adminRoleId = new Guid("11111111-1111-1111-1111-111111111111");
        var userRoleId = new Guid("22222222-2222-2222-2222-222222222222");

        modelBuilder.Entity<IdentityRole<Guid>>().HasData(
            new IdentityRole<Guid>
            {
                Id = adminRoleId,
                Name = "Admin",
                NormalizedName = "ADMIN",
                ConcurrencyStamp = "admin-seed-1",
            },
            new IdentityRole<Guid>
            {
                Id = userRoleId,
                Name = "User",
                NormalizedName = "USER",
                ConcurrencyStamp = "user-seed-1",
            });
    }

    public void ClearTrackedEntities() => ChangeTracker.Clear();
}
