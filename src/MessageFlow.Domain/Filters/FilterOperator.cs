namespace MessageFlow.Domain.Filters;

public enum FilterOperator
{
    // Comunes (todas las columnas)
    IsNull,
    IsNotNull,

    // Texto y números
    Equals,
    NotEquals,

    // Texto
    Contains,
    StartsWith,
    EndsWith,

    // Números
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,

    // Fechas
    Before,
    After,
    BeforeOrEqual,
    AfterOrEqual,
}
