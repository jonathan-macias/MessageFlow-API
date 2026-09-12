using MessageFlow.Application.Abstractions.Execution;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Scheduling;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Application.Executions;

/// <summary>
/// Productor/consumidor del scheduler (Fase 8, §16-§17).
///
/// PRODUCTOR: recorre los Flows activos y encola ejecuciones pendientes.
///  - OneTime/Recurring: camina las ocurrencias vencidas desde el ancla
///    (MAX(ScheduledForUtc) de ejecuciones programadas previas, o la creación del Flow)
///    creando UNA ejecución por ocurrencia con clave determinista
///    sched:{flowId}:{ocurrencia}. La clave + el constraint único hacen imposible
///    duplicar una ocurrencia aunque varias instancias compitan; el ancla avanza por
///    ocurrencia programada (no por procesamiento), así un incidente en una ejecución
///    nunca bloquea las siguientes.
///  - DataTriggered: UNA ejecución por fecha local del Flow (clave data:{flowId}:{fecha}).
///    El filtrado de filas por aniversario lo hace el motor al consumir.
///  - Immediate: jamás se programa aquí (solo "Send Now" manual).
///
/// CONSUMIDOR: toma un lote de ejecuciones Pending —incluye las manuales— y las
/// entrega al motor, cuyo reclamo atómico garantiza que ninguna se procese dos veces.
/// </summary>
public sealed class ScheduledExecutionProcessor(
    IFlowRepository flowRepository,
    IDatasetRepository datasetRepository,
    IFlowExecutionRepository executionRepository,
    IUnitOfWork unitOfWork,
    IFlowExecutionEngine engine,
    TimeProvider timeProvider,
    ILogger<ScheduledExecutionProcessor> logger,
    FlowSchedulingOptions options) : IScheduledExecutionProcessor
{
    public async Task<int> EnqueueDueExecutionsAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var flows = await flowRepository.ListActiveAsync(cancellationToken);

        var created = 0;

        foreach (var flow in flows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            created += flow.Schedule.Type switch
            {
                ExecutionType.OneTime or ExecutionType.Recurring => await EnqueueClockBasedAsync(flow, now, cancellationToken),
                ExecutionType.DataTriggered => await EnqueueDataTriggeredAsync(flow, now, cancellationToken),
                _ => 0,
            };
        }

        if (created > 0)
        {
            logger.LogInformation("El scheduler encoló {Created} ejecución(es) programadas.", created);
        }

        return created;
    }

    public async Task<int> ProcessQueuedExecutionsAsync(CancellationToken cancellationToken = default)
    {
        var pendingIds = await executionRepository.ListPendingIdsAsync(options.ConsumerBatchSize, cancellationToken);

        var processed = 0;

        foreach (var id in pendingIds)
        {
            try
            {
                await engine.ProcessPendingAsync(id, cancellationToken);
                processed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Apagado ordenado: el motor dejó la ejecución Cancelada; terminar el ciclo.
                throw;
            }
            catch (Exception ex)
            {
                // El motor ya cerró la ejecución como Failed y registró el detalle;
                // aislamos el fallo para no detener el resto del lote.
                logger.LogError(ex, "La ejecución {FlowExecutionId} terminó con fallo sistémico; continúa el lote.", id);
            }
        }

        return processed;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await EnqueueDueExecutionsAsync(cancellationToken);
        await ProcessQueuedExecutionsAsync(cancellationToken);
    }

    private async Task<int> EnqueueClockBasedAsync(Flow flow, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var anchor = await executionRepository.GetLatestScheduledOccurrenceUtcAsync(flow.Id, cancellationToken)
            ?? flow.CreatedAtUtc;

        if (!flow.Schedule.NextRunAfter(anchor).HasValue)
        {
            return 0;
        }

        var created = 0;

        for (var occurrence = 0; occurrence < options.MaxCatchUpOccurrences; occurrence++)
        {
            var due = flow.Schedule.NextRunAfter(anchor);

            // Sin más ocurrencias o la próxima aún no vence.
            if (due is null || due.Value > now)
            {
                break;
            }

            var key = $"sched:{flow.Id:N}:{due.Value:O}";

            if (!await executionRepository.ExistsByIdempotencyKeyAsync(key, cancellationToken))
            {
                await EnqueueAsync(flow, TriggerSource.Scheduled, key, now, due.Value, cancellationToken);
                created++;
            }

            anchor = due.Value;
        }

        return created;
    }

    private async Task<int> EnqueueDataTriggeredAsync(Flow flow, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var localToday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(now, flow.Schedule.Zone.ToTimeZoneInfo()).DateTime);

        var key = $"data:{flow.Id:N}:{localToday:yyyy-MM-dd}";

        if (await executionRepository.ExistsByIdempotencyKeyAsync(key, cancellationToken))
        {
            return 0;
        }

        await EnqueueAsync(flow, TriggerSource.DataMatch, key, now, scheduledForUtc: null, cancellationToken);
        return 1;
    }

    /// <summary>
    /// Persiste la ejecución pendiente. Si otra instancia ganara la carrera pese a la
    /// verificación previa, el constraint único rechaza el INSERT: el conflicto sube como
    /// excepción, se registra y el próximo ciclo continúa desde el ancla ya avanzada
    /// (MAX(ScheduledForUtc) ve la ejecución de la instancia ganadora), sin duplicados.
    /// </summary>
    private async Task EnqueueAsync(
        Flow flow,
        TriggerSource triggerSource,
        string idempotencyKey,
        DateTimeOffset now,
        DateTimeOffset? scheduledForUtc,
        CancellationToken cancellationToken)
    {
        var totalRecords = await datasetRepository.CountRowsAsync(flow.DatasetId, cancellationToken);

        var execution = FlowExecution.Start(
            flow.Id,
            flow.Schedule.Type,
            triggerSource,
            idempotencyKey,
            now,
            totalRecords,
            scheduledForUtc);

        await executionRepository.AddAsync(execution, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogExecutionEnqueued(execution.Id, flow.Name, triggerSource, scheduledForUtc);
    }
}

internal static class ScheduledExecutionProcessorLogs
{
    public static void LogExecutionEnqueued(
        this ILogger logger,
        Guid executionId,
        string flowName,
        TriggerSource source,
        DateTimeOffset? scheduledForUtc)
        => logger.LogInformation(
            "Encolada la ejecución {FlowExecutionId} del Flow '{FlowName}' (origen {TriggerSource}, ocurrencia {ScheduledForUtc}).",
            executionId, flowName, source, scheduledForUtc);
}
