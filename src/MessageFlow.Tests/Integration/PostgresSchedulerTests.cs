using MessageFlow.Application.Abstractions.Execution;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Executions;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;
using MessageFlow.Infrastructure.Persistence;
using MessageFlow.Infrastructure.Persistence.Repositories;
using MessageFlow.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MessageFlow.Tests.Integration;

/// <summary>
/// Scheduler end-to-end sobre PostgreSQL real (Fase 8): producción idempotente de
/// ejecuciones, ancla de ocurrencias y consumo por el motor con trigger de datos.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresSchedulerTests(PostgresDatabaseFixture fixture)
{
    [RequiresDockerFact]
    public async Task OneTime_vencida_se_encola_se_procesa_y_el_ancla_avanza()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var runAtUtc = clock.GetUtcNow().AddHours(-1);

        var setup = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(setup);
        var rows = await IntegrationData.AddRowsAsync(setup, dataset.Id, ("Juan", "juan@x.com", null));
        var correoColumnId = dataset.Columns.First(c => c.Name == "Correo").Id;

        var flow = await IntegrationData.CreateActiveFlowAsync(
            setup,
            dataset,
            correoColumnId,
            "Hola {{Nombre}}",
            schedule: FlowSchedule.OneTime(runAtUtc, TimeZoneId.Create("UTC")),
            createdAtUtc: runAtUtc.AddDays(-1)); // ancla determinista anterior a la ocurrencia

        var provider = new StubMessageProvider();
        await using var scopedContext = fixture.CreateContext();
        var processor = BuildProcessor(scopedContext, clock, provider);

        // Act: producir → consumir → volver a producir.
        var enqueued = await processor.EnqueueDueExecutionsAsync();
        var processed = await processor.ProcessQueuedExecutionsAsync();
        var reEnqueued = await processor.EnqueueDueExecutionsAsync();

        // Assert
        Assert.Equal(1, enqueued);
        Assert.Equal(1, processed);
        Assert.Equal(0, reEnqueued); // la ocurrencia ya se consumió: el ancla avanzó

        await using var verification = fixture.CreateContext();
        var execution = await verification.FlowExecutions.SingleAsync(e => e.FlowId == flow.Id);

        Assert.Equal(TriggerSource.Scheduled, execution.TriggerSource);
        Assert.Equal(runAtUtc, execution.ScheduledForUtc);
        Assert.Equal(ExecutionStatus.Completed, execution.Status);
        Assert.Equal(rows.Count, execution.SuccessfulRecords);
        Assert.Single(provider.Sent);

        // La ancla consultable es exactamente la ocurrencia programada.
        var repository = new FlowExecutionRepository(verification, new TestSupport.FakeCurrentUser { UserId = null });
        Assert.Equal(runAtUtc, await repository.GetLatestScheduledOccurrenceUtcAsync(flow.Id));
    }

    [RequiresDockerFact]
    public async Task DataTriggered_ejecuta_solo_las_filas_del_aniversario_actual()
    {
        // "Hoy" local del Flow: 23 de agosto de 2026 (UTC).
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));

        var setup = fixture.CreateContext();
        var dataset = await IntegrationData.CreateDatasetAsync(setup, withBirthDateColumn: true);
        var rows = await IntegrationData.AddRowsAsync(setup, dataset.Id,
            ("Juan", "juan@x.com", "1990-08-23"),   // cumple hoy, año distinto
            ("Pedro", "pedro@x.com", "1990-01-01"), // no cumple
            ("Ana", "ana@x.com", "2001-08-23"));    // cumple hoy

        var correoColumnId = dataset.Columns.First(c => c.Name == "Correo").Id;
        var fechaColumnId = dataset.Columns.First(c => c.Name == "FechaNacimiento").Id;

        // Borrador con trigger de datos configurado ANTES de activar (transiciones del agregado).
        var flow = Flow.CreateDraft(
            $"Cumple-{Guid.NewGuid():N}",
            null,
            dataset.Id,
            Channel.WhatsApp,
            MessageTemplate.Create("Feliz cumple {{Nombre}}"),
            FlowSchedule.DataTriggered(TimeZoneId.Create("UTC")),
            "integration-test-user");

        flow.SetRecipientColumn(correoColumnId);
        flow.ConfigureDataTrigger(DataTriggerConfig.Create(fechaColumnId, DateTriggerMatchMode.AnniversaryDayMonth));
        flow.Activate();

        setup.Flows.Add(flow);
        await setup.SaveChangesAsync();

        var provider = new StubMessageProvider();
        await using var scopedContext = fixture.CreateContext();
        var processor = BuildProcessor(scopedContext, clock, provider);

        var enqueued = await processor.EnqueueDueExecutionsAsync();
        var processed = await processor.ProcessQueuedExecutionsAsync();

        // Assert: una ejecución por día; solo las filas aniversario generan ítem.
        Assert.Equal(1, enqueued);
        Assert.Equal(1, processed);

        await using var verification = fixture.CreateContext();
        var execution = await verification.FlowExecutions.Include(e => e.Items).SingleAsync(e => e.FlowId == flow.Id);

        Assert.Equal(ExecutionStatus.Completed, execution.Status);
        Assert.Equal(3, execution.TotalRecords);     // filas evaluadas
        Assert.Equal(2, execution.ProcessedRecords); // solo aniversarios
        Assert.Equal(2, execution.SuccessfulRecords);
        Assert.Equal(2, execution.Items.Count);

        var successfulRowIds = execution.Items
            .Where(i => i.Status == ExecutionItemStatus.Succeeded)
            .Select(i => i.DatasetRowId)
            .OrderBy(id => id)
            .ToList();

        Assert.Equal(
            rows.Where(r => r.Values["Nombre"] is "Juan" or "Ana").Select(r => r.Id).OrderBy(id => id),
            successfulRowIds);

        Assert.Single(provider.Sent.Where(m => m.Body == "Feliz cumple Juan"));
        Assert.Single(provider.Sent.Where(m => m.Body == "Feliz cumple Ana"));
        Assert.DoesNotContain(provider.Sent, m => m.Body.Contains("Pedro"));
    }

    private static ScheduledExecutionProcessor BuildProcessor(
        MessageFlowDbContext context,
        MutableTimeProvider clock,
        StubMessageProvider provider)
    {
        var flowRepository = new FlowRepository(context, new TestSupport.FakeCurrentUser { UserId = null });
        var datasetRepository = new DatasetRepository(context, new TestSupport.FakeCurrentUser { UserId = null });
        var executionRepository = new FlowExecutionRepository(context, new TestSupport.FakeCurrentUser { UserId = null });
        var unitOfWork = new ScopedUnitOfWork(context);

        return new ScheduledExecutionProcessor(
            flowRepository,
            datasetRepository,
            executionRepository,
            unitOfWork,
            new FlowExecutionEngine(
                flowRepository,
                datasetRepository,
                executionRepository,
                [provider],
                unitOfWork,
                clock,
                NullLogger<FlowExecutionEngine>.Instance,
                new FlowExecutionEngineOptions()),
            clock,
            NullLogger<ScheduledExecutionProcessor>.Instance,
            new FlowSchedulingOptions());
    }

    /// <summary>Replica el binding de producción: el DbContext con alcance ES la unidad de trabajo.</summary>
    private sealed class ScopedUnitOfWork(MessageFlowDbContext context) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => context.SaveChangesAsync(cancellationToken);

        public void ClearTrackedEntities() => context.ClearTrackedEntities();
    }
}
