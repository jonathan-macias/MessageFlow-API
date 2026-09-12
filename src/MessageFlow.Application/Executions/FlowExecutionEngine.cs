using MessageFlow.Application.Abstractions.Execution;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Domain.Filters;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;
using MessageFlow.Application.Flows;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Application.Executions;

/// <summary>
/// Motor de ejecución de Flows (Fase 7). Procesa una ejecución Pending en segmentos
/// acotados de filas: filtro raíz vía <see cref="FilterMatcher"/> (construido una sola
/// vez), render de plantilla por fila, envío por el <see cref="IMessageProvider"/> del
/// canal y registro de resultados por medio del agregado <see cref="FlowExecution"/>,
/// que mantiene contadores y estado siempre consistentes.
///
/// Concurrencia (§17): el reclamo Pending→Running es un UPDATE condicional atómico;
/// ante múltiples instancias solo una gana y las demás terminan sin reprocesar.
///
/// Memoria: cada segmento se persiste con SaveChanges y se suelta el ChangeTracker;
/// el agregado se recarga desde PostgreSQL para continuar con contadores frescos,
/// de modo que el consumo no crece con el tamaño del dataset.
/// Garantía de cierre: cualquier salida (éxito, cancelación o error sistémico) deja
/// la ejecución en estado terminal y devuelve un resumen; solo falla la propia
/// operación si ni siquiera es posible leer/persistir ese cierre.
/// </summary>
public sealed class FlowExecutionEngine(
    IFlowRepository flowRepository,
    IDatasetRepository datasetRepository,
    IFlowExecutionRepository executionRepository,
    IEnumerable<IMessageProvider> messageProviders,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<FlowExecutionEngine> logger,
    FlowExecutionEngineOptions options) : IFlowExecutionEngine
{
    public async Task<ExecutionRunSummary> ProcessPendingAsync(
        Guid flowExecutionId,
        CancellationToken cancellationToken = default)
    {
        if (options.SaveBatchSize is < FlowExecutionEngineOptions.MinSaveBatchSize or > FlowExecutionEngineOptions.MaxSaveBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.SaveBatchSize,
                $"El tamaño de lote debe estar entre {FlowExecutionEngineOptions.MinSaveBatchSize} y {FlowExecutionEngineOptions.MaxSaveBatchSize}.");
        }

        var execution = await executionRepository.FindByIdAsync(flowExecutionId, cancellationToken)
            ?? throw new NotFoundException("FlowExecution", flowExecutionId);

        if (execution.Status != ExecutionStatus.Pending)
        {
            // Otra instancia la reclamó o ya está cerrada: nunca reprocesar (§17).
            logger.LogDebug("La ejecución {FlowExecutionId} no está pendiente (estado {Status}); se omite.", flowExecutionId, execution.Status);
            return ToSummary(execution);
        }

        var flow = await flowRepository.FindByIdAsync(execution.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", execution.FlowId);

        if (!flow.IsExecutable())
        {
            return await CloseFromPendingAsync(execution, $"El Flow '{flow.Name}' no está activo (estado '{flow.Status}').");
        }

        if (!await executionRepository.TryBeginProcessingAsync(flowExecutionId, timeProvider.GetUtcNow(), cancellationToken))
        {
            var winner = await executionRepository.FindByIdAsync(flowExecutionId, cancellationToken)
                ?? throw new NotFoundException("FlowExecution", flowExecutionId);
            logger.LogDebug("La ejecución {FlowExecutionId} fue reclamada por otra instancia; se omite.", flowExecutionId);
            return ToSummary(winner);
        }

        logger.LogInformation(
            "Iniciando procesamiento de la ejecución {FlowExecutionId} del Flow '{FlowName}' ({TotalRecords} filas evaluadas).",
            flowExecutionId, flow.Name, execution.TotalRecords);

        // Recarga con el estado Running persistido por el reclamo: los métodos del
        // agregado operan sobre datos reales de PostgreSQL, no sobre memoria obsoleta.
        execution = await executionRepository.FindByIdAsync(flowExecutionId, cancellationToken)
            ?? throw new NotFoundException("FlowExecution", flowExecutionId);

        try
        {
            var pipeline = await BuildPipelineAsync(flow, cancellationToken);

            long cursorRowNumber = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await datasetRepository.GetRowsAfterAsync(
                    flow.DatasetId, cursorRowNumber, options.SaveBatchSize, cancellationToken);

                var processedAtUtc = timeProvider.GetUtcNow();

                foreach (var row in page)
                {
                    cursorRowNumber = row.RowNumber;
                    await ProcessRowAsync(execution, row, pipeline, processedAtUtc, cancellationToken);
                }

                var endReached = page.Count < options.SaveBatchSize;

                if (page.Count > 0)
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    unitOfWork.ClearTrackedEntities();

                    execution = await executionRepository.FindByIdAsync(flowExecutionId, cancellationToken)
                        ?? throw new NotFoundException("FlowExecution", flowExecutionId);
                }

                if (endReached)
                {
                    break;
                }
            }

            execution.Complete(timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Ejecución {FlowExecutionId} finalizada con estado {FinalStatus}: {Processed} procesados, {Successful} exitosos, {Failed} fallidos.",
                flowExecutionId, execution.Status, execution.ProcessedRecords, execution.SuccessfulRecords, execution.FailedRecords);
            return ToSummary(execution);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("El procesamiento de la ejecución {FlowExecutionId} fue cancelado.", flowExecutionId);
            await CloseBestEffortAsync(flowExecutionId, e => e.Cancel(timeProvider.GetUtcNow(), "Procesamiento cancelado antes de finalizar."));
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fallo sistémico procesando la ejecución {FlowExecutionId}.", flowExecutionId);
            await CloseBestEffortAsync(flowExecutionId, e => e.Fail(timeProvider.GetUtcNow(), ex.Message));

            // Estado terminal garantizado; se propaga para que el orquestador registre el incidente.
            throw;
        }
    }

    private async Task<ExecutionRunSummary> CloseFromPendingAsync(FlowExecution execution, string reason)
    {
        execution.Cancel(timeProvider.GetUtcNow(), reason);
        await unitOfWork.SaveChangesAsync();
        logger.LogInformation("La ejecución {FlowExecutionId} quedó cancelada sin iniciar: {Reason}", execution.Id, reason);
        return ToSummary(execution);
    }

    /// <summary>
    /// Componentes invariantes de la corrida, validados una sola vez: catálogo+filtro
    /// (FilterMatcher), condición del data trigger (si el Flow es DataTriggered),
    /// plantilla, proveedor del canal y columna destinataria.
    /// Los problemas estructurales abortan la corrida como fallo sistémico.
    /// </summary>
    private async Task<RowPipeline> BuildPipelineAsync(Flow flow, CancellationToken cancellationToken)
    {
        var columns = await datasetRepository.GetColumnDefinitionsAsync(flow.DatasetId, cancellationToken);

        // Destinatario (§7): SIEMPRE la columna telefónica configurada en el Dataset
        // (nombre configurable, jamás fijo). Compatibilidad: si el dataset aún no la
        // tiene configurada, se respeta la columna destinataria definida en el Flow.
        var phoneColumnName = await datasetRepository.GetPhoneColumnAsync(flow.DatasetId, cancellationToken);

        ColumnDefinition? recipientColumn;
        if (!string.IsNullOrWhiteSpace(phoneColumnName))
        {
            recipientColumn = columns.FirstOrDefault(c =>
                string.Equals(c.Name, phoneColumnName.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainException(
                    $"La columna telefónica '{phoneColumnName}' configurada en el dataset ya no existe entre sus columnas.");
        }
        else if (flow.RecipientColumnId.HasValue)
        {
            recipientColumn = columns.FirstOrDefault(c => c.Id == flow.RecipientColumnId.Value);
        }
        else
        {
            throw new DomainException(
                "El dataset no tiene una columna telefónica configurada y el Flow no define una columna destinataria.");
        }

        if (recipientColumn is null)
        {
            throw new DomainException(
                "La columna destinataria configurada en el Flow ya no existe en el dataset.");
        }

        var provider = messageProviders.FirstOrDefault(p => p.Channel == flow.Channel)
            ?? throw new DomainException($"No hay un proveedor de mensajes registrado para el canal '{flow.Channel}'.");

        DataTriggerEvaluation? dataTrigger = null;
        if (flow.DataTrigger is not null)
        {
            var triggerColumn = columns.FirstOrDefault(c => c.Id == flow.DataTrigger.ColumnId)
                ?? throw new DomainException(
                    "La columna del trigger de datos no existe en el dataset del Flow.");

            var localToday = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), flow.Schedule.Zone.ToTimeZoneInfo()).DateTime);

            dataTrigger = new DataTriggerEvaluation(
                DataTriggerMatcher.Create(flow.DataTrigger),
                triggerColumn.Name,
                localToday);
        }

        return new RowPipeline(
            new FilterMatcher(flow.RootFilter, columns),
            // Las variables pueden venir por Nombre o por Id de columna; se normalizan
            // a nombre UNA VEZ por corrida y el render por fila no tiene sobrecosto.
            TemplateColumnAliases.Apply(flow.GetMessageTemplate(), columns),
            provider,
            recipientColumn.Name,
            dataTrigger);
    }

    private static async Task ProcessRowAsync(
        FlowExecution execution,
        DatasetRow row,
        RowPipeline pipeline,
        DateTimeOffset executedAtUtc,
        CancellationToken cancellationToken)
    {
        // Las filas que no pasan el filtro no generan ítem: TotalRecords cuenta filas
        // evaluadas y ProcessedRecords solo filas con resultado (enviadas/falladas/saltadas).
        // El trigger de datos (DataTriggered) es una condición adicional al filtro raíz.
        if (!pipeline.Matcher.Matches(row.Values))
        {
            return;
        }

        if (pipeline.DataTrigger is { } trigger)
        {
            row.Values.TryGetValue(trigger.ColumnName, out var triggerCell);

            if (!trigger.Matcher.Matches(triggerCell, trigger.ReferenceDate))
            {
                return;
            }
        }

        if (!row.Values.TryGetValue(pipeline.RecipientColumnName, out var destination)
            || string.IsNullOrWhiteSpace(destination))
        {
            execution.RegisterSkipped(row.Id, $"Sin valor de destinatario en la columna '{pipeline.RecipientColumnName}'.", executedAtUtc);
            return;
        }

        try
        {
            // Variables sin valor en el registro se sustituyen por cadena vacía y la
            // fila continúa (§2 del requerimiento); solo falla si el cuerpo queda vacío.
            var body = pipeline.Template.Render(row.Values, MissingVariablePolicy.Empty);
            var message = OutboundMessage.Create(new Recipient(row.Id, destination), body, pipeline.Provider.Channel);
            var delivery = await pipeline.Provider.SendAsync(message, cancellationToken);

            if (delivery.IsSuccess)
            {
                execution.RegisterSuccess(row.Id, executedAtUtc);
            }
            else
            {
                execution.RegisterFailure(row.Id, delivery.Error ?? "El proveedor devolvió un error sin detalle.", executedAtUtc);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un registro problemático no detiene la corrida masiva.
            execution.RegisterFailure(row.Id, ex.Message, executedAtUtc);
        }
    }

    /// <summary>Cierra la ejecución en un estado terminal tolerando fallos secundarios (p. ej. BD caída).</summary>
    private async Task CloseBestEffortAsync(Guid flowExecutionId, Action<FlowExecution> close)
    {
        try
        {
            var execution = await executionRepository.FindByIdAsync(flowExecutionId)
                ?? throw new NotFoundException("FlowExecution", flowExecutionId);

            close(execution);
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception closeEx)
        {
            logger.LogError(closeEx, "No se pudo cerrar la ejecución {FlowExecutionId}; requerirá intervención manual.", flowExecutionId);
        }
    }

    private static ExecutionRunSummary ToSummary(FlowExecution execution)
    {
        var skipped = Math.Max(0, execution.ProcessedRecords - execution.SuccessfulRecords - execution.FailedRecords);

        return new ExecutionRunSummary(
            execution.Id,
            execution.Status,
            execution.TotalRecords,
            execution.ProcessedRecords,
            execution.SuccessfulRecords,
            execution.FailedRecords,
            skipped,
            execution.ErrorMessage);
    }

    /// <summary>Servicios resueltos una vez por corrida e inyectados al procesamiento por fila.</summary>
    private sealed record RowPipeline(
        FilterMatcher Matcher,
        MessageTemplate Template,
        IMessageProvider Provider,
        string RecipientColumnName,
        DataTriggerEvaluation? DataTrigger);

    /// <summary>Condición del data trigger resuelta: matcher, columna y fecha de referencia (hoy local).</summary>
    private sealed record DataTriggerEvaluation(
        DataTriggerMatcher Matcher,
        string ColumnName,
        DateOnly ReferenceDate);
}

/// <summary>Extensión local para consultar ejecutabilidad sin depender de excepciones en el motor.</summary>
internal static class FlowExecutionEngineFlowExtensions
{
    public static bool IsExecutable(this Flow flow) => flow.Status == FlowStatus.Active;
}
