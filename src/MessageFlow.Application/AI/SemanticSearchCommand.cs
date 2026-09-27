using FluentValidation;
using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Domain.AI;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Application.AI;

/// <summary>
/// Búsqueda semántica (RAG) sobre un dataset.
///
/// Flujo: se vectoriza la pregunta, se recuperan las filas más similares del dataset,
/// se aplican los filtros estructurados que envió el usuario sobre ese conjunto y se
/// redacta la respuesta con esos registros como única evidencia.
///
/// Los filtros los envía el usuario de forma explícita, no los inventa el modelo: así
/// un "solo clientes de Bucaramanga" es reproducible y auditable.
/// </summary>
public sealed record SemanticSearchCommand(
    Guid DatasetId,
    string Question,
    IReadOnlyList<SemanticSearchFilterRequest>? Filters = null,
    int? TopK = null) : ICommand<SemanticSearchResponse>;

/// <summary>Filtro estructurado opcional que acota la búsqueda semántica.</summary>
public sealed record SemanticSearchFilterRequest(
    string Column,
    string Operator,
    string? Value = null);

public sealed class SemanticSearchCommandValidator : AbstractValidator<SemanticSearchCommand>
{
    /// <summary>
    /// Tope duro de seguridad. El valor por defecto sale de "SemanticSearch:DefaultTopK"
    /// y el máximo real de "SemanticSearch:MaxTopK"; este límite solo evita que una
    /// petición absurda llegue siquiera a la base de datos.
    /// </summary>
    public const int AbsoluteMaxTopK = 50;

    public SemanticSearchCommandValidator()
    {
        RuleFor(x => x.DatasetId)
            .NotEmpty().WithMessage("Dataset ID is required.");

        RuleFor(x => x.Question)
            .NotEmpty().WithMessage("Question is required.")
            .MaximumLength(1000).WithMessage("Question cannot exceed 1000 characters.");

        RuleFor(x => x.TopK)
            .InclusiveBetween(1, AbsoluteMaxTopK)
            .WithMessage($"TopK must be between 1 and {AbsoluteMaxTopK}.");

        RuleFor(x => x.Filters)
            .Must(f => f is null || f.Count <= 5)
            .WithMessage("A maximum of 5 filters is allowed.");

        RuleForEach(x => x.Filters).ChildRules(filter =>
        {
            filter.RuleFor(f => f.Column)
                .NotEmpty().WithMessage("Filter column is required.");

            filter.RuleFor(f => f.Operator)
                .NotEmpty().WithMessage("Filter operator is required.")
                .Must(BeKnownOperator)
                .WithMessage("Filter operator is not supported.");

            filter.RuleFor(f => f.Value)
                .Must((f, value) => AllowsValue(f.Operator, value))
                .WithMessage("This filter operator requires a value.");
        });
    }

    private static bool BeKnownOperator(string? @operator)
        => TryParseOperator(@operator, out _);

