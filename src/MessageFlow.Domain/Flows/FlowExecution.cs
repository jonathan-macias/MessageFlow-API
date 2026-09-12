using MessageFlow.Domain.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Flows;

/// <summary>
/// Agregado que representa cada ejecución de un Flow (§15). Registra contadores
/// agregados (consultables sin cargar ítems), la clave de idempotencia única
/// (constraint en Infrastructure) y el resultado por registro vía <see cref="FlowExecutionItem"/>.
/// </summary>
public sealed class FlowExecution : AuditableEntity
{
    public const int IdempotencyKeyMaxLength = 200;
    public const int ErrorMessageMaxLength = 2000;

    private readonly List<FlowExecutionItem> _items = [];

    public Guid FlowId { get; private set; }

    /// <summary>Tipo congelado al crear la ejecución (snapshot, no referencia viva al Flow).</summary>
    public ExecutionType ExecutionType { get; private set; }

    public TriggerSource TriggerSource { get; private set; }

    /// <summary>Evita que una misma operación dispare dos ejecuciones. Único en base de datos.</summary>
    public string IdempotencyKey { get; private set; } = default!;

    /// <summary>
    /// Instante de la ocurrencia programada que originó esta ejecución (solo
    /// TriggerSource.Scheduled). Sirve de ancla para calcular la siguiente ocurrencia
    /// y evita reprogramar una misma ocurrencia aunque el procesamiento se atrase.
    /// Null en ejecuciones manuales o por trigger de datos.
    /// </summary>
    public DateTimeOffset? ScheduledForUtc { get; private set; }

    public ExecutionStatus Status { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public long TotalRecords { get; private set; }

    public long ProcessedRecords { get; private set; }

    public long SuccessfulRecords { get; private set; }

    public long FailedRecords { get; private set; }

    public string? ErrorMessage { get; private set; }

    public IReadOnlyCollection<FlowExecutionItem> Items => _items.AsReadOnly();

    private FlowExecution()
    {
    }

    /// <summary>Crea la ejecución en estado Pending (aún no procesa registros).</summary>
    public static FlowExecution Start(
        Guid flowId,
        ExecutionType executionType,
        TriggerSource triggerSource,
        string idempotencyKey,
        DateTimeOffset startedAtUtc,
        long totalRecords,
        DateTimeOffset? scheduledForUtc = null)
    {
        if (flowId == Guid.Empty)
        {
            throw new DomainException("La ejecución debe pertenecer a un Flow.");
        }

        if (!Enum.IsDefined(executionType) || !Enum.IsDefined(triggerSource))
        {
            throw new DomainException("Tipo de ejecución u origen de disparo desconocido.");
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("La clave de idempotencia es obligatoria.");
        }

        var key = idempotencyKey.Trim();

        if (key.Length > IdempotencyKeyMaxLength)
        {
            throw new DomainException(
                $"La clave de idempotencia no puede superar {IdempotencyKeyMaxLength} caracteres.");
        }

        if (totalRecords < 0)
        {
            throw new DomainException("El total de registros no puede ser negativo.");
        }

        return new FlowExecution
        {
            FlowId = flowId,
            ExecutionType = executionType,
            TriggerSource = triggerSource,
            IdempotencyKey = key,
            ScheduledForUtc = scheduledForUtc?.RequireUtc(),
            Status = ExecutionStatus.Pending,
            StartedAtUtc = startedAtUtc.RequireUtc(),
            TotalRecords = totalRecords,
        };
    }

    /// <summary>Marca el inicio del procesamiento efectivo.</summary>
    public void Begin(DateTimeOffset atUtc)
    {
        if (Status != ExecutionStatus.Pending)
        {
            throw new InvalidExecutionTransitionException(Status, ExecutionStatus.Running);
        }

        Status = ExecutionStatus.Running;
        StartedAtUtc = atUtc.RequireUtc();
    }

    public FlowExecutionItem RegisterSuccess(Guid datasetRowId, DateTimeOffset executedAtUtc)
        => Register(datasetRowId, ExecutionItemStatus.Succeeded, errorMessage: null, executedAtUtc);

    public FlowExecutionItem RegisterFailure(Guid datasetRowId, string? errorMessage, DateTimeOffset executedAtUtc)
        => Register(datasetRowId, ExecutionItemStatus.Failed, errorMessage, executedAtUtc);

    public FlowExecutionItem RegisterSkipped(Guid datasetRowId, string? reason, DateTimeOffset atUtc)
        => Register(datasetRowId, ExecutionItemStatus.Skipped, reason, atUtc);

    /// <summary>
    /// Cierra la ejecución calculando el estado final según los contadores:
    /// Completed (cero fallos), PartiallyCompleted (éxitos y fallos mezclados) o Failed (todo falló).
    /// </summary>
    public void Complete(DateTimeOffset completedAtUtc)
    {
        if (Status != ExecutionStatus.Running)
        {
            throw new InvalidExecutionTransitionException(Status, ExecutionStatus.Completed);
        }

        CompletedAtUtc = completedAtUtc.RequireUtc();
        ErrorMessage = null;
        Status = FailedRecords switch
        {
            0 => ExecutionStatus.Completed,
            _ when SuccessfulRecords > 0 => ExecutionStatus.PartiallyCompleted,
            _ => ExecutionStatus.Failed,
        };
    }

    /// <summary>
    /// Cierre sistémico por un error de infraestructura o de configuración que impide
    /// procesar la ejecución (p. ej. proveedor de canal ausente, columna destinataria
    /// inválida). A diferencia del Failed derivado de contadores, aquí el fallo no
    /// pertenece a un registro concreto sino a la ejecución completa.
    /// </summary>
    public void Fail(DateTimeOffset failedAtUtc, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new DomainException("El mensaje de error es obligatorio al fallar una ejecución.");
        }

        if (Status is not (ExecutionStatus.Pending or ExecutionStatus.Running))
        {
            throw new InvalidExecutionTransitionException(Status, ExecutionStatus.Failed);
        }

        CompletedAtUtc = failedAtUtc.RequireUtc();
        ErrorMessage = TruncateOrNull(errorMessage, ErrorMessageMaxLength);
        Status = ExecutionStatus.Failed;
    }

