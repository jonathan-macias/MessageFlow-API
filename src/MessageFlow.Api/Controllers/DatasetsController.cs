using MessageFlow.Api.Contracts;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Common;
using MessageFlow.Application.Datasets.Commands;
using MessageFlow.Application.Datasets.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MessageFlow.Api.Controllers;

/// <summary>Datasets (§23): carga de Excel y lectura de estructura/filas.</summary>
[ApiController]
[Route("api/datasets")]
public sealed class DatasetsController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>
    /// Importa un archivo Excel (.xlsx) como dataset con columnas dinámicas (§8).
    /// phoneColumn es el nombre exacto de la columna del archivo que contiene los
    /// números telefónicos; debe existir entre los encabezados o la carga se rechaza.
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        [FromForm] IFormFile file,
        [FromForm] string name,
        [FromForm] string phoneColumn,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "Debe adjuntar un archivo." });
        }

        await using var content = file.OpenReadStream();

        var datasetId = await dispatcher.SendAsync<UploadDatasetCommand, Guid>(
            new UploadDatasetCommand(name, file.FileName, file.Length, content, phoneColumn),
            cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = datasetId }, new { id = datasetId });
    }

    /// <summary>Cambia la columna telefónica configurada del dataset.</summary>
    [HttpPut("{id:guid}/phone-column")]
    public async Task<IActionResult> UpdatePhoneColumn(Guid id, [FromBody] UpdateDatasetPhoneColumnRequest request, CancellationToken cancellationToken)
        => Ok(await dispatcher.SendAsync<UpdateDatasetPhoneColumnCommand, DatasetDto>(
            new UpdateDatasetPhoneColumnCommand(id, request.PhoneColumn),
            cancellationToken));

    /// <summary>Listado ligero de datasets (Id y Nombre) para selectores.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => Ok(await dispatcher.QueryAsync(new GetDatasetsQuery(), cancellationToken));

    /// <summary>Cantidad de datasets del usuario actual.</summary>
    [HttpGet("count")]
    [AllowAnonymous]
    public async Task<IActionResult> Count(CancellationToken cancellationToken)
        => Ok(await dispatcher.QueryAsync(new GetDatasetsCountQuery(), cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await dispatcher.QueryAsync(new GetDatasetQuery(id), cancellationToken));

    [HttpGet("{id:guid}/columns")]
    public async Task<IActionResult> GetColumns(Guid id, CancellationToken cancellationToken)
        => Ok(await dispatcher.QueryAsync(new GetDatasetColumnsQuery(id), cancellationToken));

    [HttpGet("{id:guid}/rows")]
    public async Task<IActionResult> GetRows(Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = PageRequest.DefaultPageSize, CancellationToken cancellationToken = default)
        => Ok(await dispatcher.QueryAsync(
            new GetDatasetRowsQuery(id, new PageRequest { Page = page, PageSize = pageSize }),
            cancellationToken));
}
