using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Filters;
using MessageFlow.Domain.Flows;
using MessageFlow.Infrastructure.Persistence;
using MessageFlow.Infrastructure.Persistence.Repositories;
using MessageFlow.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MessageFlow.Tests.Integration;

/// <summary>
/// Persistencia real en PostgreSQL: repositorios EF, jsonb, constraints únicos,
/// reclamo atómico y cola de pendientes (Fases 3, 5, 7).
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresRepositoriesTests(PostgresDatabaseFixture fixture)
{
    private static Guid ColumnId(Domain.Datasets.Dataset dataset, string name)
        => dataset.Columns.First(c => c.Name == name).Id;

    [RequiresDockerFact]
    public async Task Keyset_paging_avanza_por_lotes_sin_solaparse()
    {
        var context = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(context);
        await IntegrationData.AddRowsAsync(context, dataset.Id,
            ("A", "a@x.com", null), ("B", "b@x.com", null), ("C", "c@x.com", null),
            ("D", "d@x.com", null), ("E", "e@x.com", null), ("F", "f@x.com", null),
            ("G", "g@x.com", null), ("H", "h@x.com", null), ("I", "i@x.com", null),
            ("J", "j@x.com", null));

        var repository = new DatasetRepository(fixture.CreateContext(), new TestSupport.FakeCurrentUser { UserId = null });

        long cursor = 0;
        var batches = new List<long[]>();

        while (true)
        {
            var page = await repository.GetRowsAfterAsync(dataset.Id, cursor, limit: 4);

            if (page.Count == 0)
            {
                break;
            }

            batches.Add([.. page.Select(r => r.RowNumber)]);
            cursor = page.Max(r => r.RowNumber);
        }

        Assert.Equal([[1L, 2, 3, 4], [5L, 6, 7, 8], [9L, 10]], batches);
    }

    [RequiresDockerFact]
    public async Task El_filtro_raiz_sobrevive_el_roundtrip_jsonb_intacto()
    {
        var context = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(context);
        var correoColumnId = dataset.Columns.First(c => c.Name == "Correo").Id;

        var filter = FilterGroup.Create(
            FilterCompositionOperator.And,
            [new FilterCondition(correoColumnId, FilterOperator.Contains, "@gmail.com")]);

        await IntegrationData.CreateActiveFlowAsync(context, dataset, correoColumnId, "Hola {{Nombre}}", filter);

        // Contexto nuevo: lectura desde PostgreSQL sin entidades en memoria.
        var reloaded = await fixture.CreateContext().Flows.SingleAsync(f => f.DatasetId == dataset.Id);

        Assert.NotNull(reloaded.RootFilter);
        Assert.Equal(FilterCompositionOperator.And, reloaded.RootFilter!.Composition);

        var condition = Assert.IsType<FilterCondition>(Assert.Single(reloaded.RootFilter.Conditions));
        Assert.Equal(correoColumnId, condition.ColumnId);
        Assert.Equal(FilterOperator.Contains, condition.Operator);
        Assert.Equal("@gmail.com", condition.Value);
    }

    [RequiresDockerFact]
    public async Task La_clave_de_idempotencia_es_unica_a_nivel_de_base_de_datos()
    {
        var context = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(context);
        var flow = await IntegrationData.CreateActiveFlowAsync(context, dataset, ColumnId(dataset, "Correo"), "Hola {{Nombre}}");

        var key = $"sched:{flow.Id:N}:{DateTimeOffset.UtcNow:O}";
        context.FlowExecutions.Add(FlowExecution.Start(flow.Id, ExecutionType.Immediate, TriggerSource.Scheduled, key, DateTimeOffset.UtcNow, 0));
        await context.SaveChangesAsync();

        using var secondContext = fixture.CreateContext();
        secondContext.FlowExecutions.Add(FlowExecution.Start(flow.Id, ExecutionType.Immediate, TriggerSource.Scheduled, key, DateTimeOffset.UtcNow, 0));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [RequiresDockerFact]
    public async Task El_reclamo_atomico_gana_una_unica_vez_y_fija_running()
    {
        var context = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(context);
        var flow = await IntegrationData.CreateActiveFlowAsync(context, dataset, ColumnId(dataset, "Correo"), "Hola {{Nombre}}");

        var execution = FlowExecution.Start(flow.Id, ExecutionType.Immediate, TriggerSource.Manual, $"manual:{Guid.NewGuid():N}", DateTimeOffset.UtcNow, 0);
        context.FlowExecutions.Add(execution);
        await context.SaveChangesAsync();

        var claimedAtUtc = DateTimeOffset.UtcNow;
        var repositoryA = new FlowExecutionRepository(fixture.CreateContext(), new TestSupport.FakeCurrentUser { UserId = null });
        var repositoryB = new FlowExecutionRepository(fixture.CreateContext(), new TestSupport.FakeCurrentUser { UserId = null });

        Assert.True(await repositoryA.TryBeginProcessingAsync(execution.Id, claimedAtUtc));
        Assert.False(await repositoryB.TryBeginProcessingAsync(execution.Id, claimedAtUtc.AddSeconds(1)));

        var reloaded = await fixture.CreateContext().FlowExecutions.SingleAsync(e => e.Id == execution.Id);
        Assert.Equal(ExecutionStatus.Running, reloaded.Status);

        // PostgreSQL almacena timestamptz con precisión de microsegundos: se compara en esa granularidad.
        Assert.Equal(WithMicrosecondPrecision(claimedAtUtc), reloaded.StartedAtUtc);
    }

    [RequiresDockerFact]
    public async Task La_cola_de_pendientes_ordenada_por_antiguedad_respeta_el_tope()
    {
        var context = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(context);
        var flow = await IntegrationData.CreateActiveFlowAsync(context, dataset, ColumnId(dataset, "Correo"), "Hola");

        var old = FlowExecution.Start(flow.Id, ExecutionType.Immediate, TriggerSource.Manual, "k-old", Now(-30), 0);
        var middle = FlowExecution.Start(flow.Id, ExecutionType.Immediate, TriggerSource.Manual, "k-mid", Now(-20), 0);
        var recent = FlowExecution.Start(flow.Id, ExecutionType.Immediate, TriggerSource.Manual, "k-new", Now(-10), 0);

        context.FlowExecutions.AddRange(recent, old, middle);
        await context.SaveChangesAsync();

        var repository = new FlowExecutionRepository(fixture.CreateContext(), new TestSupport.FakeCurrentUser { UserId = null });

        var firstBatch = await repository.ListPendingIdsAsync(maxCount: 2);
        Assert.Equal([old.Id, middle.Id], firstBatch);

        await repository.TryBeginProcessingAsync(firstBatch[0], Now(-5));

        var secondBatch = await repository.ListPendingIdsAsync(maxCount: 2);
        Assert.Equal([middle.Id, recent.Id], secondBatch);
    }

    [RequiresDockerFact]
    public async Task El_interceptor_de_auditoria_sella_metadatos_al_persistir()
    {
        var context = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(context);

        var before = DateTimeOffset.UtcNow;
        await IntegrationData.CreateActiveFlowAsync(context, dataset, ColumnId(dataset, "Correo"), "Hola");
        var after = DateTimeOffset.UtcNow;

        var saved = await fixture.CreateContext().Flows.SingleAsync(f => f.DatasetId == dataset.Id);

        Assert.Equal("system", saved.CreatedBy);
        Assert.InRange(saved.CreatedAtUtc, before.AddSeconds(-1), after.AddSeconds(1));
    }

    private static DateTimeOffset Now(int offsetSeconds) => DateTimeOffset.UtcNow.AddSeconds(offsetSeconds);

    private static DateTimeOffset WithMicrosecondPrecision(DateTimeOffset value)
        => new(value.Ticks - value.Ticks % 10, value.Offset);
}
