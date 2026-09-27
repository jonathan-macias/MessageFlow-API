using MessageFlow.Domain.Common;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Datasets;

/// <summary>
/// Vector embedding de una fila de dataset, usado por la búsqueda semántica (RAG).
/// Es una tabla separada de <see cref="DatasetRow"/> a propósito: el motor de
/// ejecución de Flows y la previsualización no necesitan arrastrar el vector
/// (~3 KB por fila con 768 dimensiones) en cada lectura.
///
/// El texto indexable se construye únicamente con columnas <see cref="Enums.ColumnDataType.Text"/>:
/// los hechos numéricos y las agregaciones exactas siguen siendo territorio del
/// endpoint de consulta estructurada.
/// </summary>
public sealed class DatasetRowEmbedding : Entity
{
    /// <summary>
    /// Ancho del vector en PostgreSQL (<c>vector(768)</c>). Fijo por diseño: cambiarlo
    /// exige una migración nueva y reindexar todos los datasets, por eso la
    /// configuración de embeddings se valida contra esta constante al arrancar.
    /// </summary>
    public const int VectorDimensions = 768;

    /// <summary>Tope del texto indexable; evita exceder el límite de tokens del modelo.</summary>
    public const int ContentMaxLength = 8000;

    public Guid DatasetId { get; private set; }

    public Guid DatasetRowId { get; private set; }

    /// <summary>Número de fila en el archivo original (misma semántica que <see cref="DatasetRow.RowNumber"/>).</summary>
    public long RowNumber { get; private set; }

    /// <summary>Texto plano que se envió al modelo de embeddings (trazabilidad y auditoría).</summary>
    public string Content { get; private set; } = default!;

    /// <summary>
    /// Vector del embedding. Se expone como <c>float[]</c> porque es exactamente el
    /// tipo que Npgsql mapea contra la columna <c>vector(n)</c>.
    /// </summary>
    public float[] Embedding { get; private set; } = [];

    private DatasetRowEmbedding()
    {
    }

    public DatasetRowEmbedding(
        Guid datasetId,
        Guid datasetRowId,
        long rowNumber,
        string content,
        float[] embedding)
    {
        if (datasetId == Guid.Empty)
        {
            throw new DomainException("El embedding debe pertenecer a un dataset.");
        }

        if (datasetRowId == Guid.Empty)
        {
            throw new DomainException("El embedding debe pertenecer a una fila del dataset.");
        }

        if (rowNumber < 1)
        {
            throw new DomainException("El número de fila debe ser mayor o igual a 1.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentNullException.ThrowIfNull(embedding);

        if (embedding.Length != VectorDimensions)
        {
            throw new DomainException(
                $"El vector debe tener {VectorDimensions} dimensiones y se recibieron {embedding.Length}. " +
                "Verificá 'Gemini:EmbeddingDimensions' y que el modelo configurado emita ese ancho.");
        }

        DatasetId = datasetId;
        DatasetRowId = datasetRowId;
        RowNumber = rowNumber;
        // El texto que se guarda es exactamente el que se embebió, truncado al límite:
        // si divergieran, Content dejaría de servir para auditoría y depuración.
        Content = content.Length > ContentMaxLength ? content[..ContentMaxLength] : content;
        // Copia defensiva: un float[] es mutable y el vector no puede cambiar una vez
        // calculado, o el índice quedaría desalineado respecto del texto guardado.
        Embedding = [.. embedding];
    }
}
