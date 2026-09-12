using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;

namespace MessageFlow.Application.Datasets.Queries;

public sealed record DatasetRowDto(Guid Id, long RowNumber, IReadOnlyDictionary<string, string?> Values);

/// <summary>Preview paginado de las filas importadas de un dataset.</summary>
public sealed record GetDatasetRowsQuery(Guid DatasetId, PageRequest Page)
    : IQuery<PagedResult<DatasetRowDto>>;

public sealed class GetDatasetRowsQueryValidator : AbstractValidator<GetDatasetRowsQuery>
{
    public GetDatasetRowsQueryValidator()
    {
        RuleFor(x => x.DatasetId).NotEmpty();
        RuleFor(x => x.Page.Page).InclusiveBetween(1, int.MaxValue);
        RuleFor(x => x.Page.PageSize).InclusiveBetween(1, PageRequest.MaxPageSize);
    }
}

public sealed class GetDatasetRowsQueryHandler(IDatasetRepository datasetRepository)
    : IQueryHandler<GetDatasetRowsQuery, PagedResult<DatasetRowDto>>
{
    public async Task<PagedResult<DatasetRowDto>> HandleAsync(
        GetDatasetRowsQuery query,
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await datasetRepository.ListRowsAsync(
            query.DatasetId,
            query.Page.Page,
            query.Page.PageSize,
            cancellationToken);

        var dtos = items.Select(r => new DatasetRowDto(r.Id, r.RowNumber, r.Values)).ToList();

        return new PagedResult<DatasetRowDto>(dtos, total, query.Page.Page, query.Page.PageSize);
    }
}
