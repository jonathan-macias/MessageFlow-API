using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Infrastructure.Persistence;
using MessageFlow.Infrastructure.Persistence.Repositories;
using MessageFlow.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MessageFlow.Tests.Integration;

/// <summary>
/// Búsqueda vectorial contra PostgreSQL real con pgvector.
///
/// Los tests de SQL translation (<see cref="SemanticSearchSqlTests"/>) fijan la forma del
/// consulta, pero no que la extensión, la columna <c>vector(768)</c> y el índice HNSW
/// existan de verdad. Eso solo se verifica contra la base: si la migración no creara la
/// extensión o el índice, la aplicación compilaría y los tests unitarios pasarían, y el
/// fallo aparecería únicamente en producción.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PostgresVectorSearchTests(PostgresDatabaseFixture fixture)
{
    [RequiresDockerFact]
    public async Task La_extension_vector_esta_creada()
    {
        var context = fixture.CreateContext();

        var installed = await context.Database
            .SqlQueryRaw<string>(
                """
                SELECT extversion AS "Value"
                FROM pg_extension
                WHERE extname = 'vector'
                """)
            .SingleOrDefaultAsync();

        Assert.False(string.IsNullOrWhiteSpace(installed));
    }

    [RequiresDockerFact]
    public async Task El_indice_hnsw_existe_con_el_opclass_de_coseno()
    {
        var context = fixture.CreateContext();

        // vector_cosine_ops importa tanto como el propio índice: la búsqueda ordena por
        // distancia coseno, y con el opclass equivocado el índice simplemente no se usa
        // (sin error, solo un seq scan silencioso por consulta).
        var definition = await context.Database
            .SqlQueryRaw<string>(
                """
                SELECT indexdef AS "Value"
                FROM pg_indexes
                WHERE tablename = 'dataset_row_embeddings'
                  AND indexname = 'ix_dataset_row_embeddings_embedding_hnsw'
                """)
            .SingleOrDefaultAsync();

        Assert.NotNull(definition);
        Assert.Contains("USING hnsw", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vector_cosine_ops", definition, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresDockerFact]
    public async Task El_vector_sobrevive_el_roundtrip_sin_perder_precision()
    {
        var context = fixture.CreateContext();
        var (datasetId, rowId) = await SeedAsync(context, "roundtrip");

        var written = UnitVector(axis: 0);
        await AddEmbeddingAsync(context, datasetId, rowId, rowNumber: 1, written);

        // Contexto nuevo: el valor tiene que venir de la columna vector, no de memoria.
        var reloaded = await fixture.CreateContext()
            .DatasetRowEmbeddings
            .AsNoTracking()
            .SingleAsync(e => e.DatasetRowId == rowId);

        Assert.Equal(DatasetRowEmbedding.VectorDimensions, reloaded.Embedding.Length);

        // float4 es lo que almacena pgvector: la comparación debe tolerar ese redondeo,
        // no exigir igualdad bit a bit, que fallaría por diseño.
        for (var i = 0; i < written.Length; i++)
        {
            Assert.Equal(written[i], reloaded.Embedding[i], precision: 5);
        }
    }

    [RequiresDockerFact]
    public async Task La_busqueda_ordena_por_distancia_coseno()
    {
        var context = fixture.CreateContext();
        var (datasetId, _) = await SeedAsync(context, "ranking", rows: 3);

        // Tres filas en una sola dimensión: 1.0 es idéntica a la consulta, 0.0 es
        // perpendicular (distancia 1) y -1.0 es opuesta (distancia 2, la peor posible).
        await AddEmbeddingAsync(context, datasetId, (await RowIdsAsync(context, datasetId))[0], 1, UnitVector(axis: 0));
        await AddEmbeddingAsync(context, datasetId, (await RowIdsAsync(context, datasetId))[1], 2, UnitVector(axis: 1));
        await AddEmbeddingAsync(context, datasetId, (await RowIdsAsync(context, datasetId))[2], 3, InvertedUnitVector(axis: 0));

        var repository = new DatasetEmbeddingRepository(fixture.CreateContext());

        var results = await repository.SearchSimilarAsync(datasetId, UnitVector(axis: 0), limit: 3);

        Assert.Equal([1L, 2, 3], results.Select(r => r.RowNumber));

        // Similitud = 1 - distancia coseno.
        Assert.Equal(1.0, results[0].Similarity, precision: 5);
        Assert.Equal(0.0, results[1].Similarity, precision: 5);
        Assert.Equal(-1.0, results[2].Similarity, precision: 5);
    }

    [RequiresDockerFact]
    public async Task La_busqueda_nunca_mezcla_filas_de_otro_dataset()
    {
        var context = fixture.CreateContext();
        var (datasetId, _) = await SeedAsync(context, "target");
        var (otherDatasetId, otherRowId) = await SeedAsync(context, "other");

        // La fila del otro dataset es una coincidencia perfecta: si el filtro por dataset
        // se perdiera, aparecería primera y el usuario vería datos ajenos.
        await AddEmbeddingAsync(context, otherDatasetId, otherRowId, rowNumber: 1, UnitVector(axis: 0));
        await AddEmbeddingAsync(context, datasetId, (await RowIdsAsync(context, datasetId))[0], rowNumber: 1, UnitVector(axis: 5));

        var repository = new DatasetEmbeddingRepository(fixture.CreateContext());

        var results = await repository.SearchSimilarAsync(datasetId, UnitVector(axis: 0), limit: 10);

        Assert.Equal([1L], results.Select(r => r.RowNumber));
        Assert.DoesNotContain(results, r => r.DatasetRowId == otherRowId);
    }

    [RequiresDockerFact]
    public async Task El_tope_de_resultados_se_respeta()
    {
        var context = fixture.CreateContext();
        var (datasetId, _) = await SeedAsync(context, "limit", rows: 5);
        var rowIds = await RowIdsAsync(context, datasetId);

        for (var i = 0; i < rowIds.Count; i++)
        {
            await AddEmbeddingAsync(context, datasetId, rowIds[i], i + 1, UnitVector(axis: i));
        }

        var repository = new DatasetEmbeddingRepository(fixture.CreateContext());

        Assert.Equal(2, (await repository.SearchSimilarAsync(datasetId, UnitVector(axis: 0), limit: 2)).Count);
        Assert.Empty(await repository.SearchSimilarAsync(datasetId, UnitVector(axis: 0), limit: 0));
    }

    [RequiresDockerFact]
    public async Task Un_dataset_sin_embeddings_devuelve_vacio_y_no_falla()
    {
        var context = fixture.CreateContext();
        var (datasetId, _) = await SeedAsync(context, "vacio", rows: 2);

        var repository = new DatasetEmbeddingRepository(fixture.CreateContext());

        Assert.Empty(await repository.SearchSimilarAsync(datasetId, UnitVector(axis: 0), limit: 10));
    }

    [RequiresDockerFact]
    public async Task Borrar_el_dataset_borra_embeddings_y_estado()
    {
        var context = fixture.CreateContext();
        var (datasetId, rowId) = await SeedAsync(context, "borrado");

        var repository = new DatasetEmbeddingRepository(context);
        await AddEmbeddingAsync(context, datasetId, rowId, rowNumber: 1, UnitVector(axis: 0));
        await repository.AddStateAsync(new DatasetEmbeddingState(datasetId, DatasetRowEmbedding.VectorDimensions));
        await context.SaveChangesAsync();

        await repository.DeleteByDatasetAsync(datasetId);

        Assert.Equal(0, await repository.CountAsync(datasetId));
        Assert.Null(await repository.FindStateAsync(datasetId));
    }

    [RequiresDockerFact]
    public async Task No_se_pueden_duplicar_embeddings_de_la_misma_fila()
    {
        var context = fixture.CreateContext();
        var (datasetId, rowId) = await SeedAsync(context, "duplicado");

        var repository = new DatasetEmbeddingRepository(context);
        await repository.AddRangeAsync(
        [
            new DatasetRowEmbedding(datasetId, rowId, 1, "texto", UnitVector(axis: 0)),
        ]);
        await context.SaveChangesAsync();

        // Un segundo embedding para la misma fila dejaría el índice con dos vectores
        // compitiendo y el resultado dependería del orden arbitrario del planner.
        // La base tiene que rechazarlo, no el dominio.
        await repository.AddRangeAsync(
        [
            new DatasetRowEmbedding(datasetId, rowId, 1, "texto", UnitVector(axis: 1)),
        ]);

        var error = await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Contains("ux_dataset_row_embeddings_dataset_row_number", error.Message);
    }

    [RequiresDockerFact]
    public async Task Solo_se_listan_para_indexar_los_datasets_pendientes_o_en_curso()
    {
        var context = fixture.CreateContext();
        var (pendingId, _) = await SeedAsync(context, "pending");
        var (readyId, _) = await SeedAsync(context, "ready");
        var (failedId, _) = await SeedAsync(context, "failed");

        var repository = new DatasetEmbeddingRepository(context);
        var pending = new DatasetEmbeddingState(pendingId, DatasetRowEmbedding.VectorDimensions);
        var ready = new DatasetEmbeddingState(readyId, DatasetRowEmbedding.VectorDimensions);
        var failed = new DatasetEmbeddingState(failedId, DatasetRowEmbedding.VectorDimensions);
        failed.MarkFailed("429 del proveedor");

        await repository.AddStateAsync(pending);
        await repository.AddStateAsync(ready);
        await repository.AddStateAsync(failed);
        await context.SaveChangesAsync();

        // Ready no se reindexa. Failed tampoco: reencolar el indexado es responsabilidad
        // de una nueva búsqueda del usuario, no del worker, o un error de cuota convertiría
        // al worker en un bucle que agota el rate limit.
        ready.MarkReady();
        await context.SaveChangesAsync();

        var queued = await new DatasetEmbeddingRepository(fixture.CreateContext())
            .ListDatasetsPendingIndexingAsync();

        Assert.Contains(pendingId, queued);
        Assert.Contains(failedId, queued);
        Assert.DoesNotContain(readyId, queued);
    }

    private static async Task<(Guid DatasetId, Guid RowId)> SeedAsync(
        MessageFlowDbContext fixtureContext,
        string name,
        int rows = 1)
    {
        var context = fixtureContext;
        var dataset = await IntegrationData.CreateDatasetAsync(context, name: name);

        var cells = Enumerable
            .Range(0, rows)
            .Select(i => ($"Nombre{i}", $"correo{i}@x.com", (string?)null))
            .ToArray();

        var added = await IntegrationData.AddRowsAsync(context, dataset.Id, cells);

        return (dataset.Id, added[0].Id);
    }

    private static async Task<List<Guid>> RowIdsAsync(MessageFlowDbContext context, Guid datasetId)
        => await context.DatasetRows
            .AsNoTracking()
            .Where(r => r.DatasetId == datasetId)
            .OrderBy(r => r.RowNumber)
            .Select(r => r.Id)
            .ToListAsync();

    private static async Task AddEmbeddingAsync(
        MessageFlowDbContext context,
        Guid datasetId,
        Guid rowId,
        long rowNumber,
        float[] vector)
    {
        var repository = new DatasetEmbeddingRepository(context);
        await repository.AddRangeAsync(
        [
            new DatasetRowEmbedding(datasetId, rowId, rowNumber, $"contenido de la fila {rowNumber}", vector),
        ]);
        await context.SaveChangesAsync();
    }

    /// <summary>Vector de ancho 768 con un único 1 en <paramref name="axis"/>: así la distancia coseno es 0, 1 o 2 según el eje.</summary>
    private static float[] UnitVector(int axis)
    {
        var vector = new float[DatasetRowEmbedding.VectorDimensions];
        vector[axis] = 1f;
        return vector;
    }

    private static float[] InvertedUnitVector(int axis)
    {
        var vector = UnitVector(axis);
        vector[axis] = -1f;
        return vector;
    }
}
