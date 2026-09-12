using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.AI;
using Microsoft.AspNetCore.Mvc;

namespace MessageFlow.Api.Controllers;

/// <summary>
/// AI Dataset Query endpoint.
/// Converts natural language questions to structured queries and returns answers.
/// </summary>
[ApiController]
[Route("api/ai/datasets")]
public sealed class DatasetQueriesController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>
    /// Queries a dataset using natural language.
    /// The AI converts the question to a structured query, executes it, and returns an answer.
    /// </summary>
    /// <param name="request">Request with datasetId and question.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>AI-generated answer with query details and data.</returns>
    [HttpPost("query")]
    public async Task<IActionResult> Query(
        [FromBody] DatasetQueryRequest request,
        CancellationToken cancellationToken)
    {
        var command = new GenerateDatasetQueryCommand(
            request.DatasetId,
            request.Question);

        var result = await dispatcher.SendAsync<GenerateDatasetQueryCommand, DatasetQueryResponse>(command, cancellationToken);

        return Ok(result);
    }
}

/// <summary>
/// Request DTO for dataset query.
/// </summary>
public sealed record DatasetQueryRequest(
    Guid DatasetId,
    string Question);
