using FluentValidation;
using MessageFlow.Application.AI;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Tests.TestSupport;
using Xunit;

namespace MessageFlow.Tests;

/// <summary>
/// Dominio y cálculo de la búsqueda semántica: máquina de estados del indexado, texto
/// indexable, aritmética de candidatos y parseo de operadores.
///
/// La parte que necesita PostgreSQL de verdad (columna vector, operador &lt;=&gt;, índice
/// HNSW) está en <see cref="Integration.PostgresVectorSearchTests"/>.
/// </summary>
public class DatasetEmbeddingDomainTests
{
    // ── DatasetRowEmbedding ────────────────────────────────────────────────────

    [Fact]
    public void El_embedding_rechaza_un_vector_del_ancho_equivocado()
    {
        var error = Assert.Throws<DomainException>(() => new DatasetRowEmbedding(
            Guid.NewGuid(), Guid.NewGuid(), 1, "texto", new float[10]));

        Assert.Contains("768", error.Message);
    }

    [Fact]
    public void El_embedding_copia_el_vector_para_que_el_llamante_no_lo_pueda_mutar()
    {
        var vector = new float[DatasetRowEmbedding.VectorDimensions];
        vector[3] = 0.5f;

        var embedding = new DatasetRowEmbedding(Guid.NewGuid(), Guid.NewGuid(), 1, "texto", vector);

        vector[3] = 99f;

        Assert.Equal(0.5f, embedding.Embedding[3]);
    }

    [Fact]
    public void El_embedding_trunca_el_contenido_al_limite()
    {
        var embedding = new DatasetRowEmbedding(
            Guid.NewGuid(), Guid.NewGuid(), 1, new string('a', DatasetRowEmbedding.ContentMaxLength + 500), new float[DatasetRowEmbedding.VectorDimensions]);

        Assert.Equal(DatasetRowEmbedding.ContentMaxLength, embedding.Content.Length);
    }

    // ── DatasetEmbeddingState ──────────────────────────────────────────────────

    [Fact]
    public void Un_estado_nace_pendiente_y_no_listo()
    {
        var state = new DatasetEmbeddingState(Guid.NewGuid(), DatasetRowEmbedding.VectorDimensions);

        Assert.Equal(DatasetEmbeddingStatus.Pending, state.Status);
        Assert.Throws<DomainException>(() => state.EnsureComplete());
    }

    [Fact]
    public void El_indexado_progresa_con_watermark_y_queda_listo_al_terminar()
    {
        var state = new DatasetEmbeddingState(Guid.NewGuid(), DatasetRowEmbedding.VectorDimensions);

        state.MarkPending(totalRows: 100);
        state.MarkIndexing();
        state.RecordProgress(embeddedRows: 50, lastRowNumber: 50, totalRows: 100);

        Assert.Equal(DatasetEmbeddingStatus.Indexing, state.Status);
        Assert.Equal(50, state.EmbeddedRows);
        Assert.Equal(50, state.LastEmbeddedRowNumber);
        Assert.Throws<DomainException>(() => state.EnsureComplete());

        state.MarkReady();

        state.EnsureComplete();
        Assert.Equal(DatasetEmbeddingStatus.Ready, state.Status);
    }

    [Fact]
    public void Listo_no_inventa_un_total_de_filas_que_se_saltaron_sin_texto()
    {
        // 8 filas, solo 5 con texto embebible: las otras 3 no se vectorizan. Marcar
        // EmbeddedRows = TotalRows mentiría sobre el índice construido.
        var state = new DatasetEmbeddingState(Guid.NewGuid(), DatasetRowEmbedding.VectorDimensions);
        state.MarkPending(totalRows: 8);
        state.MarkIndexing();
        state.RecordProgress(embeddedRows: 5, lastRowNumber: 8, totalRows: 8);
        state.MarkReady();

        Assert.Equal(5, state.EmbeddedRows);
        Assert.Equal(8, state.TotalRows);
    }

    [Fact]
    public void Un_fallo_conserva_el_watermark_para_reanudar_sin_repetir_trabajo()
    {
        var state = new DatasetEmbeddingState(Guid.NewGuid(), DatasetRowEmbedding.VectorDimensions);
        state.MarkPending(totalRows: 100);
        state.MarkIndexing();
        state.RecordProgress(embeddedRows: 50, lastRowNumber: 50, totalRows: 100);
        state.MarkFailed("429: quota exceeded");

        Assert.Equal(DatasetEmbeddingStatus.Failed, state.Status);
        Assert.Equal(50, state.LastEmbeddedRowNumber);
        Assert.Equal("429: quota exceeded", state.LastError);

        state.Retry();

        Assert.Equal(DatasetEmbeddingStatus.Pending, state.Status);
        Assert.Null(state.LastError);
        Assert.Equal(50, state.LastEmbeddedRowNumber);
    }

    [Fact]
    public void Reintentar_ignora_un_estado_que_no_falló()
    {
        var state = new DatasetEmbeddingState(Guid.NewGuid(), DatasetRowEmbedding.VectorDimensions);
        state.MarkPending(totalRows: 10);
        state.MarkIndexing();
        state.RecordProgress(embeddedRows: 10, lastRowNumber: 10, totalRows: 10);
        state.MarkReady();

        state.Retry();

        Assert.Equal(DatasetEmbeddingStatus.Ready, state.Status);
    }

    [Fact]
    public void Cambiar_el_ancho_del_vector_invalida_el_estado_existente()
    {
        // Un dataset indexado con 768 dimensiones no puede seguir en pie si la
        // configuración pasa a otro ancho: el estado debe decirlo, no mezclar vectores.
        var state = new DatasetEmbeddingState(Guid.NewGuid(), dimensions: 1536);

        var error = Assert.Throws<DomainException>(() => state.MarkPending(totalRows: 10));

        Assert.Contains("1536", error.Message);
    }