    private static bool AllowsValue(string? @operator, string? value)
    {
        if (!TryParseOperator(@operator, out var parsed))
        {
            return true;
        }

        return parsed is DatasetQueryOperator.IsNull or DatasetQueryOperator.IsNotNull
            || !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Acepta notación PascalCase y snake_case: "greater_than" y "GreaterThan" son el
    /// mismo operador.
    /// </summary>
    internal static string Normalize(string? @operator)
    {
        if (string.IsNullOrWhiteSpace(@operator))
        {
            return string.Empty;
        }

        return @operator.Replace("_", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// Parseo estricto del operador, compartido por el validator y el handler.
    ///
    /// No alcanza con <c>Enum.IsDefined</c>: <c>Enum.TryParse</c> convierte el texto "3"
    /// en el miembro <c>Contains</c> (valor 3), y ese miembro sí está definido, así que la
    /// comprobación adicional lo daba por válido. Un cliente que envíe "3" obtendría un
    /// filtro de contención sin haber pedido contención, y el filtro se aplicaría en
    /// silencio. Por eso el texto numérico se descarta antes de parsear.
    /// </summary>
    internal static bool TryParseOperator(string? @operator, out DatasetQueryOperator parsed)
    {
        parsed = default;

        var normalized = Normalize(@operator);

        if (string.IsNullOrEmpty(normalized) || long.TryParse(normalized, out _))
        {
            return false;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }
}

public sealed class SemanticSearchCommandHandler(
    IDatasetRepository datasetRepository,
    IDatasetEmbeddingRepository embeddingRepository,
    IEmbeddingService embeddingService,
    IGenerativeAIService aiService,
    IUnitOfWork unitOfWork,
    SemanticSearchOptions options) : ICommandHandler<SemanticSearchCommand, SemanticSearchResponse>
{
    public async Task<SemanticSearchResponse> HandleAsync(
        SemanticSearchCommand command,
        CancellationToken cancellationToken = default)
    {
        // 1. Ownership + existencia. El repositorio de datasets aplica el filtro por
        //    propietario, así que un dataset ajeno es indistinguible de uno inexistente.
        var dataset = await datasetRepository.FindByIdAsync(command.DatasetId, cancellationToken)
            ?? throw new NotFoundException("Dataset", command.DatasetId);

        var columns = await datasetRepository.GetColumnDefinitionsAsync(command.DatasetId, cancellationToken);

        if (columns.Count == 0)
        {
            throw new DomainException("The selected dataset has no columns.");
        }

        if (!DatasetRowEmbeddingTextBuilder.HasIndexableColumns(columns))
        {
            throw new DomainException(
                "This dataset has no text columns, so semantic search cannot be performed on it. " +
                "Use the structured query endpoint for aggregations and exact counts.");
        }

        var columnIndex = DatasetRowFilterEvaluator.BuildColumnIndex(columns);
        var filters = NormalizeFilters(command.Filters, columnIndex);

        // 2. El índice debe estar completo. Si no lo está, se encola y se avisa con un
        //    mensaje accionable en lugar de devolver resultados parciales silenciosos.
        await EnsureIndexReadyAsync(dataset, cancellationToken);

        var topK = Math.Min(command.TopK ?? options.DefaultTopK, options.MaxTopK);
        var embeddedRows = await embeddingRepository.CountAsync(command.DatasetId, cancellationToken);

        var appliedFilters = filters
            .Select(f => new GroundedFilter(f.Column, f.Operator.ToString(), f.Value))
            .ToList();

        // 3. Búsqueda vectorial acotada al dataset.
        var queryVector = await embeddingService.GenerateEmbeddingAsync(
            command.Question,
            EmbeddingPurpose.Query,
            cancellationToken);

        var candidates = await embeddingRepository.SearchSimilarAsync(
            command.DatasetId,
            queryVector,
            CalculateCandidateCount(topK),
            cancellationToken);

        if (candidates.Count == 0)
        {
            // No se llama al modelo: sin evidencia no hay respuesta que dar, y cada
            // llamada consume cuota.
            return new SemanticSearchResponse(
                "No matching records were found in this dataset.",
                topK,
                appliedFilters,
                [],
                embeddedRows,
                dataset.RowCount);
        }

        // 4. Hidratar los candidatos en un solo round-trip y aplicar los filtros.
        var rows = await datasetRepository.GetRowsByIdsAsync(
            command.DatasetId,
            candidates.Select(c => c.DatasetRowId).ToList(),
            cancellationToken);

        var rowsById = rows.ToDictionary(r => r.Id);
        var matches = new List<SemanticSearchMatch>(topK);

        foreach (var candidate in candidates)
        {
            if (matches.Count == topK)
            {
                break;
            }

            if (!rowsById.TryGetValue(candidate.DatasetRowId, out var row))
            {
                continue;
            }

            if (!DatasetRowFilterEvaluator.Matches(row.Values, filters, columnIndex))
            {
                continue;
            }

            matches.Add(new SemanticSearchMatch(row.Id, row.RowNumber, candidate.Similarity, row.Values));
        }

        if (matches.Count == 0)
        {
            return new SemanticSearchResponse(
                "No matching records were found in this dataset for the given filters.",
                topK,
                appliedFilters,
                [],
                embeddedRows,
                dataset.RowCount);
        }

        // 5. Redactar la respuesta con los registros recuperados como única evidencia.
        var answer = await aiService.GenerateGroundedResponseAsync(
            command.Question,
            [.. matches.Select(m => new GroundedRow(m.RowNumber, m.Similarity, m.Values))],
            appliedFilters,
            cancellationToken);

        return new SemanticSearchResponse(
            answer,
            topK,
            appliedFilters,
            matches,
            embeddedRows,
            dataset.RowCount);
    }

    /// <summary>
    /// Verifica el índice y, si todavía no existe, lo deja encolado para el worker.
    /// Un error explícito es preferible a una respuesta basada en la mitad del dataset,
    /// que el usuario interpretaría como el resultado completo.
    /// </summary>
    private async Task EnsureIndexReadyAsync(Dataset dataset, CancellationToken cancellationToken)
    {
        var state = await embeddingRepository.FindStateAsync(dataset.Id, cancellationToken);

        if (state is null)
        {
            state = new DatasetEmbeddingState(dataset.Id, embeddingService.Dimensions);
            state.MarkPending(dataset.RowCount);
            await embeddingRepository.AddStateAsync(state, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        else
        {
            state.Retry();

            if (state.Status != DatasetEmbeddingStatus.Ready)
            {
                state.MarkPending(dataset.RowCount);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }

        state.EnsureComplete();
    }

    /// <summary>
    /// Cuántos candidatos vectoriales se piden antes de filtrar en memoria. La base de
    /// datos ordena por similitud y devuelve más de los necesarios para que el filtrado
    /// pueda descartar filas sin vaciar el resultado. Con filtros muy selectivos puede
    /// quedarse corto: es el compromiso de no escribir SQL a mano sobre el jsonb.
    /// </summary>
    internal int CalculateCandidateCount(int topK)
    {
        var scaled = topK * options.CandidateMultiplier;
        var floor = Math.Min(options.MinCandidates, options.MaxCandidates);
        return Math.Clamp(scaled, floor, Math.Max(options.MaxCandidates, topK));
    }

    private static List<DatasetQueryFilter> NormalizeFilters(
        IReadOnlyList<SemanticSearchFilterRequest>? requests,
        IReadOnlyDictionary<string, ColumnDefinition> columnIndex)
    {
        if (requests is null || requests.Count == 0)
        {
            return [];
        }

        var filters = new List<DatasetQueryFilter>(requests.Count);

        foreach (var request in requests)
        {
            if (!columnIndex.ContainsKey(request.Column))
            {
                throw new DomainException(
                    $"Filter column '{request.Column}' does not exist in the dataset.");
            }

            if (!SemanticSearchCommandValidator.TryParseOperator(request.Operator, out var parsed))
            {
                throw new DomainException($"Invalid filter operator: '{request.Operator}'.");
            }

            filters.Add(DatasetQueryFilter.Create(request.Column, parsed, request.Value));
        }

        return filters;
    }
}

/// <summary>Respuesta de la búsqueda semántica.</summary>
public sealed record SemanticSearchResponse(
    string Answer,
    int TopK,
    IReadOnlyList<GroundedFilter> AppliedFilters,
    IReadOnlyList<SemanticSearchMatch> Matches,
    long IndexedRows,
    long TotalRows);

/// <summary>Registro recuperado, con su similitud coseno y sus valores originales.</summary>
public sealed record SemanticSearchMatch(
    Guid RowId,
    long RowNumber,
    double Similarity,
    IReadOnlyDictionary<string, string?> Values);
