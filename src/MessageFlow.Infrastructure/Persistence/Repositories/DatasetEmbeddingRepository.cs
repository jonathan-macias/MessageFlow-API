using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace MessageFlow.Infrastructure.Persistence.Repositories;

/// <summary>
/// Índice vectorial de los datasets, respaldado por pgvector.
///
/// La búsqueda se resuelve con la distancia coseno de pgvector y el orden se hace en la
/// base de datos. No se escribe SQL a mano: la única sentencia cruda del proyecto es la
/// que crea el índice HNSW en la migración, porque EF no puede expresar DDL de índice
/// vectorial.
/// </summary>
public sealed class DatasetEmbeddingRepository(MessageFlowDbContext context) : IDatasetEmbeddingRepository
{
    public Task<DatasetEmbeddingState?> FindStateAsync(
        Guid datasetId,
        CancellationToken cancellationToken = default)
        => context.DatasetEmbeddingStates
            .FirstOrDefaultAsync(s => s.DatasetId == datasetId, cancellationToken);

    public async Task AddStateAsync(DatasetEmbeddingState state, CancellationToken cancellationToken = default)
        => await context.DatasetEmbeddingStates.AddAsync(state, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListDatasetsPendingIndexingAsync(
        CancellationToken cancellationToken = default)
    {
        var statuses = new[]
        {
            DatasetEmbeddingStatus.Pending,
            DatasetEmbeddingStatus.Indexing,
        };

        return await context.DatasetEmbeddingStates
            .AsNoTracking()
            .Where(s => statuses.Contains(s.Status))
            .OrderBy(s => s.CreatedAtUtc)
            .Select(s => s.DatasetId)
            .ToListAsync(cancellationToken);
    }

    public async Task AddRangeAsync(
        IReadOnlyList<DatasetRowEmbedding> embeddings,
        CancellationToken cancellationToken = default)
        => await context.DatasetRowEmbeddings.AddRangeAsync(embeddings, cancellationToken);

    public async Task<IReadOnlyList<ScoredDatasetRow>> SearchSimilarAsync(
        Guid datasetId,
        float[] queryVector,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit < 1)
        {
            return [];
        }

        ValidateQueryVector(queryVector);

        var rows = await BuildSearchQuery(datasetId, queryVector, limit)
            .ToListAsync(cancellationToken);

        return rows;
    }

    /// <summary>
    /// Consulta vectorial sin materializar. Existe para poder inspeccionar el SQL
    /// generado en tests: si dejara de usar el operador '&lt;=&gt;', la búsqueda passaría
    /// a ordenar el dataset entero en PostgreSQL y el índice HNSW quedaría sin usar.
    /// </summary>
    internal IQueryable<ScoredDatasetRow> ProbeSearchQuery(
        Guid datasetId,
        float[] queryVector,
        int limit)
    {
        ValidateQueryVector(queryVector);
        return BuildSearchQuery(datasetId, queryVector, limit);
    }

    private static void ValidateQueryVector(float[] queryVector)
    {
        ArgumentNullException.ThrowIfNull(queryVector);

        if (queryVector.Length != DatasetRowEmbedding.VectorDimensions)
        {
            throw new DomainException(
                $"El vector de consulta tiene {queryVector.Length} dimensiones y la columna es " +
                $"vector({DatasetRowEmbedding.VectorDimensions}).");
        }
    }

    // CosineDistance se traduce al operador '<=>' de pgvector, que es la forma que
    // permite que PostgreSQL use el índice HNSW en lugar de ordenar el dataset entero.
    //
    // El ORDER BY va sobre la expresión escalar y no sobre un miembro de un objeto
    // proyectado: EF no traduce "new Dto(...).Miembro" como criterio de orden, y el
    // orden es justamente lo que el índice necesita para evitar el sort completo.
    private IQueryable<ScoredDatasetRow> BuildSearchQuery(
        Guid datasetId,
        float[] queryVector,
        int limit)
    {
        // El vector de consulta se envuelve en Pgvector.Vector a propósito. Un float[]
        // suelto lo tipa Npgsql como real[], y pgvector no define el operador '<=>' para
        // arrays: la consulta fallaba en PostgreSQL con
        // "42883 operator does not exist: vector <=> real[]". El tipo de la columna sí
        // está declarado como vector(768), pero eso no cambia cómo se infiere el
        // parámetro. El dominio sigue viendo float[]; el envoltorio es de Infrastructure.
        var query = new Pgvector.Vector(queryVector);

        var embeddings = context.DatasetRowEmbeddings
            .AsNoTracking()
            .Where(e => e.DatasetId == datasetId);

        return embeddings
            .OrderBy(e => VectorDbFunctionsExtensions.CosineDistance(e.Embedding, query))
            .Select(e => new
            {
                e.DatasetRowId,
                e.RowNumber,
                Distance = VectorDbFunctionsExtensions.CosineDistance(e.Embedding, query),
            })
            .Take(limit)
            // Similitud = 1 - distancia coseno. El rango queda en [-1, 1]: dos vectores
            // opuestos dan -1 y el modelo los trata como irrelevantes igual que un 0.
            .Select(e => new ScoredDatasetRow(e.DatasetRowId, e.RowNumber, 1.0 - e.Distance));
    }

    public Task<long> CountAsync(Guid datasetId, CancellationToken cancellationToken = default)
        => context.DatasetRowEmbeddings.LongCountAsync(e => e.DatasetId == datasetId, cancellationToken);

    public async Task DeleteByDatasetAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        await context.DatasetRowEmbeddings
            .Where(e => e.DatasetId == datasetId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.DatasetEmbeddingStates
            .Where(s => s.DatasetId == datasetId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