    /// <summary>Cancela una ejecución pendiente o en curso (apagado ordenado o solicitud del usuario).</summary>
    public void Cancel(DateTimeOffset cancelledAtUtc, string? reason)
    {
        if (Status is not (ExecutionStatus.Pending or ExecutionStatus.Running))
        {
            throw new InvalidExecutionTransitionException(Status, ExecutionStatus.Cancelled);
        }

        CompletedAtUtc = cancelledAtUtc.RequireUtc();
        ErrorMessage = TruncateOrNull(reason, ErrorMessageMaxLength);
        Status = ExecutionStatus.Cancelled;
    }

    private bool IsClosed => Status
        is ExecutionStatus.Completed
        or ExecutionStatus.PartiallyCompleted
        or ExecutionStatus.Failed
        or ExecutionStatus.Cancelled;

    private FlowExecutionItem Register(
        Guid datasetRowId,
        ExecutionItemStatus status,
        string? errorMessage,
        DateTimeOffset executedAtUtc)
    {
        var utc = executedAtUtc.RequireUtc();

        if (IsClosed)
        {
            throw new ClosedExecutionException(Id, Status);
        }

        if (_items.Any(i => i.DatasetRowId == datasetRowId))
        {
            throw new DuplicateExecutionItemException(Id, datasetRowId);
        }

        var item = FlowExecutionItem.Create(Id, datasetRowId, status, errorMessage, utc);
        _items.Add(item);
        ProcessedRecords++;

        switch (status)
        {
            case ExecutionItemStatus.Succeeded:
                SuccessfulRecords++;
                break;
            case ExecutionItemStatus.Failed:
                FailedRecords++;
                break;
        }

        return item;
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
