using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Messaging;

namespace MessageFlow.Application.Flows.Queries;

/// <summary>
/// Vista previa del mensaje renderizado (§24): con una fila real del dataset
/// (DatasetRowId) o con valores de muestra provistos por el frontend.
/// </summary>
public sealed record PreviewFlowMessageQuery(
    Guid FlowId,
    Guid? DatasetRowId = null,
    IReadOnlyDictionary<string, string?>? SampleValues = null,
    MissingVariablePolicy MissingVariablePolicy = MissingVariablePolicy.Fail)
    : IQuery<MessagePreviewDto>;

public sealed record MessagePreviewDto(string RenderedMessage, IReadOnlyList<string> VariablesUsed);

public sealed class PreviewFlowMessageQueryValidator : AbstractValidator<PreviewFlowMessageQuery>
{
    public PreviewFlowMessageQueryValidator()
    {
        RuleFor(x => x.FlowId).NotEmpty();
        RuleFor(x => x.MissingVariablePolicy).IsInEnum();

        RuleFor(x => x)
            .Must(q => q.DatasetRowId.HasValue || (q.SampleValues?.Count ?? 0) > 0)
            .WithMessage("Provea 'DatasetRowId' o 'SampleValues' para generar la vista previa.")
            .WithName(nameof(PreviewFlowMessageQuery.DatasetRowId));
    }
}

public sealed class PreviewFlowMessageQueryHandler(
    IFlowRepository flowRepository,
    IDatasetRepository datasetRepository)
    : IQueryHandler<PreviewFlowMessageQuery, MessagePreviewDto>
{
    public async Task<MessagePreviewDto> HandleAsync(PreviewFlowMessageQuery query, CancellationToken cancellationToken = default)
    {
        var flow = await flowRepository.FindByIdAsync(query.FlowId, cancellationToken)
            ?? throw new NotFoundException("Flow", query.FlowId);

        var columns = await datasetRepository.GetColumnDefinitionsAsync(flow.DatasetId, cancellationToken);

        // Las variables pueden venir por Nombre o por Id de columna; se normalizan
        // antes de renderizar para que ambos formatos produzcan el mismo resultado.
        var template = TemplateColumnAliases.Apply(flow.GetMessageTemplate(), columns);

        IReadOnlyDictionary<string, string?> values;

        if (query.DatasetRowId.HasValue)
        {
            var row = await datasetRepository.FindRowAsync(flow.DatasetId, query.DatasetRowId.Value, cancellationToken)
                ?? throw new NotFoundException("DatasetRow", query.DatasetRowId.Value);

            values = row.Values;
        }
        else
        {
            values = TemplateColumnAliases.TranslateSampleValues(query.SampleValues!, columns);
        }

        var rendered = template.Render(values, query.MissingVariablePolicy);

        return new MessagePreviewDto(rendered, template.ExtractVariableNames());
    }
}
