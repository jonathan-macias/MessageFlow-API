using System.Diagnostics.CodeAnalysis;
using MessageFlow.Domain.Common;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Domain.Filters;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;

namespace MessageFlow.Domain.Flows;

/// <summary>
/// Definición de la automatización: QUÉ enviar (plantilla), A QUIÉN (dataset + columna destinataria),
/// CUÁNDO (schedule/data trigger) y BAJO QUÉ CONDICIONES (filtro raíz).
/// El CÓMO se envía pertenece a IMessageProvider (Infrastructure).
/// </summary>
public sealed class Flow : AuditableEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    private static readonly IReadOnlyDictionary<FlowStatus, IReadOnlySet<FlowStatus>> AllowedTransitions =
        new Dictionary<FlowStatus, IReadOnlySet<FlowStatus>>
        {
            [FlowStatus.Draft] = new HashSet<FlowStatus> { FlowStatus.Active, FlowStatus.Archived },
            [FlowStatus.Active] = new HashSet<FlowStatus> { FlowStatus.Paused, FlowStatus.Completed, FlowStatus.Archived },
            [FlowStatus.Paused] = new HashSet<FlowStatus> { FlowStatus.Active, FlowStatus.Archived },
            [FlowStatus.Completed] = new HashSet<FlowStatus> { FlowStatus.Archived },
            [FlowStatus.Archived] = new HashSet<FlowStatus>(),
        };

    public string Name { get; private set; } = default!;

    public string? Description { get; private set; }

    public FlowStatus Status { get; private set; }

    public Channel Channel { get; private set; }

    /// <summary>UserId del propietario del Flow (ownership, §34).</summary>
    public string OwnerId { get; private set; } = default!;

    public Guid DatasetId { get; private set; }

    /// <summary>Columna del dataset que contiene el identificador del destinatario (p. ej. teléfono).</summary>
    public Guid? RecipientColumnId { get; private set; }

    /// <summary>Texto de la plantilla. Se manipula como VO <see cref="MessageTemplate"/> en los handlers.</summary>
    public string MessageTemplateText { get; private set; } = default!;

    /// <summary>Filtro raíz opcional sobre las filas del dataset. Serializado como jsonb.</summary>
    public FilterGroup? RootFilter { get; private set; }

    public FlowSchedule Schedule { get; private set; } = default!;

    public DataTriggerConfig? DataTrigger { get; private set; }

    private Flow()
    {
    }

    public static Flow CreateDraft(
        string? name,
        string? description,
        Guid datasetId,
        Channel channel,
        MessageTemplate template,
        FlowSchedule schedule,
        string ownerId)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        ValidateName(name);

        if (datasetId == Guid.Empty)
        {
            throw new DomainException("El Flow debe estar asociado a un dataset.");
        }

        return new Flow
        {
            OwnerId = ownerId,
            Name = name.Trim(),
            Description = TruncateOrNull(description, DescriptionMaxLength),
            Status = FlowStatus.Draft,
            Channel = channel,
            DatasetId = datasetId,
            MessageTemplateText = template.Text,
            Schedule = schedule,
        };
    }

    public MessageTemplate GetMessageTemplate() => MessageTemplate.Create(MessageTemplateText);

    public void UpdateDetails(string? name, string? description)
    {
        EnsureEditable();
        ValidateName(name);
        Name = name.Trim();
        Description = TruncateOrNull(description, DescriptionMaxLength);
    }

    public void UpdateSchedule(FlowSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        EnsureEditable();
        Schedule = schedule;
    }

    public void UpdateMessage(MessageTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        EnsureEditable();
        MessageTemplateText = template.Text;
    }

    public void SetRootFilter(FilterGroup? filter)
    {
        EnsureEditable();
        RootFilter = filter;
    }

    public void SetRecipientColumn(Guid? recipientColumnId)
    {
        EnsureEditable();
        RecipientColumnId = recipientColumnId;
    }

    public void ConfigureDataTrigger(DataTriggerConfig? dataTrigger)
    {
        EnsureEditable();

        if (dataTrigger is not null && Schedule.Type != ExecutionType.DataTriggered)
        {
            throw new DomainException(
                $"El trigger de datos solo aplica a Flows de tipo '{ExecutionType.DataTriggered}'.");
        }

        DataTrigger = dataTrigger;
    }

    public void Activate()
    {
        EnsureScheduleConsistent();
        TransitionTo(FlowStatus.Active);
    }

    public void Pause() => TransitionTo(FlowStatus.Paused);

    public void MarkCompleted() => TransitionTo(FlowStatus.Completed);

    public void Archive() => TransitionTo(FlowStatus.Archived);

    /// <summary>Regla previa a cualquier ejecución: solo Flows activos se ejecutan (§18).</summary>
    public void EnsureExecutable()
    {
        if (Status != FlowStatus.Active)
        {
            throw new FlowNotExecutableException(Name, Status);
        }
    }

    /// <summary>Solo se permite eliminar Flows en borrador o archivados.</summary>
    public void EnsureDeletable()
    {
        if (Status is not (FlowStatus.Draft or FlowStatus.Archived))
        {
            throw new FlowNotModifiableException(Name, Status);
        }
    }

    /// <summary>
    /// Valida que el filtro raíz sea coherente con el catálogo de columnas del dataset.
    /// Se invoca desde Application al guardar filtros o antes de ejecutar.
    /// </summary>
    public void ValidateRootFilterAgainst(IReadOnlyList<ColumnDefinition> columns)
        => Filters.FilterTreeValidator.Validate(RootFilter, columns);

    private void EnsureEditable()
    {
        if (Status is FlowStatus.Archived or FlowStatus.Completed)
        {
            throw new FlowNotModifiableException(Name, Status);
        }
    }

    private void EnsureScheduleConsistent()
    {
        var invalid = Schedule.Type switch
        {
            ExecutionType.OneTime => Schedule.RunAtUtc is null,
            ExecutionType.Recurring => Schedule.Recurrence is null,
            ExecutionType.DataTriggered => DataTrigger is null,
            ExecutionType.Immediate => false,
            _ => true,
        };

        if (invalid)
        {
            throw new DomainException(
                $"La configuración del Flow no es consistente con el tipo de ejecución '{Schedule.Type}'. " +
                "Complete los datos de programación antes de activarlo.");
        }
    }

    private void TransitionTo(FlowStatus target)
    {
        if (!AllowedTransitions.TryGetValue(Status, out var allowed) || !allowed.Contains(target))
        {
            throw new InvalidFlowTransitionException(Status, target);
        }

        Status = target;
    }

    private static void ValidateName([NotNull] string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("El nombre del Flow es obligatorio.");
        }

        if (name.Trim().Length > NameMaxLength)
        {
            throw new DomainException($"El nombre del Flow no puede superar {NameMaxLength} caracteres.");
        }
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
