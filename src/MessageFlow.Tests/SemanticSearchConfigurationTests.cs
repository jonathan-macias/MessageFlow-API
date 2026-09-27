using MessageFlow.Application.AI;
using MessageFlow.Domain.Datasets;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MessageFlow.Tests;

/// <summary>
/// La configuración de búsqueda semántica se enlaza a clases de opciones al arrancar.
/// Si una clave se renombra o se mueve de sección, el binding no falla: devuelve los
/// valores por defecto de la clase, y el worker indexaría con otros lotes y pausas sin
/// que nadie se entere hasta ver un 429 o un índice que tarda horas.
///
/// Estos tests leen el appsettings.json que realmente se despliega, no una copia de
/// prueba, y comprueban que enlaza y que pasa su propia validación.
/// </summary>
public class SemanticSearchConfigurationTests
{
    private static IConfiguration LoadAppSettings(string fileName = "api-appsettings.json")
        => new ConfigurationBuilder()
            .AddJsonFile(fileName, optional: false)
            .Build();

    [Fact]
    public void La_seccion_SemanticSearch_enlaza_con_sus_valores_configurados()
    {
        var options = LoadAppSettings()
            .GetSection(SemanticSearchOptions.SectionName)
            .Get<SemanticSearchOptions>();

        Assert.NotNull(options);
        options.Validate();

        Assert.True(options.Enabled);
        Assert.Equal(10, options.DefaultTopK);
        Assert.Equal(50, options.MaxTopK);
        Assert.Equal(10, options.CandidateMultiplier);
        Assert.Equal(50, options.MinCandidates);
        Assert.Equal(500, options.MaxCandidates);
    }

    [Fact]
    public void La_subseccion_Indexing_enlaza_con_sus_valores_configurados()
    {
        // Crítico para el free tier: DelayBetweenBatchesMs en 0 convertiría el indexado en
        // un bucle de llamadas que agota el rate limit del proveedor.
        var options = LoadAppSettings()
            .GetSection($"{SemanticSearchOptions.SectionName}:Indexing")
            .Get<EmbeddingIndexingOptions>();

        Assert.NotNull(options);
        options.Validate();

        Assert.True(options.Enabled);
        Assert.Equal(50, options.BatchSize);
        Assert.Equal(1500, options.DelayBetweenBatchesMs);
        Assert.Equal(30, options.PollIntervalSeconds);
        Assert.Equal(20, options.MaxBatchesPerRun);
    }

    [Fact]
    public void El_ancho_de_embedding_configurado_coincide_con_la_columna_vector()
    {
        var dimensions = LoadAppSettings().GetValue<int>("Gemini:EmbeddingDimensions");

        // Si divergen, el arranque falla a propósito (fail fast) en vez de escribir vectores
        // que la columna vector(768) no puede aceptar a mitad de una indexación de horas.
        Assert.Equal(DatasetRowEmbedding.VectorDimensions, dimensions);
    }

    [Fact]
    public void El_modelo_de_embeddings_esta_configurado()
    {
        var model = LoadAppSettings().GetValue<string>("Gemini:EmbeddingModel");

        Assert.False(string.IsNullOrWhiteSpace(model));
    }

    [Fact]
    public void La_conexion_habilita_el_barrido_iterativo_de_hnsw()
    {
        var connectionString = LoadAppSettings()
            .GetConnectionString("Default");

        Assert.NotNull(connectionString);

        // HNSW no admite dataset_id como columna de índice, así que el filtro por dataset
        // se resuelve después de recorrer el grafo. Sin barrido iterativo, buscar un
        // dataset pequeño dentro de una tabla grande devuelve menos de TopK resultados.
        Assert.Contains("hnsw.iterative_scan", connectionString, StringComparison.Ordinal);
    }

    [Fact]
    public void El_appsettings_de_ejemplo_declara_las_mismas_claves_que_el_real()
    {
        // El example es lo primero que copia quien despliega. Si se queda atrás respecto de
        // appsettings.json, el entorno nuevo arranca con los valores por defecto de las
        // clases de opciones en lugar de con los previstos.
        var real = LoadAppSettings("api-appsettings.json");
        var example = LoadAppSettings("api-appsettings.example.json");

        foreach (var key in new[]
                 {
                     "SemanticSearch:Enabled",
                     "SemanticSearch:DefaultTopK",
                     "SemanticSearch:MaxTopK",
                     "SemanticSearch:CandidateMultiplier",
                     "SemanticSearch:MinCandidates",
                     "SemanticSearch:MaxCandidates",
                     "SemanticSearch:Indexing:Enabled",
                     "SemanticSearch:Indexing:BatchSize",
                     "SemanticSearch:Indexing:DelayBetweenBatchesMs",
                     "SemanticSearch:Indexing:PollIntervalSeconds",
                     "SemanticSearch:Indexing:MaxBatchesPerRun",
                     "Gemini:EmbeddingModel",
                     "Gemini:EmbeddingDimensions",
                 })
        {
            Assert.NotNull(example[key]);
            Assert.NotNull(real[key]);
        }
    }
}
