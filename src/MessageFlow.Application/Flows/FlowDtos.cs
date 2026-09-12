using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Filters;
using MessageFlow.Domain.Scheduling;

namespace MessageFlow.Application.Flows;

/// <summary>Configuración de CUÁNDO se ejecuta el Flow, expresada como la envía el cliente.</summary>
public sealed record ScheduleRequest(
    ExecutionType Type,
    string TimeZone,
    DateTimeOffset? RunAtUtc = null,
    string? RecurrenceTimeOfDay = null,
    int? RecurrenceIntervalDays = null);

public sealed record ScheduleDto(
    ExecutionType Type,
    string TimeZone,
    DateTimeOffset? RunAtUtc,
    string? RecurrenceTimeOfDay,
    int? RecurrenceIntervalDays);

/// <summary>Trigger de datos configurado (columna de fecha + modo de coincidencia).</summary>
public sealed record DataTriggerDto(Guid ColumnId, DateTriggerMatchMode MatchMode);

/// <summary>Condición simple de filtro (árbol dinámico §12-14).</summary>
public sealed record FilterConditionRequest(Guid ColumnId, FilterOperator Operator, string? Value);

/// <summary>Grupo de filtros AND/OR. Puede contener condiciones y subgrupos anidados.</summary>
public sealed record FilterGroupRequest(
    FilterCompositionOperator Composition,
    IReadOnlyList<FilterConditionRequest>? Conditions = null,
    IReadOnlyList<FilterGroupRequest>? Groups = null)
{
    public static readonly FilterGroupRequest? Empty = null;
}

public sealed record FlowSummaryDto(
    Guid Id,
    string Name,
    FlowStatus Status,
    Channel Channel,
    ExecutionType ExecutionType,
    DateTimeOffset CreatedAtUtc);

public sealed record FlowDetailDto(
    Guid Id,
    string Name,
    string? Description,
    FlowStatus Status,
    Channel Channel,
    Guid DatasetId,
    Guid? RecipientColumnId,
    string MessageTemplateText,
    FilterGroupRequest? RootFilter,
    ScheduleDto Schedule,
    DataTriggerDto? DataTrigger,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
