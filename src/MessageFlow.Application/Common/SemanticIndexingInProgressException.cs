using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Application.Common;

/// <summary>
/// El índice semántico de un dataset todavía se está construyendo.
///
/// Se distingue de un <see cref="DomainException"/> genérico porque el frontend necesita
/// reaccionar de otra manera: no es un error de la solicitud del usuario, es un estado
/// transitorio del recurso. Mostrarlo como error de validación haría que el usuario
/// creyera que escribió mal la pregunta, cuando puede repetir la misma consulta en
/// minutos y obtener el resultado.
///
/// La API la mapea a 409 con <c>code: "semantic_search_indexing"</c>.
/// </summary>
public sealed class SemanticIndexingInProgressException(Guid datasetId, string status)
    : DomainException(
        $"El dataset todavía no está listo para búsqueda semántica (estado: {status}). " +
        "Se indexa en segundo plano; intentá de nuevo en unos minutos.")
{
    /// <summary>Código estable para el cliente. No depende del texto del mensaje.</summary>
    public const string ErrorCode = "semantic_search_indexing";

    public Guid DatasetId { get; } = datasetId;

    /// <summary>Estado actual del indexado, por ejemplo "Pending", "Indexing" o "Failed".</summary>
    public string Status { get; } = status;
}
