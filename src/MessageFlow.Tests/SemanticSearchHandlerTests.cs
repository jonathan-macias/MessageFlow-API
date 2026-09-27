using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.AI;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Tests.TestSupport;
using Xunit;

namespace MessageFlow.Tests;

/// <summary>
/// Orquestación de la búsqueda semántica: ownership, filtros, encolado del indexado y
/// redacción fundamentada.
///
/// La parte de persistencia vectorial está en <see cref="Integration.PostgresVectorSearchTests"/>.
/// Aquí importa el comportamiento del handler, que es donde se decide qué evidencia ve el
/// modelo y qué se responde cuando el índice todavía no está listo.
/// </summary>
public class SemanticSearchHandlerTests
{
    [Fact]
    public async Task Un_dataset_ajeno_o_inexistente_produce_NotFound()
    {
        // El repositorio de datasets aplica ownership, así que un dataset de otro usuario
        // es indistinguible de uno inexistente: la respuesta no puede revelar que existe.
        var scenario = Scenario.Create();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            scenario.Handler.HandleAsync(new SemanticSearchCommand(Guid.NewGuid(), "clientes de Bucaramanga")));
    }

    [Fact]
    public async Task Un_dataset_sin_columnas_de_texto_orienta_hacia_el_endpoint_estructurado()
    {
        var scenario = Scenario.Create(withTextColumns: false);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Handler.HandleAsync(new SemanticSearchCommand(scenario.Dataset.Id, "cuántos hay")));

        Assert.Contains("structured query", error.Message);
    }

    [Fact]
    public async Task La_primera_busqueda_encola_el_indexado_y_falla_con_un_mensaje_accionable()
    {
        var scenario = Scenario.Create();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Handler.HandleAsync(new SemanticSearchCommand(scenario.Dataset.Id, "clientes de Bucaramanga")));

        // Sin índice no se devuelve nada: una respuesta construida sobre la mitad del
        // dataset se leería como el resultado completo.
        Assert.Contains("segundo plano", error.Message);

        var state = await scenario.Embeddings.FindStateAsync(scenario.Dataset.Id);
        Assert.NotNull(state);
        Assert.Equal(DatasetEmbeddingStatus.Pending, state.Status);
        Assert.Equal(scenario.Dataset.RowCount, state.TotalRows);

        // Y no se gastó cuota de generación en un intento imposible.
        Assert.Equal(0, scenario.Ai.GroundedCallCount);
    }

    [Fact]
    public async Task La_primera_busqueda_no_vectoriza_la_pregunta_antes_de_tener_indice()
    {
        var scenario = Scenario.Create();

        await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Handler.HandleAsync(new SemanticSearchCommand(scenario.Dataset.Id, "clientes")));

        Assert.Empty(scenario.EmbeddingService.Calls);
    }

    [Fact]
    public async Task Un_indexado_fallido_se_vuelve_a_encolar_conservando_el_avance()
    {
        var scenario = Scenario.Create();
        var state = scenario.Embeddings.StateFor(scenario.Dataset.Id);
        state.MarkIndexing();
        state.RecordProgress(embeddedRows: 2, lastRowNumber: 2, totalRows: scenario.Dataset.RowCount);
        state.MarkFailed("429: quota exceeded");

        await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Handler.HandleAsync(new SemanticSearchCommand(scenario.Dataset.Id, "clientes")));

        Assert.Equal(DatasetEmbeddingStatus.Pending, state.Status);
        Assert.Null(state.LastError);

        // El watermark sobrevive al reintento: el worker no vuelve a embeber las filas ya
        // procesadas, que en free tier es la diferencia entre acabar o no.
        Assert.Equal(2, state.LastEmbeddedRowNumber);
        Assert.Equal(2, state.EmbeddedRows);
    }

    [Fact]
    public async Task Sin_candidatos_no_se_llama_al_modelo()
    {
        var scenario = Scenario.Create(ready: true);

        var response = await scenario.Handler.HandleAsync(
            new SemanticSearchCommand(scenario.Dataset.Id, "nada relevante"));

        // Cada llamada al modelo cuesta cuota; sin evidencia no hay nada que redactar.
        Assert.Equal(0, scenario.Ai.GroundedCallCount);
        Assert.Empty(response.Matches);
        Assert.Contains("No matching records", response.Answer);
    }

    [Fact]
    public async Task La_pregunta_se_vectoriza_como_consulta_y_no_como_documento()
    {
        var scenario = Scenario.Create(ready: true);

        await scenario.Handler.HandleAsync(new SemanticSearchCommand(scenario.Dataset.Id, "clientes de Bucaramanga"));

        var call = Assert.Single(scenario.EmbeddingService.Calls);
        Assert.Equal("clientes de Bucaramanga", call.Text);
        Assert.Equal(EmbeddingPurpose.Query, call.Purpose);
    }

    [Fact]
    public async Task La_respuesta_se_redacta_solo_con_las_filas_que_cumplen_los_filtros()
    {
        var scenario = Scenario.Create(
            ready: true,
            rows: [("Ana", "Bucaramanga"), ("Luis", "Bogotá"), ("Sara", "Bucaramanga")]);

        // Ana es la más similar, Luis la segunda: sin el filtro, Luis entraría en la
        // evidencia. El orden importa para comprobar que el filtrado descarta y no reordena.
        scenario.SetSearchResults(scenario.Rows[0], scenario.Rows[1], scenario.Rows[2]);

        var response = await scenario.Handler.HandleAsync(new SemanticSearchCommand(
            scenario.Dataset.Id,
            "clientes de Bucaramanga",
            [new SemanticSearchFilterRequest("Ciudad", "equals", "Bucaramanga")]));

        Assert.Equal([1L, 3], response.Matches.Select(m => m.RowNumber));
        Assert.Equal("respuesta fundamentada", response.Answer);

        // Ana y Sara, no Luis: el modelo no puede ver una fila que el filtro descartó,
        // o respondería con datos que el usuario pidió excluir.
        Assert.Equal([1L, 3], scenario.Ai.LastGroundedRows.Select(r => r.RowNumber));
        Assert.Equal("clientes de Bucaramanga", scenario.Ai.LastGroundedQuestion);
    }

    [Fact]
    public async Task Los_filtros_aplicados_se_reportan_en_la_respuesta_y_al_modelo()
    {
        var scenario = Scenario.Create(ready: true, rows: [("Ana", "Bucaramanga")]);
        scenario.SetSearchResults(scenario.Rows[0]);

        var response = await scenario.Handler.HandleAsync(new SemanticSearchCommand(
            scenario.Dataset.Id,
            "clientes",
            [new SemanticSearchFilterRequest("Ciudad", "equals", "Bucaramanga")]));

        var filter = Assert.Single(response.AppliedFilters);
        Assert.Equal("Ciudad", filter.Column);
        Assert.Equal("Equals", filter.Operator);
        Assert.Equal("Bucaramanga", filter.Value);

        // El modelo recibe los filtros para poder explicar por qué la respuesta está
        // acotada, en lugar de presentar un subconjunto como si fuera el total.
        Assert.Single(scenario.Ai.LastGroundedFilters);
    }

    [Fact]
    public async Task Un_filtro_sobre_una_columna_inexistente_se_rechaza_antes_de_buscar()
    {
        var scenario = Scenario.Create(ready: true);

        var error = await Assert.ThrowsAsync<DomainException>(() => scenario.Handler.HandleAsync(new SemanticSearchCommand(
            scenario.Dataset.Id,
            "clientes",
            [new SemanticSearchFilterRequest("ColumnaInventada", "equals", "x")])));

        Assert.Contains("ColumnaInventada", error.Message);
        Assert.Equal(0, scenario.Ai.GroundedCallCount);
    }

    [Fact]
    public async Task Un_operador_invalido_se_rechaza_antes_de_buscar()
    {
        var scenario = Scenario.Create(ready: true);

        await Assert.ThrowsAsync<DomainException>(() => scenario.Handler.HandleAsync(new SemanticSearchCommand(
            scenario.Dataset.Id,
            "clientes",
            [new SemanticSearchFilterRequest("Ciudad", "drop_table", "x")])));

        Assert.Equal(0, scenario.Ai.GroundedCallCount);
    }

    [Fact]
    public async Task Un_filtro_que_deja_sin_filas_lo_dice_sin_inventar_una_respuesta()
    {
        var scenario = Scenario.Create(ready: true, rows: [("Luis", "Bogotá")]);
        scenario.SetSearchResults(scenario.Rows[0]);

        var response = await scenario.Handler.HandleAsync(new SemanticSearchCommand(
            scenario.Dataset.Id,
            "clientes",
            [new SemanticSearchFilterRequest("Ciudad", "equals", "Bucaramanga")]));

        Assert.Empty(response.Matches);
        Assert.Equal(0, scenario.Ai.GroundedCallCount);
        Assert.Contains("No matching records", response.Answer);
    }

    [Fact]
    public async Task La_respuesta_informa_las_filas_indexadas_y_el_total_del_dataset()
    {
        var scenario = Scenario.Create(ready: true, rows: [("Ana", "Bucaramanga"), ("Luis", "Bogotá")]);
        scenario.SetSearchResults(scenario.Rows[0], scenario.Rows[1]);

        var response = await scenario.Handler.HandleAsync(new SemanticSearchCommand(scenario.Dataset.Id, "clientes"));

        Assert.Equal(10, response.TopK);
        Assert.Equal(2, response.Matches.Count);
        Assert.Equal(2, response.IndexedRows);
        Assert.Equal(scenario.Dataset.RowCount, response.TotalRows);
    }
    [Fact]
    public async Task Un_candidato_sin_fila_hidratada_se_omite_en_lugar_de_romper_la_respuesta()
    {
        var scenario = Scenario.Create(ready: true, rows: [("Ana", "Bucaramanga")]);

        // Una fila que ya no existe puede aparecer si el dataset se editó entre la
        // indexación y la búsqueda: se descarta y se sigue con el resto.
        scenario.Embeddings.SearchResults =
        [
            new(Guid.NewGuid(), 99, 0.99),
            new(scenario.Rows[0].Id, 1, 0.80),
        ];

        var response = await scenario.Handler.HandleAsync(new SemanticSearchCommand(scenario.Dataset.Id, "clientes"));

        Assert.Single(response.Matches);
        Assert.Equal(1, response.Matches[0].RowNumber);
        Assert.Equal(1, scenario.Ai.GroundedCallCount);
    }

    /// <summary>
    /// Escenario completo y coherente: un dataset, sus filas, su estado de indexado y las
    /// dependencias del handler, todas cableadas desde el principio. Evita estados
    /// compartidos entre tests y reemplazos por reflexión a mitad de ejecución.
    /// </summary>
    private sealed class Scenario
    {
        private readonly FakeDatasetRepository _datasets;

        private Scenario(
            FakeDatasetRepository datasets,
            FakeEmbeddingRepository embeddings,
            FakeEmbeddingService embeddingService,
            FakeGenerativeAIService ai,
            FakeUnitOfWork unitOfWork,
            Dataset dataset,
            IReadOnlyList<DatasetRow> rows,
            SemanticSearchOptions options)
        {
            _datasets = datasets;
            Embeddings = embeddings;
            EmbeddingService = embeddingService;
            Ai = ai;
            Dataset = dataset;
            Rows = rows;
            Handler = new SemanticSearchCommandHandler(
                datasets, embeddings, embeddingService, ai, unitOfWork, options);
        }

        public Dataset Dataset { get; }

        public IReadOnlyList<DatasetRow> Rows { get; }

        public FakeEmbeddingRepository Embeddings { get; }

        public FakeEmbeddingService EmbeddingService { get; }

        public FakeGenerativeAIService Ai { get; }

        public SemanticSearchCommandHandler Handler { get; }

        public static Scenario Create(
            bool ready = false,
            bool withTextColumns = true,
            IReadOnlyList<(string Nombre, string Ciudad)>? rows = null)
        {
            var cells = rows ?? [("Ana", "Bucaramanga")];

            var dataset = Dataset.Create("Contactos", "contactos.xlsx", "user-1");

            // Un dataset sin ninguna columna de texto es el caso que debe rechazarse: no
            // hay nada que embeber, y el camino correcto es el endpoint estructurado.
            dataset.AddColumn("Nombre", withTextColumns ? ColumnDataType.Text : ColumnDataType.Number, ordinal: 0);
            dataset.AddColumn("Ciudad", withTextColumns ? ColumnDataType.Text : ColumnDataType.Date, ordinal: 1);
            dataset.SetPhoneColumn("Nombre");
            dataset.SetRowCount(cells.Count);

            var columns = dataset.GetColumnDefinitions();

            var datasets = new FakeDatasetRepository();
            datasets.Load(dataset);
            datasets.Load(columns, []);

            var datasetRows = cells
                .Select((cell, index) => new DatasetRow(
                    dataset.Id,
                    index + 1,
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Nombre"] = cell.Nombre,
                        ["Ciudad"] = cell.Ciudad,
                    }))
                .ToList();

            datasets.AddRowsAsync(datasetRows).GetAwaiter().GetResult();

            var embeddings = new FakeEmbeddingRepository();
            var state = new DatasetEmbeddingState(dataset.Id, DatasetRowEmbedding.VectorDimensions);
            state.MarkPending(dataset.RowCount);
            embeddings.AddStateAsync(state).GetAwaiter().GetResult();

            if (ready)
            {
                state.MarkIndexing();
                state.RecordProgress(datasetRows.Count, datasetRows.Count, dataset.RowCount);
                state.MarkReady();

                foreach (var row in datasetRows)
                {
                    embeddings.AddRangeAsync(
                    [
                        new DatasetRowEmbedding(
                            dataset.Id,
                            row.Id,
                            row.RowNumber,
                            DatasetRowEmbeddingTextBuilder.Build(row, columns),
                            new float[DatasetRowEmbedding.VectorDimensions]),
                    ]).GetAwaiter().GetResult();
                }
            }

            return new Scenario(
                datasets,
                embeddings,
                new FakeEmbeddingService(),
                new FakeGenerativeAIService(),
                new FakeUnitOfWork(),
                dataset,
                datasetRows,
                new SemanticSearchOptions
                {
                    DefaultTopK = 10,
                    MaxTopK = 50,
                    CandidateMultiplier = 10,
                    MinCandidates = 50,
                    MaxCandidates = 500,
                });
        }

        /// <summary>
        /// Define los candidatos vectoriales en el orden de similitud dado, que es como
        /// los devuelve el repositorio real (de más a menos similar).
        /// </summary>
        public void SetSearchResults(params DatasetRow[] rows)
            => Embeddings.SearchResults =
            [
                .. rows.Select((row, index) => new ScoredDatasetRow(row.Id, row.RowNumber, 1.0 - (index * 0.05)))
            ];
    }
}
