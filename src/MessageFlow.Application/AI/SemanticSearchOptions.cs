namespace MessageFlow.Application.AI;

/// <summary>
/// Ajustes de la búsqueda semántica y del worker que indexa los embeddings.
/// Bound desde la sección "SemanticSearch" de appsettings.json; los valores de abajo
/// son los que se usan si la sección no está definida.
/// </summary>
public sealed class SemanticSearchOptions
{
    public const string SectionName = "SemanticSearch";

    /// <summary>Permite apagar la búsqueda semántica sin desregistrar el código.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Filas a recuperar por consulta si el cliente no especifica <c>topK</c>.</summary>
    public int DefaultTopK { get; set; } = 10;

    /// <summary>Tope duro de filas por consulta.</summary>
    public int MaxTopK { get; set; } = 50;

    /// <summary>
    /// Cuántos candidatos vectoriales se piden por cada fila que se va a devolver.
    /// Como los filtros estructurados se evalúan en memoria sobre los candidatos, un
    /// multiplicador mayor mejora la precisión con filtros selectivos a costa de hidratar
    /// más filas.
    /// </summary>
    public int CandidateMultiplier { get; set; } = 10;

    /// <summary>Piso de candidatos, para que un <c>topK</c> chico no se quede sin margen.</summary>
    public int MinCandidates { get; set; } = 50;

    /// <summary>Techo de candidatos, para acotar la memoria por consulta.</summary>
    public int MaxCandidates { get; set; } = 500;

    /// <summary>Valida rangos coherentes. Se invoca al arrancar la aplicación.</summary>
    public void Validate()
    {
        if (DefaultTopK < 1)
        {
            throw new InvalidOperationException("SemanticSearch:DefaultTopK must be greater than zero.");
        }

        if (MaxTopK < DefaultTopK)
        {
            throw new InvalidOperationException("SemanticSearch:MaxTopK must be greater than or equal to DefaultTopK.");
        }

        if (MaxTopK > SemanticSearchCommandValidator.AbsoluteMaxTopK)
        {
            throw new InvalidOperationException(
                $"SemanticSearch:MaxTopK cannot exceed {SemanticSearchCommandValidator.AbsoluteMaxTopK}.");
        }

        if (CandidateMultiplier < 1)
        {
            throw new InvalidOperationException("SemanticSearch:CandidateMultiplier must be greater than zero.");
        }

        if (MinCandidates < 1 || MaxCandidates < 1 || MinCandidates > MaxCandidates)
        {
            throw new InvalidOperationException(
                "SemanticSearch candidate bounds are invalid (MinCandidates must be between 1 and MaxCandidates).");
        }
    }
}

/// <summary>
/// Ritmo del worker de indexado. En el free tier de Gemini los límites de requests
/// por minuto son bajos, así que el trabajo se hace en lotes chicos con pausa entre
/// lotes y solo unas cuantas tandas por ciclo.
/// </summary>
public sealed class EmbeddingIndexingOptions
{
    /// <summary>Permite apagar el worker sin desregistrarlo.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Filas por llamada al endpoint batch de embeddings.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Pausa entre lotes, para no disparar el rate limit.</summary>
    public int DelayBetweenBatchesMs { get; set; } = 1500;

    /// <summary>Cada cuánto tiempo se busca trabajo pendiente.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Tandas de lotes por ciclo. Limita cuánto se gasta por vuelta y deja respirar
    /// al rate limit del proveedor.
    /// </summary>
    public int MaxBatchesPerRun { get; set; } = 20;

    public void Validate()
    {
        if (BatchSize is < 1 or > 100)
        {
            throw new InvalidOperationException("SemanticSearch:Indexing:BatchSize must be between 1 and 100.");
        }

        if (DelayBetweenBatchesMs < 0)
        {
            throw new InvalidOperationException("SemanticSearch:Indexing:DelayBetweenBatchesMs cannot be negative.");
        }

        if (PollIntervalSeconds < 5)
        {
            throw new InvalidOperationException("SemanticSearch:Indexing:PollIntervalSeconds must be at least 5.");
        }

        if (MaxBatchesPerRun < 1)
        {
            throw new InvalidOperationException("SemanticSearch:Indexing:MaxBatchesPerRun must be greater than zero.");
        }
    }
}
