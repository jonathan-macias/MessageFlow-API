using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Flows;

namespace MessageFlow.Application.Executions.Queries;

public sealed record FlowExecutionSummaryDto(
    Guid Id,
    Guid FlowId,
    ExecutionStatus Status,
    TriggerSource TriggerSource,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long TotalRecords,
    long ProcessedRecords,
    long SuccessfulRecords,
    long FailedRecords);

public sealed record FlowExecutionDetailDto(
    Guid Id,
    Guid FlowId,
    ExecutionType ExecutionType,
    string IdempotencyKey,
    ExecutionStatus Status,
    TriggerSource TriggerSource,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long TotalRecords,
    long ProcessedRecords,
    long SuccessfulRecords,
    long FailedRecords,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>Ejecuciones de un Flow, más recientes primero (paginado).</summary>
public sealed record GetFlowExecutionsQuery(Guid FlowId, PageRequest Page)
    : IQuery<PagedResult<FlowExecutionSummaryDto>>;

/// <summary>Detalle de una ejecución. Los ítems por fila se exponen en una query propia (pueden ser millones).</summary>
public sealed record GetFlowExecutionByIdQuery(Guid ExecutionId) : IQuery<FlowExecutionDetailDto>;

public sealed class GetFlowExecutionsQueryHandler(IFlowExecutionRepository executionRepository)
    : IQueryHandler<GetFlowExecutionsQuery, PagedResult<FlowExecutionSummaryDto>>
{
    public async Task<PagedResult<FlowExecutionSummaryDto>> HandleAsync(
        GetFlowExecutionsQuery query,
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await executionRepository.ListByFlowAsync(
            query.FlowId,
            query.Page.Page,
            query.Page.PageSize,
            cancellationToken);

        var dtos = items.Select(ToSummary).ToList();
        return new PagedResult<FlowExecutionSummaryDto>(dtos, total, query.Page.Page, query.Page.PageSize);
    }

    internal static FlowExecutionSummaryDto ToSummary(FlowExecution e) => new(
        e.Id,
        e.FlowId,
        e.Status,
        e.TriggerSource,
        e.StartedAtUtc,
        e.CompletedAtUtc,
        e.TotalRecords,
        e.ProcessedRecords,
        e.SuccessfulRecords,
        e.FailedRecords);
}

public sealed class GetFlowExecutionByIdQueryHandler(IFlowExecutionRepository executionRepository)
    : IQueryHandler<GetFlowExecutionByIdQuery, FlowExecutionDetailDto>
{
    public async Task<FlowExecutionDetailDto> HandleAsync(
        GetFlowExecutionByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        var execution = await executionRepository.FindByIdAsync(query.ExecutionId, cancellationToken)
            ?? throw new NotFoundException("FlowExecution", query.ExecutionId);

        return new FlowExecutionDetailDto(
            execution.Id,
            execution.FlowId,
            execution.ExecutionType,
            execution.IdempotencyKey,
            execution.Status,
            execution.TriggerSource,
            execution.StartedAtUtc,
            execution.CompletedAtUtc,
            execution.TotalRecords,
            execution.ProcessedRecords,
            execution.SuccessfulRecords,
            execution.FailedRecords,
            execution.ErrorMessage,
            execution.CreatedAtUtc,
            execution.UpdatedAtUtc);
    }
}
