using MessageFlow.Domain.Enums;

namespace MessageFlow.Domain.Datasets;

/// <summary>
/// Metadatos de una columna del dataset, proyectados para validar plantillas y filtros
/// sin cargar entidades completas.
/// </summary>
public sealed record ColumnDefinition(Guid Id, string Name, ColumnDataType DataType);
