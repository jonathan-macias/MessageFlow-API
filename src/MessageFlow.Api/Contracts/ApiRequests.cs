using MessageFlow.Application.Flows;
using MessageFlow.Domain.Enums;

namespace MessageFlow.Api.Contracts;

/// <summary>
/// DTOs de solicitud propios de la capa HTTP: combinan datos de ruta y cuerpo para
/// construir los Commands de Application sin exponerlos como contratos de transporte.
/// </summary>
public sealed record UpdateFlowDetailsRequest(string Name, string? Description, ScheduleRequest? Schedule = null);

public sealed record UpdateFlowMessageRequest(string Message);

/// <summary>Filter null = limpiar el filtro raíz.</summary>
public sealed record SetFlowRootFilterRequest(FilterGroupRequest? Filter);

public sealed record PreviewMessageRequest(
    Guid? DatasetRowId,
    IReadOnlyDictionary<string, string?>? SampleValues = null,
    MissingVariablePolicy MissingVariablePolicy = MissingVariablePolicy.Fail);

public sealed record FiltersPreviewRequest(FilterGroupRequest? Filter = null, int SampleLimit = 20);

/// <summary>Cambio de la columna telefónica de un dataset ya cargado.</summary>
public sealed record UpdateDatasetPhoneColumnRequest(string PhoneColumn);
