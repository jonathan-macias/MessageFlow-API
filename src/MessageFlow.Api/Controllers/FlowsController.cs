using MessageFlow.Api.Contracts;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Common;
using MessageFlow.Application.Executions.Queries;
using MessageFlow.Application.Flows.Commands;
using MessageFlow.Application.Flows.Queries;
using Microsoft.AspNetCore.Mvc;

namespace MessageFlow.Api.Controllers;

/// <summary>
/// Flows (§23): CRUD, transiciones de estado, "Send Now" y previews de mensaje/filtro.
/// Delgado por diseño: valida binding HTTP y delega en el dispatcher CQRS.
/// </summary>
[ApiController]
[Route("api/flows")]
public sealed class FlowsController(IDispatcher dispatcher) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateFlowCommand command, CancellationToken cancellationToken)
    {
        var flowId = await dispatcher.SendAsync<CreateFlowCommand, Guid>(command, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = flowId }, new { id = flowId });
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = PageRequest.DefaultPageSize, CancellationToken cancellationToken = default)
        => Ok(await dispatcher.QueryAsync(new GetFlowsQuery(new PageRequest { Page = page, PageSize = pageSize }), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await dispatcher.QueryAsync(new GetFlowByIdQuery(id), cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateDetails(Guid id, [FromBody] UpdateFlowDetailsRequest request, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new UpdateFlowDetailsCommand(id, request.Name, request.Description, request.Schedule), cancellationToken);
        return NoContent();
    }

    [HttpPut("{id:guid}/message")]
    public async Task<IActionResult> UpdateMessage(Guid id, [FromBody] UpdateFlowMessageRequest request, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new UpdateFlowMessageCommand(id, request.Message), cancellationToken);
        return NoContent();
    }

    /// <summary>Reemplaza el filtro raíz; cuerpo con Filter null lo limpia.</summary>
    [HttpPut("{id:guid}/filter")]
    public async Task<IActionResult> SetRootFilter(Guid id, [FromBody] SetFlowRootFilterRequest request, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new SetFlowRootFilterCommand(id, request.Filter), cancellationToken);
        return NoContent();
    }

    /// <summary>Eliminación física; solo permitida en Draft/Archived.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteFlowCommand(id), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new ActivateFlowCommand(id), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new PauseFlowCommand(id), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new ArchiveFlowCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// "Send Now" (§6): encola una ejecución inmediata y retorna 202 sin esperar el envío.
    /// </summary>
    [HttpPost("{id:guid}/execute")]
    public async Task<IActionResult> Execute(Guid id, CancellationToken cancellationToken)
    {
        var executionId = await dispatcher.SendAsync<ExecuteFlowCommand, Guid>(new ExecuteFlowCommand(id), cancellationToken);

        return AcceptedAtAction(
            nameof(FlowExecutionsController.GetById),
            "FlowExecutions",
            new { id = executionId },
            new { executionId });
    }

    [HttpGet("{id:guid}/executions")]
    public async Task<IActionResult> ListExecutions(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = PageRequest.DefaultPageSize, CancellationToken cancellationToken = default)
        => Ok(await dispatcher.QueryAsync(
            new GetFlowExecutionsQuery(id, new PageRequest { Page = page, PageSize = pageSize }),
            cancellationToken));

    /// <summary>Vista previa del mensaje renderizado (§24).</summary>
    [HttpPost("{id:guid}/preview")]
    public async Task<IActionResult> PreviewMessage(Guid id, [FromBody] PreviewMessageRequest request, CancellationToken cancellationToken)
        => Ok(await dispatcher.QueryAsync(
            new PreviewFlowMessageQuery(id, request.DatasetRowId, request.SampleValues, request.MissingVariablePolicy),
            cancellationToken));

    /// <summary>Vista previa del filtro raíz o de uno ad-hoc (§25).</summary>
    [HttpPost("{id:guid}/filters/preview")]
    public async Task<IActionResult> PreviewFilter(Guid id, [FromBody] FiltersPreviewRequest request, CancellationToken cancellationToken)
        => Ok(await dispatcher.QueryAsync(
            new PreviewFilteredRecordsQuery(id, request.Filter, request.SampleLimit),
            cancellationToken));
}
