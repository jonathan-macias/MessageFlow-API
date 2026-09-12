using MessageFlow.Api.Contracts;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.AI;
using Microsoft.AspNetCore.Mvc;

namespace MessageFlow.Api.Controllers;

/// <summary>
/// AI Message Generator endpoint.
/// Generates WhatsApp message templates using AI based on user description and dataset variables.
/// </summary>
[ApiController]
[Route("api/ai/messages")]
public sealed class AiMessagesController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>
    /// Generates a WhatsApp message template using AI.
    /// The frontend sends only the description, tone, and datasetId.
    /// The backend retrieves dataset variables internally.
    /// </summary>
    /// <param name="request">Request with description, tone, and datasetId.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Generated message template and variables used.</returns>
    [HttpPost("generate")]
    public async Task<IActionResult> Generate(
        [FromBody] GenerateMessageRequest request,
        CancellationToken cancellationToken)
    {
        var command = new GenerateMessageCommand(
            request.Description,
            request.Tone,
            request.DatasetId);

        var result = await dispatcher.SendAsync<GenerateMessageCommand, GenerateMessageResponse>(command, cancellationToken);

        return Ok(result);
    }
}

/// <summary>
/// Request DTO for AI message generation.
/// </summary>
public sealed record GenerateMessageRequest(
    string Description,
    string Tone,
    Guid DatasetId);
