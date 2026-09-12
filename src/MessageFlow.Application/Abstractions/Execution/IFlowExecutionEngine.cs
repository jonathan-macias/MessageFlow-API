using MessageFlow.Domain.Enums;

namespace MessageFlow.Application.Abstractions.Execution;

/// <summary>
/// Motor de ejecución de Flows (Fase 7): procesa una FlowExecution en estado Pending.
/// Aplica el filtro raíz con FilterMatcher, renderiza la plantilla por fila, envía vía
/// IMessageProvider, registra FlowExecutionItem y contadores y cierra la ejecución.
/// La invocación corresponde al disparo manual (Send Now) o al scheduler (Fase 8).
/// </summary>
public interface IFlowExecutionEngine
{
    /// <summary>
    /// Reclama (atímicamente) y procesa una ejecución pendiente. Si otra instancia ya
    /// la tomó o está cerrada, no hace nada y devuelve el resumen actual sin reprocesar.
    /// </summary>
    Task<ExecutionRunSummary> ProcessPendingAsync(Guid flowExecutionId, CancellationToken cancellationToken = default);
}

/// <summary>Resultado agregado de una corrida del motor.</summary>
public sealed record ExecutionRunSummary(
    Guid FlowExecutionId,
    ExecutionStatus FinalStatus,
    long TotalRecords,
    long ProcessedRecords,
    long SuccessfulRecords,
    long FailedRecords,
    long SkippedRecords,
    string? ErrorMessage);

/// <summary>Ajustes del motor. Registrado como singleton; configurable en Fase 9.</summary>
public sealed class FlowExecutionEngineOptions
{
    public const int MinSaveBatchSize = 1;
    public const int MaxSaveBatchSize = 10_000;

    /// <summary>Filas procesadas por transacción de persistencia (memoria acotada).</summary>
    public int SaveBatchSize { get; init; } = 500;
}
