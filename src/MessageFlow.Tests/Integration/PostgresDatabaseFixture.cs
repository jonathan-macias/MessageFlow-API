using MessageFlow.Infrastructure;
using MessageFlow.Infrastructure.CurrentUser;
using MessageFlow.Infrastructure.Persistence;
using MessageFlow.Infrastructure.Persistence.Files;
using MessageFlow.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace MessageFlow.Tests.Integration;

/// <summary>
/// Fixture compartido por todos los tests de integración: levanta UN contenedor
/// PostgreSQL 18 real (Testcontainers), aplica las migraciones de EF Core y expone
/// contextos nuevos por prueba (tracking limpio, como instancias independientes).
/// </summary>
public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    // La imagen oficial de postgres no trae pgvector: sin la extensión, la migración que
    // crea las tablas de embeddings falla y los tests de integración de búsqueda vectorial
    // no podrían ejecutarse. pgvector/pgvector:pg18 es PostgreSQL 18 con la extensión.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg18")
        .WithDatabase("messageflow_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Contexto nuevo por prueba/consulta: sin entidades rastreadas heredadas.</summary>
    public MessageFlowDbContext CreateContext() => new(BuildOptions());

    public DbContextOptions<MessageFlowDbContext> BuildOptions() => new DbContextOptionsBuilder<MessageFlowDbContext>()
        .UseNpgsql(_container.GetConnectionString(), npgsql =>
            npgsql.MigrationsAssembly(typeof(DependencyInjection).Assembly.FullName))
        .AddInterceptors(new AuditSaveChangesInterceptor(TimeProvider.System, new SystemCurrentUser()))
        .Options;
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresDatabaseFixture>
{
    public const string Name = "PostgreSQL";
}
