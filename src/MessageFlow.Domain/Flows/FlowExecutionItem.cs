using MessageFlow.Domain.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Flows;

/// <summary>
/// Resultado por registro de una ejecución: qué ocurrió con cada destinatario (§15).
/// Se crea exclusivamente a través de <see cref="FlowExecution"/> para mantener
/// contadores y estado del agregado consistentes.
/// </summary>
public sealed class FlowExecutionItem : Entity
{
    public const int ErrorMessageMaxLength = 1000;

    public Guid FlowExecutionId { get; private set; }

    public Guid DatasetRowId { get; private set; }

    public ExecutionItemStatus Status { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Instante UTC en que se resolvió el ítem.</summary>
    public DateTimeOffset ExecutedAtUtc { get; private set; }

    private FlowExecutionItem()
    {
    }

    internal static FlowExecutionItem Create(
        Guid flowExecutionId,
        Guid datasetRowId,
        ExecutionItemStatus status,
        string? errorMessage,
        DateTimeOffset executedAtUtc)
    {
        if (flowExecutionId == Guid.Empty)
        {
            throw new DomainException("The item must belong to an execution.");
        }

        if (datasetRowId == Guid.Empty)
        {
            throw new DomainException("The item must reference a dataset row.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new DomainException($"Unknown item status: '{status}'.");
        }

        return new FlowExecutionItem
        {
            FlowExecutionId = flowExecutionId,
            DatasetRowId = datasetRowId,
            Status = status,
            ErrorMessage = TruncateOrNull(errorMessage, ErrorMessageMaxLength),
            ExecutedAtUtc = executedAtUtc.RequireUtc(),
        };
    }

    private static string? TruncateOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