    [Fact]
    public void El_error_se_recorta_a_la_longitud_de_la_columna()
    {
        var state = new DatasetEmbeddingState(Guid.NewGuid(), DatasetRowEmbedding.VectorDimensions);

        state.MarkFailed(new string('x', DatasetEmbeddingState.LastErrorMaxLength + 100));

        Assert.Equal(DatasetEmbeddingState.LastErrorMaxLength, state.LastError!.Length);
    }

    // ── DatasetRowEmbeddingTextBuilder ─────────────────────────────────────────

    [Fact]
    public void El_texto_indexable_solo_incluye_columnas_de_texto()
    {
        var columns = new List<ColumnDefinition>
        {
            Column("Nombre", ColumnDataType.Text),
            Column("Edad", ColumnDataType.Number),
            Column("Nacimiento", ColumnDataType.Date),
            Column("Ciudad", ColumnDataType.Text),
        };

        var row = Row(("Nombre", "Ana"), ("Edad", "33"), ("Nacimiento", "1990-01-01"), ("Ciudad", "Bucaramanga"));

        var text = DatasetRowEmbeddingTextBuilder.Build(row, columns);

        // Los números y las fechas son territorio del endpoint estructurado: embeberlos
        // como texto degrada las búsquedas numéricas sin aportar nada.
        Assert.Contains("Nombre: Ana", text);
        Assert.Contains("Ciudad: Bucaramanga", text);
        Assert.DoesNotContain("Edad", text);
        Assert.DoesNotContain("33", text);
        Assert.DoesNotContain("Nacimiento", text);
    }

    [Fact]
    public void Una_fila_sin_texto_produce_contenido_vacio_y_no_se_indexa()
    {
        var columns = new List<ColumnDefinition> { Column("Edad", ColumnDataType.Number) };
        var row = Row(("Edad", "33"));

        Assert.Equal(DatasetRowEmbeddingTextBuilder.EmptyContent, DatasetRowEmbeddingTextBuilder.Build(row, columns));
    }

    [Fact]
    public void Las_celdas_vacias_no_generan_fragmentos_colgantes()
    {
        var columns = new List<ColumnDefinition> { Column("Nombre", ColumnDataType.Text), Column("Apellido", ColumnDataType.Text) };
        var row = Row(("Nombre", "Ana"), ("Apellido", "   "));

        Assert.Equal("Nombre: Ana", DatasetRowEmbeddingTextBuilder.Build(row, columns));
    }

    [Fact]
    public void Un_dataset_solo_con_numeros_no_admite_busqueda_semantica()
    {
        Assert.False(DatasetRowEmbeddingTextBuilder.HasIndexableColumns(
        [
            Column("Edad", ColumnDataType.Number),
            Column("Nacimiento", ColumnDataType.Date),
        ]));

        Assert.True(DatasetRowEmbeddingTextBuilder.HasIndexableColumns([Column("Nombre", ColumnDataType.Text)]));
    }

    // ── Operadores ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GreaterThan", true)]
    [InlineData("greater_than", true)]
    [InlineData("GREATER_THAN", true)]
    [InlineData("Contains", true)]
    // Enum.TryParse por sí solo acepta cualquier entero: sin Enum.IsDefined, un "3"
    // enviada por el cliente se convertiría en Contains.
    [InlineData("3", false)]
    [InlineData("0", false)]
    [InlineData("99", false)]
    [InlineData("Like", false)]
    [InlineData("", false)]
    public void El_parseo_de_operadores_es_estricto(string @operator, bool expected)
        => Assert.Equal(expected, SemanticSearchCommandValidator.TryParseOperator(@operator, out _));

    [Theory]
    // null usa DefaultTopK (10), así que 10 * 10 = 100 candidatos.
    [InlineData(null, 100)]
    [InlineData(0, 50)]
    [InlineData(1, 50)]
    [InlineData(10, 100)]
    [InlineData(50, 500)]
    [InlineData(60, 500)]
    public void Calcular_candidatos_escala_topk_pero_respeta_el_tope(int? topK, int expected)
    {
        var handler = CreateHandler(
            out _,
            out _,
            out _,
            out _,
            out _,
            out var options);

        Assert.Equal(expected, handler.CalculateCandidateCount(topK ?? options.DefaultTopK));
    }

    // ── Fakes auxiliares ───────────────────────────────────────────────────────

    private static SemanticSearchCommandHandler CreateHandler(
        out FakeDatasetRepository datasets,
        out FakeEmbeddingRepository embeddings,
        out FakeEmbeddingService embeddingService,
        out FakeGenerativeAIService ai,
        out FakeUnitOfWork unitOfWork,
        out SemanticSearchOptions options)
    {
        datasets = new FakeDatasetRepository();
        embeddings = new FakeEmbeddingRepository();
        embeddingService = new FakeEmbeddingService();
        ai = new FakeGenerativeAIService();
        unitOfWork = new FakeUnitOfWork();
        options = new SemanticSearchOptions
        {
            DefaultTopK = 10,
            MaxTopK = 50,
            CandidateMultiplier = 10,
            MinCandidates = 50,
            MaxCandidates = 500,
        };

        return new SemanticSearchCommandHandler(
            datasets,
            embeddings,
            embeddingService,
            ai,
            unitOfWork,
            options);
    }

    private static ColumnDefinition Column(string name, ColumnDataType type)
        => new(Guid.NewGuid(), name, type);

    private static DatasetRow Row(params (string Column, string? Value)[] cells)
        => new(
            Guid.NewGuid(),
            1,
            cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.OrdinalIgnoreCase));
}
