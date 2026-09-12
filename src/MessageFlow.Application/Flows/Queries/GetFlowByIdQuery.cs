using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Flows;

namespace MessageFlow.Application.Flows.Queries;

public sealed record GetFlowByIdQuery(Guid FlowId) : IQuery<FlowDetailDto>;

public sealed class GetFlowByIdQueryHandler(IFlowRepository flowRepository)
    : IQueryHandler<GetFlowByIdQuery, FlowDetailDto>
{
    public async Task<FlowDetailDto> HandleAsync(GetFlowByIdQuery query, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(query.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", query.FlowId);

        return ToDetail(flow);
    }

    internal static FlowDetailDto ToDetail(Flow flow) => new(
        flow.Id,
        flow.Name,
        flow.Description,
        flow.Status,
        flow.Channel,
        flow.DatasetId,
        flow.RecipientColumnId,
        flow.MessageTemplateText,
        flow.RootFilter.ToRequest(),
        flow.Schedule.ToDto(),
        flow.DataTrigger.ToDto(),
        flow.CreatedAtUtc,
        flow.UpdatedAtUtc);
}
