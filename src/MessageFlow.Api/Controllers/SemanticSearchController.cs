using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.AI;
using Microsoft.AspNetCore.Mvc;

namespace MessageFlow.Api.Controllers;

/// <summary>
/// Búsqueda semántica (RAG) sobre los registros de un dataset.
///
/// Complementa —no reemplaza— a <see cref="DatasetQueriesController"/>: aquí la IA
/// recupera por significado (texto libre, lenguaje natural), y los filtros estructurados
/// que envía el cliente acotan el resultado sin abandonar la búsqueda vectorial.
///
/// El indexado de embeddings ocurre en segundo plano la primera vez que se usa un
/// dataset. Mientras tanto el endpoint responde con un error explícito en lugar de
/// devolver resultados parciales que el usuario tomaría como completos.
/// </summary>
[ApiController]
[Route("api/ai/datasets")]
public sealed class SemanticSearchController(
    IDispatcher dispatcher,
    SemanticSearchOptions options) : ControllerBase
{
    /// <summary>
    /// Busca registros por significado y responde en lenguaje natural.
    /// </summary>
    /// <param name="request">Dataset, pregunta, filtros opcionales y topK opcional.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Respuesta fundamentada en los registros recuperados, con sus filas y similitud.</returns>
    [HttpPost("semantic-search")]
    public async Task<IActionResult> Search(
        [FromBody] SemanticSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return Problem(
                title: "Semantic search is disabled.",
                detail: "The 'SemanticSearch:Enabled' setting is false on this server.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var command = new SemanticSearchCommand(
            request.DatasetId,
            request.Question,
            request.Filters?.Select(f => new SemanticSearchFilterRequest(f.Column, f.Operator, f.Value)).ToList(),
            request.TopK);

        var result = await dispatcher.SendAsync<SemanticSearchCommand, SemanticSearchResponse>(command, cancellationToken);

        return Ok(result);
    }
}

/// <summary>Request de búsqueda semántica.</summary>
public sealed record SemanticSearchRequest(
    Guid DatasetId,
    string Question,
    IReadOnlyList<SemanticSearchFilter>? Filters = null,
    int? TopK = null);

/// <summary>
/// Filtro estructurado. Acepta <c>equals</c>, <c>not_equals</c>, <c>contains</c>,
/// <c>greater_than</c>, <c>greater_than_or_equal</c>, <c>less_than</c>,
/// <c>less_than_or_equal</c>, <c>is_null</c> e <c>is_not_null</c>.
/// </summary>
public sealed record SemanticSearchFilter(
    string Column,
    string Operator,
    string? Value = null);
