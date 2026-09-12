using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;

namespace MessageFlow.Application.Flows.Queries;

public sealed record GetFlowsQuery(PageRequest Page) : IQuery<PagedResult<FlowSummaryDto>>;

public sealed class GetFlowsQueryValidator : AbstractValidator<GetFlowsQuery>
{
    public GetFlowsQueryValidator()
    {
        RuleFor(x => x.Page).NotNull();
        RuleFor(x => x.Page.Page).InclusiveBetween(1, int.MaxValue);
        RuleFor(x => x.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize);
    }
}

public sealed class GetFlowsQueryHandler(IFlowRepository flowRepository)
    : IQueryHandler<GetFlowsQuery, PagedResult<FlowSummaryDto>>
{
    public async Task<PagedResult<FlowSummaryDto>> HandleAsync(GetFlowsQuery query, CancellationToken cancellationToken = default)
    {
        var (items, total) = await flowRepository.ListAsync(query.Page.Page, query.Page.PageSize, cancellationToken);

        var dtos = items.Select(f => new FlowSummaryDto(
            f.Id,
            f.Name,
            f.Status,
            f.Channel,
            f.Schedule.Type,
            f.CreatedAtUtc)).ToList();

        return new PagedResult<FlowSummaryDto>(dtos, total, query.Page.Page, query.Page.PageSize);
    }
}
