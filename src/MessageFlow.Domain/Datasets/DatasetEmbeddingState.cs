using MessageFlow.Domain.Common;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Datasets;

/// <summary>
/// Progreso del indexado semántico de un dataset. Permite mostrar avance al usuario,
/// reanudar tras una interrupción y depurar fallos del proveedor de embeddings.
/// No se crea al importar el dataset: se crea la primera vez que alguien pide una
/// búsqueda semántica, para no gastar cuota de la API sin que nadie lo solicite.
/// </summary>
public sealed class DatasetEmbeddingState : AuditableEntity
{
    public const int LastErrorMaxLength = 2000;

    public Guid DatasetId { get; private set; }

    public DatasetEmbeddingStatus Status { get; private set; } = DatasetEmbeddingStatus.NotIndexed;

    /// <summary>Filas del dataset declaradas en la importación.</summary>
    public long TotalRows { get; private set; }

    /// <summary>Filas ya persistidas con vector.</summary>
    public long EmbeddedRows { get; private set; }

    /// <summary>
    /// Watermark de keyset pagination: última fila cuyo embedding quedó confirmada.
    /// Permite reanudar el trabajo justo donde se cortó, sin releer el dataset.
    /// </summary>
    public long LastEmbeddedRowNumber { get; private set; }

    /// <summary>Ancho del vector persistido; valida que la config no cambie a mitad de indexado.</summary>
    public int Dimensions { get; private set; }

    public string? LastError { get; private set; }

    private DatasetEmbeddingState()
    {
    }

    public DatasetEmbeddingState(Guid datasetId, int dimensions)
    {
        if (datasetId == Guid.Empty)
        {
            throw new DomainException("El estado de indexado debe pertenecer a un dataset.");
        }

        if (dimensions <= 0)
        {
            throw new DomainException("El ancho del vector debe ser mayor que cero.");
        }

        DatasetId = datasetId;
        Dimensions = dimensions;
        Status = DatasetEmbeddingStatus.Pending;
    }

    public void MarkPending(long totalRows)
    {
        EnsureNoUnexpectedDimensions();
        TotalRows = totalRows < 0 ? 0 : totalRows;
        LastError = null;

        if (EmbeddedRows < TotalRows)
        {
            Status = DatasetEmbeddingStatus.Pending;
        }
    }

    public void MarkIndexing()
    {
        EnsureNoUnexpectedDimensions();
        Status = DatasetEmbeddingStatus.Indexing;
        LastError = null;
    }

    public void RecordProgress(long embeddedRows, long lastRowNumber, long totalRows)
    {
        EnsureNoUnexpectedDimensions();

        EmbeddedRows = embeddedRows < 0 ? 0 : embeddedRows;
        TotalRows = totalRows < 0 ? 0 : totalRows;
        LastEmbeddedRowNumber = lastRowNumber < 0 ? 0 : lastRowNumber;
        Status = DatasetEmbeddingStatus.Indexing;
    }

    public void MarkReady()
    {
        EnsureNoUnexpectedDimensions();

        // No se fuerza EmbeddedRows = TotalRows: las filas sin texto embebible se
        // saltan durante el indexado, así que el total con vector legitimately es menor
        // que el total de filas del dataset.
        Status = DatasetEmbeddingStatus.Ready;
        LastError = null;
    }

    public void MarkFailed(string? error)
    {
        EnsureNoUnexpectedDimensions();
        Status = DatasetEmbeddingStatus.Failed;
        LastError = Truncate(error);
    }

    /// <summary>Permite reintentar un indexado fallido conservando el watermark ya alcanzado.</summary>
    public void Retry()
    {
        EnsureNoUnexpectedDimensions();

        if (Status == DatasetEmbeddingStatus.Failed)
        {
            Status = DatasetEmbeddingStatus.Pending;
            LastError = null;
        }
    }

    /// <summary>
    /// Verifica que el estado quedó completo. Las filas sin texto embebible se saltan
    /// durante el indexado, así que el total de filas no es necesariamente el total
    /// de filas con vector.
    /// </summary>
    public void EnsureComplete()
    {
        if (Status != DatasetEmbeddingStatus.Ready)
        {
            throw new DomainException(
                $"El dataset todavía no está listo para búsqueda semántica (estado: {Status}). " +
                "Se indexa en segundo plano; intentá de nuevo en unos minutos.");
        }
    }

    private void EnsureNoUnexpectedDimensions()
    {
        if (Dimensions != DatasetRowEmbedding.VectorDimensions)
        {
            throw new DomainException(
                $"El estado de indexado fue creado con {Dimensions} dimensiones pero la base de datos " +
                $"usa {DatasetRowEmbedding.VectorDimensions}. Reindexá el dataset tras cambiar la configuración.");
        }
    }

    private static string? Truncate(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var trimmed = error.Trim();
        return trimmed.Length > LastErrorMaxLength ? trimmed[..LastErrorMaxLength] : trimmed;
    }
}
