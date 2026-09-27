namespace MessageFlow.Domain.AI;

/// <summary>
/// Fila recuperada por búsqueda vectorial que se entrega al modelo de lenguaje
/// para redactar la respuesta. Incluye la similitud para que el modelo pueda
/// calibrar qué tanto confiar de cada registro.
/// </summary>
public sealed record GroundedRow(
    long RowNumber,
    double Similarity,
    IReadOnlyDictionary<string, string?> Values);

/// <summary>
/// Filtro estructurado combinado con la búsqueda semántica. Lo envía el usuario de
/// forma explícita (no lo inventa la IA), y se valida contra el catálogo de columnas
/// antes de tocar la base de datos.
/// </summary>
public sealed record GroundedFilter(
    string Column,
    string Operator,
    string? Value);
