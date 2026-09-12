using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;

namespace MessageFlow.Application.Datasets.Queries;

public sealed record DatasetDto(
    Guid Id,
    string Name,
    string SourceFileName,
    long RowCount,
    string PhoneColumn,
    DateTimeOffset CreatedAtUtc);

public sealed record ColumnDefinitionDto(Guid Id, string Name, ColumnDataType DataType);

public sealed record GetDatasetQuery(Guid DatasetId) : IQuery<DatasetDto>;

/// <summary>Listado ligero de datasets (Id y Nombre) para selectores del frontend.</summary>
public sealed record DatasetSummaryDto(Guid Id, string Name);

public sealed record GetDatasetsQuery : IQuery<IReadOnlyList<DatasetSummaryDto>>;

public sealed class GetDatasetsQueryHandler(IDatasetRepository datasetRepository)
    : IQueryHandler<GetDatasetsQuery, IReadOnlyList<DatasetSummaryDto>>
{
    public async Task<IReadOnlyList<DatasetSummaryDto>> HandleAsync(
        GetDatasetsQuery query,
        CancellationToken cancellationToken = default)
        => [.. (await datasetRepository.ListSummariesAsync(cancellationToken))
            .Select(d => new DatasetSummaryDto(d.Id, d.Name))];
}

public sealed record GetDatasetColumnsQuery(Guid DatasetId) : IQuery<IReadOnlyList<ColumnDefinitionDto>>;

public sealed class GetDatasetQueryHandler(IDatasetRepository datasetRepository)
    : IQueryHandler<GetDatasetQuery, DatasetDto>
{
    public async Task<DatasetDto> HandleAsync(GetDatasetQuery query, CancellationToken cancellationToken = default)
    {
        var dataset = await datasetRepository.FindByIdAsync(query.DatasetId, cancellationToken)
            ?? throw new NotFoundException("Dataset", query.DatasetId);

        return new DatasetDto(dataset.Id, dataset.Name, dataset.SourceFileName, dataset.RowCount, dataset.PhoneColumn, dataset.CreatedAtUtc);
    }
}

public sealed class GetDatasetColumnsQueryHandler(IDatasetRepository datasetRepository)
    : IQueryHandler<GetDatasetColumnsQuery, IReadOnlyList<ColumnDefinitionDto>>
{
    public async Task<IReadOnlyList<ColumnDefinitionDto>> HandleAsync(GetDatasetColumnsQuery query, CancellationToken cancellationToken = default)
    {
        var columns = await datasetRepository.GetColumnDefinitionsAsync(query.DatasetId, cancellationToken);

        return [.. columns.Select(c => new ColumnDefinitionDto(c.Id, c.Name, c.DataType))];
    }
}
