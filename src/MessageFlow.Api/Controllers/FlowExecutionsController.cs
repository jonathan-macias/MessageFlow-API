using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Executions.Queries;
using Microsoft.AspNetCore.Mvc;

namespace MessageFlow.Api.Controllers;

/// <summary>Consulta de ejecuciones: detalle de cada corrida de un Flow (§15, §23).</summary>
[ApiController]
[Route("api/flow-executions")]
public sealed class FlowExecutionsController(IDispatcher dispatcher) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await dispatcher.QueryAsync(new GetFlowExecutionByIdQuery(id), cancellationToken));
}
