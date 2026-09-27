namespace MessageFlow.Application.Abstractions;

/// <summary>
/// Genera vectores de embedding para búsqueda semántica.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Ancho del vector que emite el proveedor. La aplicación lo compara con
    /// <c>DatasetRowEmbedding.VectorDimensions</c> antes de persistir, para no
    /// escribir vectores que la columna <c>vector(n)</c> no puede aceptar.
    /// </summary>
    int Dimensions { get; }

    /// <summary>Vectoriza un único texto.</summary>
    Task<float[]> GenerateEmbeddingAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Vectoriza varios textos en una sola llamada. El endpoint batch del proveedor
    /// es más barato y evita agotar el rate limit en el free tier.
    /// </summary>
    Task<IReadOnlyList<float[]>> GenerateBatchAsync(
        IReadOnlyList<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// El modelo de embeddings optimiza la representación según si el texto se está
/// indexando o se usa como consulta de búsqueda.
/// </summary>
public enum EmbeddingPurpose
{
    /// <summary>Texto que se persiste como documento indexable (filas del dataset).</summary>
    Document = 1,

    /// <summary>Pregunta del usuario que se busca contra los documentos indexados.</summary>
    Query = 2,
}
