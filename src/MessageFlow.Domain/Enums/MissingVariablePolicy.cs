namespace MessageFlow.Domain.Enums;

public enum MissingVariablePolicy
{
    /// <summary> Lanza MissingTemplateVariableException (comportamiento por defecto). </summary>
    Fail = 1,

    /// <summary> Deja el placeholder original sin resolver. </summary>
    LeavePlaceholder = 2,

    /// <summary> Sustituye la variable por cadena vacía. </summary>
    Empty = 3,
}
