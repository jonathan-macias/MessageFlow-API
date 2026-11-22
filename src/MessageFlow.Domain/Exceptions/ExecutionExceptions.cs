using MessageFlow.Domain.Enums;

namespace MessageFlow.Domain.Exceptions;

public sealed class InvalidExecutionTransitionException(ExecutionStatus from, ExecutionStatus to)
    : DomainException($"Execution status transition not allowed: from '{from}' to '{to}'.")
{
    public ExecutionStatus From { get; } = from;

    public ExecutionStatus To { get; } = to;
}

public sealed class ClosedExecutionException(Guid executionId, ExecutionStatus status)
    : DomainException($"Execution '{executionId}' is closed (status '{status}') and accepts no further results.")
{
    public Guid ExecutionId { get; } = executionId;

    public ExecutionStatus Status { get; } = status;
}

public sealed class DuplicateExecutionItemException(Guid executionId, Guid datasetRowId)
    : DomainException($"Execution '{executionId}' already has a result for row '{datasetRowId}'.")
{
    public Guid ExecutionId { get; } = executionId;

    public Guid DatasetRowId { get; } = datasetRowId;
}
