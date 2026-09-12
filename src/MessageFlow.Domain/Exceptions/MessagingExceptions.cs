namespace MessageFlow.Domain.Exceptions;

public sealed class InvalidTemplateException(string message)
    : DomainException(message)
{
}

public sealed class MissingTemplateVariableException(string variableName)
    : DomainException($"La variable '{variableName}' no tiene valor en el registro actual.")
{
    public string VariableName { get; } = variableName;
}

public sealed class UnknownTemplateVariablesException(IReadOnlyList<string> variableNames)
    : DomainException(BuildMessage(variableNames))
{
    public IReadOnlyList<string> VariableNames { get; } = variableNames;

    private static string BuildMessage(IReadOnlyList<string> names)
        => $"Las variables {string.Join(", ", names.Select(WrapVariable))} no existen como columnas del dataset.";

    private static string WrapVariable(string name) => "{{" + name + "}}";
}

public sealed class MissingRecipientException()
    : DomainException("El destinatario no puede estar vacío: verifique la columna destinataria del dataset.")
{
}
