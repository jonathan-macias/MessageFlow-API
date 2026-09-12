using MessageFlow.Application.Abstractions.Execution;
using MessageFlow.Application.Executions;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Domain.Filters;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;
using MessageFlow.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageFlow.Tests;

/// <summary>
/// Tests del motor de ejecución (Fase 7) con dobles en memoria: filtro raíz,
/// render por fila, envío por canal, contadores, cierre y concurrencia del reclamo.
/// </summary>
public class FlowExecutionEngineTests
{
    private readonly Guid _datasetId = Guid.CreateVersion7();

    private readonly ColumnDefinition _nombre = new(Guid.CreateVersion7(), "Nombre", ColumnDataType.Text);
    private readonly ColumnDefinition _correo = new(Guid.CreateVersion7(), "Correo", ColumnDataType.Text);
    private readonly ColumnDefinition _ejecutivo = new(Guid.CreateVersion7(), "Ejecutivo", ColumnDataType.Text);

    private IReadOnlyList<ColumnDefinition> Columns => [_nombre, _correo, _ejecutivo];

    private static DatasetRow Row(Guid datasetId, long number, params (string Key, string? Value)[] cells)
        => new(datasetId, number, cells.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value)));

    private FilterCondition Cond(ColumnDefinition column, FilterOperator op, string value)
        => new(column.Id, op, value);

    [Fact]
    public async Task Aplica_filtro_renderiza_por_fila_y_envia_por_el_proveedor()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows =
            [
                Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com"), ("Ejecutivo", "PRINCIPAL")),
                Row(_datasetId, 2, ("Nombre", "Pedro"), ("Correo", "pedro@x.com"), ("Ejecutivo", "SECUNDARIO")),
                Row(_datasetId, 3, ("Nombre", "Ana"), ("Correo", "ana@x.com"), ("Ejecutivo", "PRINCIPAL")),
            ],
        };

        var filter = FilterGroup.Create(FilterCompositionOperator.And, [Cond(harness.EjecutivoRef!, FilterOperator.Equals, "PRINCIPAL")]);
        var (_, execution) = harness.Setup("Hola {{Nombre}}, tu ejecutivo es {{Ejecutivo}}", filter);
        var engine = harness.BuildEngine();

        var summary = await engine.ProcessPendingAsync(execution.Id);

        Assert.Equal(ExecutionStatus.Completed, summary.FinalStatus);
        Assert.Equal(3, summary.TotalRecords);
        Assert.Equal(2, summary.ProcessedRecords);
        Assert.Equal(2, summary.SuccessfulRecords);
        Assert.Equal(0, summary.FailedRecords);

        var sent = harness.ProviderAsStub().Sent;
        Assert.Equal(2, sent.Count);
        Assert.Contains(sent, m => m.Body == "Hola Juan, tu ejecutivo es PRINCIPAL");
        Assert.Contains(sent, m => m.Body == "Hola Ana, tu ejecutivo es PRINCIPAL");

        Assert.All(execution.Items, item => Assert.Equal(ExecutionItemStatus.Succeeded, item.Status));
    }

    [Fact]
    public async Task Fila_sin_destinatario_se_registra_como_skipped_y_no_se_envia()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows =
            [
                Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com")),
                Row(_datasetId, 2, ("Nombre", "Nadie"), ("Correo", null)),
            ],
        };

        var (_, execution) = harness.Setup("Hola {{Nombre}}");
        var engine = harness.BuildEngine();

        var summary = await engine.ProcessPendingAsync(execution.Id);

        Assert.Equal(ExecutionStatus.Completed, summary.FinalStatus);
        Assert.Equal(1, summary.SuccessfulRecords);
        Assert.Equal(1, summary.SkippedRecords);
        Assert.Single(harness.ProviderAsStub().Sent);

        var skipped = Assert.Single(execution.Items, i => i.Status == ExecutionItemStatus.Skipped);
        Assert.Contains("Correo", skipped.ErrorMessage);
    }

    [Fact]
    public async Task Fallo_del_proveedor_marca_la_fila_y_cierra_parcialmente()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows =
            [
                Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com")),
                Row(_datasetId, 2, ("Nombre", "Pedro"), ("Correo", "pedro@x.com")),
            ],
            FailingDestinations = ["pedro@x.com"],
        };

        var (_, execution) = harness.Setup("Hola {{Nombre}}");
        var engine = harness.BuildEngine();

        var summary = await engine.ProcessPendingAsync(execution.Id);

        Assert.Equal(ExecutionStatus.PartiallyCompleted, summary.FinalStatus);
        Assert.Equal(1, summary.SuccessfulRecords);
        Assert.Equal(1, summary.FailedRecords);

        var failed = Assert.Single(execution.Items, i => i.Status == ExecutionItemStatus.Failed);
        Assert.Contains("no está registrado", failed.ErrorMessage);
    }

    [Fact]
    public async Task Variable_sin_valor_se_sustituye_por_cadena_vacia_y_la_fila_se_envia()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows =
            [
                Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com")),
                Row(_datasetId, 2, ("Nombre", null), ("Correo", "sinvalor@x.com")),
                Row(_datasetId, 3, ("Nombre", "Ana"), ("Correo", "ana@x.com")),
            ],
        };

        var (_, execution) = harness.Setup("Hola {{Nombre}}!");
        var engine = harness.BuildEngine();

        var summary = await engine.ProcessPendingAsync(execution.Id);

        // La variable sin valor se vuelve "" y la corrida NO falla por ello.
        Assert.Equal(ExecutionStatus.Completed, summary.FinalStatus);
        Assert.Equal(3, summary.SuccessfulRecords);
        Assert.Equal(0, summary.FailedRecords);

        var sent = harness.ProviderAsStub().Sent;
        Assert.Contains(sent, m => m.Body == "Hola Juan!");
        Assert.Contains(sent, m => m.Body == "Hola !");       // fila con Nombre nulo
        Assert.DoesNotContain(sent, m => m.Destination == null);
    }

    [Fact]
    public async Task El_telefono_se_toma_de_la_PhoneColumn_del_dataset_sin_nombres_fijos()
    {
        // Dataset con columna telefónica configurada con un nombre arbitrario y el Flow
        // SIN columna destinataria: el motor debe usar exclusivamente PhoneColumn.
        var columns = new List<ColumnDefinition>
        {
            new(Guid.CreateVersion7(), "Nombre", ColumnDataType.Text),
            new(Guid.CreateVersion7(), "Numero Celular", ColumnDataType.Text),
        };

        var harness = new Harness(_datasetId, columns)
        {
            Rows =
            [
                Row(_datasetId, 1, ("Nombre", "Carlos"), ("Numero Celular", "3001234567")),
                Row(_datasetId, 2, ("Nombre", "Ana"), ("Numero Celular", "3109876543")),
            ],
            WithoutRecipientColumn = true,
        };

        harness.Datasets.PhoneColumnValue = "Numero Celular";

        var (_, execution) = harness.Setup("Hola {{Nombre}}, tu pedido va en camino.");
        var engine = harness.BuildEngine();

        var summary = await engine.ProcessPendingAsync(execution.Id);

        Assert.Equal(ExecutionStatus.Completed, summary.FinalStatus);

        var destinations = harness.ProviderAsStub().Sent.Select(m => m.Destination).OrderBy(d => d).ToList();
        Assert.Equal(["3001234567", "3109876543"], destinations);
    }

    [Fact]
    public async Task Una_PhoneColumn_que_ya_no_existe_en_el_dataset_falla_sistemicamente()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows = [Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com"))],
            WithoutRecipientColumn = true,
        };

        harness.Datasets.PhoneColumnValue = "Columna Eliminada";

        var (_, execution) = harness.Setup("Hola {{Nombre}}");
        var engine = harness.BuildEngine();

        await Assert.ThrowsAsync<DomainException>(() => engine.ProcessPendingAsync(execution.Id));

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Contains("ya no existe", execution.ErrorMessage);
    }

    [Fact]
    public async Task Sin_columna_destinataria_valida_falla_sistemicamente_y_propaga()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows = [Row(_datasetId, 1, ("Nombre", "Juan"))],
            WithoutRecipientColumn = true,
        };

        var (_, execution) = harness.Setup("Hola {{Nombre}}");
        var engine = harness.BuildEngine();

        await Assert.ThrowsAsync<DomainException>(() => engine.ProcessPendingAsync(execution.Id));

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Contains("columna destinataria", execution.ErrorMessage);
        Assert.Empty(harness.ProviderAsStub().Sent);
    }

    [Fact]
    public async Task Flow_no_activo_cancela_la_ejecucion_pendiente_sin_enviar()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows = [Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com"))],
        };

        var (flow, execution) = harness.Setup("Hola {{Nombre}}", activate: false);
        var engine = harness.BuildEngine();

        var summary = await engine.ProcessPendingAsync(execution.Id);

        Assert.Equal(FlowStatus.Draft, flow.Status);
        Assert.Equal(ExecutionStatus.Cancelled, summary.FinalStatus);
        Assert.Contains("no está activo", execution.ErrorMessage);
        Assert.Empty(harness.ProviderAsStub().Sent);
    }

    [Fact]
    public async Task Una_ejecucion_reclamada_por_otra_instancia_no_se_procesa_dos_veces()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows =
            [
                Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com")),
                Row(_datasetId, 2, ("Nombre", "Pedro"), ("Correo", "pedro@x.com")),
            ],
        };

        var (_, execution) = harness.Setup("Hola {{Nombre}}");
        var engine = harness.BuildEngine();

        // Simula que otra instancia ganó el reclamo antes de esta llamada.
        harness.Executions.BlockClaim = true;

        var summary = await engine.ProcessPendingAsync(execution.Id);

        Assert.Equal(ExecutionStatus.Pending, summary.FinalStatus);
        Assert.Empty(harness.ProviderAsStub().Sent);
        Assert.False(harness.Executions.BlockClaim);
    }

    [Fact]
    public async Task Reinvocar_una_ejecucion_ya_cerrada_es_un_no_op()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows = [Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com"))],
        };

        var (_, execution) = harness.Setup("Hola {{Nombre}}");
        var engine = harness.BuildEngine();

        var first = await engine.ProcessPendingAsync(execution.Id);
        var second = await engine.ProcessPendingAsync(execution.Id);

        Assert.Equal(ExecutionStatus.Completed, first.FinalStatus);
        Assert.Equal(ExecutionStatus.Completed, second.FinalStatus);
        Assert.Single(harness.ProviderAsStub().Sent);
        Assert.Equal(first.ProcessedRecords, second.ProcessedRecords);
    }

    [Fact]
    public async Task Cancelacion_a_mitad_de_camino_deja_la_ejecucion_cancelada()
    {
        var cts = new CancellationTokenSource();
        var provider = new CancellingAfterFirstSendProvider(cts);
        var harness = new Harness(_datasetId, Columns)
        {
            Rows =
            [
                Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com")),
                Row(_datasetId, 2, ("Nombre", "Pedro"), ("Correo", "pedro@x.com")),
                Row(_datasetId, 3, ("Nombre", "Ana"), ("Correo", "ana@x.com")),
            ],
            ProviderOverride = provider,
        };

        var (_, execution) = harness.Setup("Hola {{Nombre}}");
        var engine = harness.BuildEngine();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.ProcessPendingAsync(execution.Id, cts.Token));

        Assert.Equal(ExecutionStatus.Cancelled, execution.Status);
        Assert.Single(provider.Sent);
    }

    [Fact]
    public async Task Las_variables_por_id_de_columna_se_resuelven_con_el_valor_del_registro()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows = [Row(_datasetId, 1, ("Nombre", "Juan"), ("Correo", "juan@x.com"))],
        };

        // El frontend puede construir la plantilla con Ids de columna en lugar de nombres.
        var templateText = $"Hola {{{{{harness.IdOf("Nombre")}}}}}, tu correo es {{{{{harness.IdOf("Correo")}}}}}";
        var (_, execution) = harness.Setup(templateText);
        var engine = harness.BuildEngine();

        var summary = await engine.ProcessPendingAsync(execution.Id);

        Assert.Equal(ExecutionStatus.Completed, summary.FinalStatus);
        var sent = Assert.Single(harness.ProviderAsStub().Sent);
        Assert.Equal("Hola Juan, tu correo es juan@x.com", sent.Body);
    }

    [Fact]
    public async Task La_segmentacion_persiste_por_lotes_con_memoria_acotada()
    {
        var harness = new Harness(_datasetId, Columns)
        {
            Rows =
            [
                Row(_datasetId, 1, ("Nombre", "A"), ("Correo", "a@x.com")),
                Row(_datasetId, 2, ("Nombre", "B"), ("Correo", "b@x.com")),
                Row(_datasetId, 3, ("Nombre", "C"), ("Correo", "c@x.com")),
            ],
            Options = new FlowExecutionEngineOptions { SaveBatchSize = 1 },
        };

        var (_, execution) = harness.Setup("Hola {{Nombre}}");
        var engine = harness.BuildEngine();

        var summary = await engine.ProcessPendingAsync(execution.Id);

        // 3 segmentos + el cierre final de la ejecución.
        Assert.Equal(4, harness.UnitOfWork.SaveCount);
        Assert.Equal(ExecutionStatus.Completed, summary.FinalStatus);
        Assert.Equal(3, summary.SuccessfulRecords);
    }

    // --------------------------------------------------------------------------

    /// <summary>Ensambla repos en memoria + motor con las piezas de la prueba.</summary>
    private sealed class Harness(
        Guid datasetId,
        IReadOnlyList<ColumnDefinition> columns)
    {
        private readonly ColumnDefinition[] _columns = [.. columns];

        public required List<DatasetRow> Rows { get; init; }
        public HashSet<string>? FailingDestinations { get; init; }
        public IMessageProvider? ProviderOverride { get; init; }
        public bool WithoutRecipientColumn { get; init; }
        public FlowExecutionEngineOptions Options { get; init; } = new();

        private IMessageProvider? _provider;

        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FakeFlowRepository Flows { get; } = new();
        public FakeDatasetRepository Datasets { get; } = new();
        public FakeFlowExecutionRepository Executions { get; } = new();
        public IMessageProvider Provider => _provider ??= ProviderOverride ?? new StubMessageProvider(FailingDestinations);

        public ColumnDefinition? EjecutivoRef => _columns.FirstOrDefault(c => c.Name == "Ejecutivo");

        public Guid IdOf(string columnName) => _columns.First(c => c.Name == columnName).Id;

        public StubMessageProvider ProviderAsStub() => (StubMessageProvider)Provider;

        public (Flow Flow, FlowExecution Execution) Setup(string templateText, FilterGroup? rootFilter = null, bool activate = true)
        {
            Datasets.Load(_columns, Rows);

            var flow = Flow.CreateDraft(
                "Cumpleaños",
                description: null,
                datasetId,
                Channel.WhatsApp,
                MessageTemplate.Create(templateText),
                FlowSchedule.Immediate(TimeZoneId.Create("America/Bogota")),
                "owner-1");

            if (!WithoutRecipientColumn)
            {
                flow.SetRecipientColumn(_columns.First(c => c.Name == "Correo").Id);
            }

            if (rootFilter is not null)
            {
                flow.SetRootFilter(rootFilter);
            }

            if (activate)
            {
                flow.Activate();
            }

            Flows.Store[flow.Id] = flow;

            var execution = FlowExecution.Start(
                flow.Id,
                ExecutionType.Immediate,
                TriggerSource.Manual,
                $"manual:{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                Rows.Count);

            Executions.Store[execution.Id] = execution;

            return (flow, execution);
        }

        public FlowExecutionEngine BuildEngine() => new(
            Flows,
            Datasets,
            Executions,
            [Provider],
            UnitOfWork,
            TimeProvider.System,
            NullLogger<FlowExecutionEngine>.Instance,
            Options);
    }

    /// <summary>Cancela el token justo después del primer envío para probar el cierre por cancelación.</summary>
    private sealed class CancellingAfterFirstSendProvider(CancellationTokenSource cts) : IMessageProvider
    {
        public List<OutboundMessage> Sent { get; } = [];

        public Channel Channel => Channel.WhatsApp;

        public Task<DeliveryResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);

            if (Sent.Count == 1)
            {
                cts.Cancel();
            }

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(DeliveryResult.Success("stub-1"));
        }
    }
}
