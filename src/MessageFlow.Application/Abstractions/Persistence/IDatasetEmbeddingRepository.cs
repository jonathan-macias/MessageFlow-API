using MessageFlow.Domain.Datasets;

namespace MessageFlow.Application.Abstractions.Persistence;

/// <summary>
/// Persistencia del índice vectorial de un dataset: los embeddings y el estado de
/// avance del indexado. La búsqueda siempre está acotada a un dataset; nunca cruza
/// datos entre datasets ni entre tenants.
///
/// El acotado por dataset es responsabilidad del repositorio de datasets (que aplica
/// ownership), no de este repositorio: aquí la única garantía es que no se mezclan filas.
/// </summary>
public interface IDatasetEmbeddingRepository
{
    // ── Estado de indexado ────────────────────────────────────────────────────
    Task<DatasetEmbeddingState?> FindStateAsync(Guid datasetId, CancellationToken cancellationToken = default);

    Task AddStateAsync(DatasetEmbeddingState state, CancellationToken cancellationToken = default);

    /// <summary>Datasets con indexado solicitado y pendiente de trabajo, para el worker.</summary>
    Task<IReadOnlyList<Guid>> ListDatasetsPendingIndexingAsync(CancellationToken cancellationToken = default);

    // ── Embeddings ─────────────────────────────────────────────────────────────
    Task AddRangeAsync(IReadOnlyList<DatasetRowEmbedding> embeddings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Filas del dataset más similares al vector de consulta, ordenadas por distancia
    /// coseno. La similitud va de 1.0 (idéntico) a 0.0 (ortogonal).
    /// </summary>
    Task<IReadOnlyList<ScoredDatasetRow>> SearchSimilarAsync(
        Guid datasetId,
        float[] queryVector,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Cantidad de filas del dataset que ya tienen vector.</summary>
    Task<long> CountAsync(Guid datasetId, CancellationToken cancellationToken = default);

    /// <summary>Elimina embeddings y estado de un dataset (reindexado desde cero).</summary>
    Task DeleteByDatasetAsync(Guid datasetId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fila recuperada de la búsqueda vectorial, con su puntaje de similitud.
/// </summary>
public sealed record ScoredDatasetRow(
    Guid DatasetRowId,
    long RowNumber,
    double Similarity);
