namespace MessageFlow.Domain.Enums;

/// <summary>
/// Estado del indexado semántico (embeddings) de un dataset.
/// </summary>
public enum DatasetEmbeddingStatus
{
    /// <summary>El usuario todavía no solicitó búsqueda semántica sobre el dataset.</summary>
    NotIndexed = 0,

    /// <summary>Solicitado y en cola para el worker de indexado.</summary>
    Pending = 1,

    /// <summary>El worker está generando embeddings.</summary>
    Indexing = 2,

    /// <summary>Todas las filas con texto embebible tienen vector.</summary>
    Ready = 3,

    /// <summary>El indexado se interrumpió por un error; <c>LastError</c> lo detalla.</summary>
    Failed = 4,
}
