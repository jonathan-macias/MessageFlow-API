using MessageFlow.Application.Abstractions.Execution;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Executions;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Flows;
using MessageFlow.Infrastructure.Persistence;
using MessageFlow.Infrastructure.Persistence.Repositories;
using MessageFlow.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MessageFlow.Tests.Integration;

/// <summary>
/// Ejecución de Flows end-to-end sobre PostgreSQL real (Fase 7): segmentación con
/// recarga del agregado, persistencia de ítems y constraint único por fila.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresExecutionEngineTests(PostgresDatabaseFixture fixture)
{
    [RequiresDockerFact]
    public async Task El_motor_procesa_end_to_end_con_persistencia_real_y_segmentacion()
    {
        // Arrange: dataset con 2 filas, flow activo y ejecución pendiente.
        var setup = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(setup);
        var rows = await IntegrationData.AddRowsAsync(setup, dataset.Id,
            ("Juan", "juan@x.com", null), ("Pedro", "pedro@x.com", null));
        var correoColumnId = dataset.Columns.First(c => c.Name == "Correo").Id;

        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var provider = new StubMessageProvider();

        var flow = await IntegrationData.CreateActiveFlowAsync(setup, dataset, correoColumnId, "Hola {{Nombre}}");

        var execution = FlowExecution.Start(
            flow.Id,
            ExecutionType.Immediate,
            TriggerSource.Manual,
            $"manual:{Guid.NewGuid():N}",
            clock.GetUtcNow(),
            rows.Count);

        setup.FlowExecutions.Add(execution);
        await setup.SaveChangesAsync();

        // Act: mismo patrón que el host real — UN DbContext con alcance para toda la
        // corrida usado por repositorios y unidad de trabajo (segmentación real:
        // SaveChanges por lote + ClearTrackedEntities + recarga desde PostgreSQL).
        await using var scopedContext = fixture.CreateContext();
        var engine = BuildEngine(scopedContext, provider, clock, new FlowExecutionEngineOptions { SaveBatchSize = 1 });

        var summary = await engine.ProcessPendingAsync(execution.Id);

        // Assert: resumen y estado persistido en un contexto completamente nuevo.
        Assert.Equal(ExecutionStatus.Completed, summary.FinalStatus);
        Assert.Equal(2, summary.SuccessfulRecords);

        var persisted = await fixture.CreateContext().FlowExecutions
            .Include(e => e.Items)
            .SingleAsync(e => e.Id == execution.Id);

        Assert.Equal(ExecutionStatus.Completed, persisted.Status);
        Assert.Equal(2, persisted.ProcessedRecords);
        Assert.Equal(2, persisted.SuccessfulRecords);
        Assert.Equal(0, persisted.FailedRecords);
        Assert.Equal(2, persisted.Items.Count);
        Assert.All(persisted.Items, item => Assert.Equal(ExecutionItemStatus.Succeeded, item.Status));
        Assert.NotNull(persisted.CompletedAtUtc);
        Assert.Null(persisted.ErrorMessage);

        Assert.Equal(2, provider.Sent.Count);
        Assert.Contains(provider.Sent, m => m.Body == "Hola Juan");
        Assert.Contains(provider.Sent, m => m.Body == "Hola Pedro");
    }

    [RequiresDockerFact]
    public async Task El_constraint_unico_por_fila_respalda_la_deteccion_en_memoria()
    {
        // El agregado detecta duplicados en memoria; este test verifica el respaldo
        // estructural en PostgreSQL (ux_flow_execution_items_execution_row): si una
        // segunda instancia cargara la ejecución sin los ítems previos y registrara
        // la misma fila, el INSERT falla al guardar.
        var context = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(context);
        var row = (await IntegrationData.AddRowsAsync(context, dataset.Id, ("Juan", "juan@x.com", null))).Single();
        var flow = await IntegrationData.CreateActiveFlowAsync(context, dataset, dataset.Columns.First(c => c.Name == "Correo").Id, "Hola {{Nombre}}");

        var execution = FlowExecution.Start(flow.Id, ExecutionType.Immediate, TriggerSource.Manual, $"manual:{Guid.NewGuid():N}", DateTimeOffset.UtcNow, 1);
        context.FlowExecutions.Add(execution);
        await context.SaveChangesAsync();

        var repositoryA = new FlowExecutionRepository(fixture.CreateContext(), new TestSupport.FakeCurrentUser { UserId = null });
        var repositoryB = new FlowExecutionRepository(fixture.CreateContext(), new TestSupport.FakeCurrentUser { UserId = null });

        // Instancia A: adjunta su copia en un contexto propio (como un scope real),
        // registra la fila y persiste.
        var aggregateA = (await repositoryA.FindByIdAsync(execution.Id))!;
        using var contextA = fixture.CreateContext();
        contextA.FlowExecutions.Attach(aggregateA);

        aggregateA.RegisterSuccess(row.Id, DateTimeOffset.UtcNow);
        await contextA.SaveChangesAsync();

        // Instancia B recarga SIN incluir ítems (misma consulta que usa el motor),
        // adjunta su copia en otro contexto e intenta registrar la misma fila:
        // el constraint único de PostgreSQL lo impide al guardar.
        var aggregateB = (await repositoryB.FindByIdAsync(execution.Id))!;
        await using var contextB = fixture.CreateContext();
        contextB.FlowExecutions.Attach(aggregateB);

        aggregateB.RegisterSuccess(row.Id, DateTimeOffset.UtcNow.AddSeconds(1));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => contextB.SaveChangesAsync());
    }

    private static FlowExecutionEngine BuildEngine(
        MessageFlowDbContext context,
        StubMessageProvider provider,
        MutableTimeProvider clock,
        FlowExecutionEngineOptions options) => new(
            new FlowRepository(context, new TestSupport.FakeCurrentUser { UserId = null }),
            new DatasetRepository(context, new TestSupport.FakeCurrentUser { UserId = null }),
            new FlowExecutionRepository(context, new TestSupport.FakeCurrentUser { UserId = null }),
            [provider],
            new ScopedUnitOfWork(context),
            clock,
            NullLogger<FlowExecutionEngine>.Instance,
            options);

    /// <summary>Replica el binding de producción: el DbContext con alcance ES la unidad de trabajo.</summary>
    private sealed class ScopedUnitOfWork(MessageFlowDbContext context) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => context.SaveChangesAsync(cancellationToken);

        public void ClearTrackedEntities() => context.ClearTrackedEntities();
    }
}
