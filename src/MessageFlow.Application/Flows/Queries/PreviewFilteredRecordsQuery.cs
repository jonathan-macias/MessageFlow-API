using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Application.Datasets.Queries;
using MessageFlow.Application.Flows.Commands;
using MessageFlow.Domain.Filters;
using MessageFlow.Domain.Flows;

namespace MessageFlow.Application.Flows.Queries;

/// <summary>
/// Preview de filtros (§25): cuenta cuántas filas del dataset coinciden con el filtro
/// y devuelve una muestra. Permite probar un filtro ad-hoc ANTES de guardarlo
/// (Filter no nulo) o evaluar el filtro raíz almacenado del Flow.
/// Evaluación en streaming con <see cref="FilterMatcher"/>: nunca construye SQL.
/// </summary>
public sealed record PreviewFilteredRecordsQuery(
    Guid FlowId,
    FilterGroupRequest? Filter = null,
    int SampleLimit = 20)
    : IQuery<PreviewFilteredRecordsResult>;

public sealed record PreviewFilteredRecordsResult(
    long TotalMatches,
    long TotalRows,
    IReadOnlyList<DatasetRowDto> Sample);

public sealed class PreviewFilteredRecordsQueryValidator : AbstractValidator<PreviewFilteredRecordsQuery>
{
    public PreviewFilteredRecordsQueryValidator(IValidator<FilterGroupRequest> filterValidator)
    {
        RuleFor(x => x.FlowId).NotEmpty();
        RuleFor(x => x.SampleLimit).InclusiveBetween(1, 100);

#pragma warning disable CS8620
        RuleFor(x => x.Filter)!
            .SetValidator(filterValidator)
            .When(x => x.Filter is not null);
#pragma warning restore CS8620
    }
}

public sealed class PreviewFilteredRecordsQueryHandler(
    IFlowRepository flowRepository,
    IDatasetRepository datasetRepository)
    : IQueryHandler<PreviewFilteredRecordsQuery, PreviewFilteredRecordsResult>
{
    public async Task<PreviewFilteredRecordsResult> HandleAsync(
        PreviewFilteredRecordsQuery query,
        CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(query.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", query.FlowId);

        var columns = await datasetRepository.GetColumnDefinitionsAsync(flow.DatasetId, cancellationToken);

        // Filtro ad-hoc a probar (sin persistir) o el almacenado en el agregado.
        var candidateFilter = query.Filter is not null ? query.Filter.ToGroup() : flow.RootFilter;
        var matcher = new FilterMatcher(candidateFilter, columns);

        long totalRows = 0;
        long totalMatches = 0;
        List<DatasetRowDto> sample = [];

        await foreach (var row in datasetRepository.StreamRowsAsync(flow.DatasetId, cancellationToken))
        {
            totalRows++;

            if (!matcher.Matches(row.Values))
            {
                continue;
            }

            totalMatches++;

            if (sample.Count < query.SampleLimit)
            {
                sample.Add(new DatasetRowDto(row.Id, row.RowNumber, row.Values));
            }
        }

        return new PreviewFilteredRecordsResult(totalMatches, totalRows, sample);
    }
}
