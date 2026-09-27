using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Infrastructure.Persistence;
using MessageFlow.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MessageFlow.Tests;

/// <summary>
/// La búsqueda vectorial se construye con LINQ, pero su valor depende de que se
/// traduzca al operador '&lt;=&gt;' de pgvector: cualquier otra forma de ordenar
/// obligaría a PostgreSQL a materializar y ordenar el dataset completo, y el índice
/// HNSW no se usaría. Estos tests fijan esa contrato sin necesidad de una base de datos,
/// porque ToQueryString() solo necesita el modelo.
/// </summary>
public class SemanticSearchSqlTests
{
    private const string DummyConnectionString =
        "Host=localhost;Port=5432;Database=messageflow;Username=postgres;Password=postgres";

    private static MessageFlowDbContext CreateContext()
        => new(new DbContextOptionsBuilder<MessageFlowDbContext>()
            .UseNpgsql(DummyConnectionString)
            .Options);

    [Fact]
    public void SearchSimilar_translates_to_the_cosine_operator()
    {
        using var context = CreateContext();
        var repository = new DatasetEmbeddingRepository(context);

        var query = BuildQuery(repository, Guid.NewGuid(), topK: 10);

        var sql = query.ToQueryString();

        // Contrato exacto, no solo "contiene <=>": el ORDER BY debe ser la expresión
        // escalar desnuda. Si alguien reintroduce un OrderBy sobre un miembro proyectado
        // (new Dto(...).Similarity) o ordena por la similitud ya restada, PostgreSQL
        // deja de poder usar el índice HNSW y ordena el dataset completo, y estos tests
        // no lo detectarían solo mirando que aparece el operador.
        Assert.Contains("WHERE d.dataset_id = @datasetId", sql);
        Assert.Contains("ORDER BY d.embedding <=> @queryVector", sql);
        Assert.Contains("LIMIT @p", sql);
    }

    [Fact]
    public void DatasetRowEmbedding_maps_to_a_vector_column()
    {
        using var context = CreateContext();

        var property = context.Model
            .FindEntityType(typeof(DatasetRowEmbedding))!
            .FindProperty(nameof(DatasetRowEmbedding.Embedding))!;

        Assert.Equal($"vector({DatasetRowEmbedding.VectorDimensions})", property.GetColumnType());
    }

    [Fact]
    public void VectorModel_uses_float_arrays_without_leaking_the_vector_type_into_the_domain()
    {
        // El dominio guarda float[] para no depender de Pgvector; la conversión ocurre en
        // Infrastructure. Si alguien cambia el tipo, esta prueba avisa.
        Assert.Equal(typeof(float[]), typeof(DatasetRowEmbedding).GetProperty(nameof(DatasetRowEmbedding.Embedding))!.PropertyType);
    }

    private static IQueryable<MessageFlow.Application.Abstractions.Persistence.ScoredDatasetRow> BuildQuery(
        DatasetEmbeddingRepository repository,
        Guid datasetId,
        int topK)
    {
        // Se replica la consulta del repositorio porque SearchSimilarAsync materializa el
        // resultado: para inspeccionar el SQL hace falta el IQueryable.
        var vector = new float[DatasetRowEmbedding.VectorDimensions];
        vector[0] = 1f;

        return repository.ProbeSearchQuery(datasetId, vector, topK);
    }
}
