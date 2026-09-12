using MessageFlow.Domain.Enums;

namespace MessageFlow.Domain.Exceptions;

public sealed class InvalidExecutionTransitionException(ExecutionStatus from, ExecutionStatus to)
    : DomainException($"Transición de estado no permitida para la ejecución: de '{from}' a '{to}'.")
{
    public ExecutionStatus From { get; } = from;

    public ExecutionStatus To { get; } = to;
}

public sealed class ClosedExecutionException(Guid executionId, ExecutionStatus status)
    : DomainException($"La ejecución '{executionId}' está cerrada (estado '{status}') y no admite más resultados.")
{
    public Guid ExecutionId { get; } = executionId;

    public ExecutionStatus Status { get; } = status;
}

public sealed class DuplicateExecutionItemException(Guid executionId, Guid datasetRowId)
    : DomainException($"La ejecución '{executionId}' ya registra un resultado para la fila '{datasetRowId}'.")
{
    public Guid ExecutionId { get; } = executionId;

    public Guid DatasetRowId { get; } = datasetRowId;
}
