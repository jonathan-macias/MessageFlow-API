using MessageFlow.Application.Abstractions.Execution;
using MessageFlow.Application.Executions;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;
using MessageFlow.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageFlow.Tests;

/// <summary>
/// Tests del scheduler (Fase 8): producción idempotente de ejecuciones por reloj y
/// por trigger de datos, avance del ancla de ocurrencias y consumo aislado de la cola.
/// </summary>
public class FlowSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 10, 30, 0, TimeSpan.Zero); // domingo

    private readonly IReadOnlyList<ColumnDefinition> _columns =
    [
        new(Guid.CreateVersion7(), "Nombre", ColumnDataType.Text),
        new(Guid.CreateVersion7(), "Correo", ColumnDataType.Text),
        new(Guid.CreateVersion7(), "FechaNacimiento", ColumnDataType.Date),
    ];

    private static DatasetRow Row(Guid datasetId, long number, string nombre, string correo)
        => new(datasetId, number,
        [
            new KeyValuePair<string, string?>("Nombre", nombre),
            new KeyValuePair<string, string?>("Correo", correo),
        ]);

    // ------------------------------- Productor -------------------------------

    [Fact]
    public async Task Una_OneTime_vencida_encola_ejecucion_con_clave_y_ocurrencia()
    {
        var harness = new SchedulerHarness(_columns);
        var runAtUtc = Now.AddHours(-1);
        harness.AddClockFlow(FlowSchedule.OneTime(runAtUtc, harness.Zone), createdAtUtc: Now.AddDays(-1));

        var created = await harness.BuildProcessor().EnqueueDueExecutionsAsync();

        Assert.Equal(1, created);
        var execution = Assert.Single(harness.Executions.Store.Values);
        Assert.Equal(TriggerSource.Scheduled, execution.TriggerSource);
        Assert.Equal(runAtUtc, execution.ScheduledForUtc);
        Assert.Equal(ExecutionStatus.Pending, execution.Status);
        Assert.True(await harness.Executions.ExistsByIdempotencyKeyAsync(execution.IdempotencyKey));
        Assert.Contains("sched:", execution.IdempotencyKey);
    }

    [Fact]
    public async Task Una_OneTime_futura_no_encola_nada()
    {
        var harness = new SchedulerHarness(_columns);
        harness.AddClockFlow(FlowSchedule.OneTime(Now.AddHours(2), harness.Zone), Now.AddDays(-1));

        var created = await harness.BuildProcessor().EnqueueDueExecutionsAsync();

        Assert.Equal(0, created);
        Assert.Empty(harness.Executions.Store);
    }

    [Fact]
    public async Task Repetir_el_ciclo_no_duplica_la_misma_ocurrencia()
    {
        var harness = new SchedulerHarness(_columns);
        harness.AddClockFlow(FlowSchedule.OneTime(Now.AddMinutes(-30), harness.Zone), Now.AddDays(-1));
        var processor = harness.BuildProcessor();

        await processor.EnqueueDueExecutionsAsync();
        var secondRun = await processor.EnqueueDueExecutionsAsync();

        Assert.Equal(0, secondRun);
        Assert.Single(harness.Executions.Store);
    }

    [Fact]
    public async Task Recurring_avanza_por_todas_las_ocurrencias_vencidas_y_luego_se_detiene()
    {
        var harness = new SchedulerHarness(_columns);
        // Diaria 10:00 UTC creada el 20/08 09:00; ahora es 23/08 10:30 ⇒ 4 ocurrencias vencidas.
        harness.AddClockFlow(
            FlowSchedule.Recurring(RecurrenceRule.Daily(new TimeOnly(10, 0)), harness.Zone),
            createdAtUtc: new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero));

        var processor = harness.BuildProcessor();
        var firstRun = await processor.EnqueueDueExecutionsAsync();
        var occurrences = harness.Executions.Store.Values
            .Select(e => e.ScheduledForUtc!.Value.UtcDateTime)
            .OrderBy(d => d)
            .ToList();

        Assert.Equal(4, firstRun);
        Assert.Equal(4, occurrences.Count);
        Assert.All(occurrences, o => Assert.Equal(10, o.Hour));

        // El ancla quedó en la última ocurrencia: un nuevo ciclo no crea nada más hoy.
        var secondRun = await processor.EnqueueDueExecutionsAsync();

        Assert.Equal(0, secondRun);
        Assert.Equal(4, harness.Executions.Store.Count);
    }

    [Fact]
    public async Task El_cap_limita_la_recuperacion_de_ocurrencias_atrasadas()
    {
        var harness = new SchedulerHarness(_columns)
        {
            Options = new FlowSchedulingOptions { MaxCatchUpOccurrences = 2 },
        };

        harness.AddClockFlow(
            FlowSchedule.Recurring(RecurrenceRule.Daily(new TimeOnly(8, 0)), harness.Zone),
            createdAtUtc: Now.AddDays(-30));

        var created = await harness.BuildProcessor().EnqueueDueExecutionsAsync();

        Assert.Equal(2, created);
    }

    [Fact]
    public async Task DataTriggered_encola_una_unica_ejecucion_por_fecha_local()
    {
        var harness = new SchedulerHarness(_columns);
        var triggerColumnId = _columns.First(c => c.Name == "FechaNacimiento").Id;
        harness.AddDataTriggeredFlow(triggerColumnId, harness.Zone);

        var processor = harness.BuildProcessor();

        Assert.Equal(1, await processor.EnqueueDueExecutionsAsync());
        Assert.Equal(0, await processor.EnqueueDueExecutionsAsync());

        var execution = Assert.Single(harness.Executions.Store.Values);
        Assert.Equal(TriggerSource.DataMatch, execution.TriggerSource);
        Assert.Null(execution.ScheduledForUtc);
        Assert.Equal($"data:{execution.FlowId:N}:{Now.UtcDateTime:yyyy-MM-dd}", execution.IdempotencyKey);
    }

    [Fact]
    public async Task Immediate_jamas_se_programa_y_los_inactivos_se_ignoran()
    {
        var harness = new SchedulerHarness(_columns);
        harness.AddManualOnlyFlow(activate: true);   // Immediate activo
        harness.AddClockFlow(FlowSchedule.OneTime(Now.AddHours(-1), harness.Zone), Now.AddDays(-1), activate: false);

        var created = await harness.BuildProcessor().EnqueueDueExecutionsAsync();

        Assert.Equal(0, created);
        Assert.Empty(harness.Executions.Store);
    }

    // ------------------------------- Consumidor -------------------------------

    [Fact]
    public async Task El_consumidor_procesa_la_cola_aislando_los_fallos_sistemicos()
    {
        var harness = new SchedulerHarness(_columns);
        harness.LoadRows(Row(harness.DatasetId, 1, "Juan", "juan@x.com"));

        // Flow sano con una ejecución manual pendiente.
        var healthy = harness.AddManualOnlyFlow(activate: true);
        var healthyExecution = harness.EnqueueManual(healthy);

        // Flow roto (sin columna destinataria): el motor fallará sistémicamente.
        var broken = harness.AddManualOnlyFlow(activate: true, withoutRecipientColumn: true);
        var brokenExecution = harness.EnqueueManual(broken);

        var processed = await harness.BuildProcessor().ProcessQueuedExecutionsAsync();

        Assert.Equal(1, processed);
        Assert.Equal(ExecutionStatus.Completed, healthyExecution.Status);
        Assert.Equal(ExecutionStatus.Failed, brokenExecution.Status);
        Assert.Contains("columna destinataria", brokenExecution.ErrorMessage);
    }

    // --------------------------------------------------------------------------

    /// <summary>Ensambla productor + motor sobre dobles en memoria.</summary>
    private sealed class SchedulerHarness(IReadOnlyList<ColumnDefinition> columns)
    {
        private readonly ColumnDefinition[] _columns = [.. columns];

        public Guid DatasetId { get; } = Guid.CreateVersion7();
        public TimeZoneId Zone { get; } = TimeZoneId.Create("UTC");
        public MutableTimeProvider Clock { get; } = new(Now);
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FakeFlowRepository Flows { get; } = new();
        public FakeDatasetRepository Datasets { get; } = new();
        public FakeFlowExecutionRepository Executions { get; } = new();
        public StubMessageProvider Provider { get; } = new();
        public FlowSchedulingOptions Options { get; init; } = new();

        public void LoadRows(params DatasetRow[] rows)
            => Datasets.Load(_columns, rows);

        public Flow AddClockFlow(FlowSchedule schedule, DateTimeOffset createdAtUtc, bool activate = true)
            => AddFlow(schedule, createdAtUtc, activate, dataTriggerColumnId: null);

        public Flow AddDataTriggeredFlow(Guid triggerColumnId, TimeZoneId zone)
            => AddFlow(FlowSchedule.DataTriggered(zone), Now.AddDays(-1), activate: true, triggerColumnId);

        public Flow AddManualOnlyFlow(bool activate, bool withoutRecipientColumn = false)
            => AddFlow(FlowSchedule.Immediate(Zone), Now.AddDays(-1), activate, dataTriggerColumnId: null, withoutRecipientColumn);

        public FlowExecution EnqueueManual(Flow flow)
        {
            var execution = FlowExecution.Start(
                flow.Id,
                flow.Schedule.Type,
                TriggerSource.Manual,
                $"manual:{Guid.NewGuid():N}",
                Clock.GetUtcNow(),
                0);

            Executions.Store[execution.Id] = execution;
            return execution;
        }

        public ScheduledExecutionProcessor BuildProcessor() => new(
            Flows,
            Datasets,
            Executions,
            UnitOfWork,
            new FlowExecutionEngine(
                Flows,
                Datasets,
                Executions,
                [Provider],
                UnitOfWork,
                Clock,
                NullLogger<FlowExecutionEngine>.Instance,
                new FlowExecutionEngineOptions()),
            Clock,
            NullLogger<ScheduledExecutionProcessor>.Instance,
            Options);

        private Flow AddFlow(
            FlowSchedule schedule,
            DateTimeOffset createdAtUtc,
            bool activate,
            Guid? dataTriggerColumnId,
            bool withoutRecipientColumn = false)
        {
            var flow = Flow.CreateDraft(
                $"Flow-{Guid.NewGuid():N}",
                description: null,
                DatasetId,
                Channel.WhatsApp,
                MessageTemplate.Create("Hola {{Nombre}}"),
                schedule,
                "owner-1");

            if (!withoutRecipientColumn)
            {
                flow.SetRecipientColumn(_columns.First(c => c.Name == "Correo").Id);
            }

            if (dataTriggerColumnId.HasValue)
            {
                flow.ConfigureDataTrigger(DataTriggerConfig.Create(dataTriggerColumnId.Value, DateTriggerMatchMode.AnniversaryDayMonth));
            }

            flow.CreatedAtUtc = createdAtUtc;

            if (activate)
            {
                flow.Activate();
            }

            Flows.Store[flow.Id] = flow;
            return flow;
        }
    }
}
