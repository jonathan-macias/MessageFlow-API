namespace MessageFlow.Domain.Exceptions;

public sealed class InvalidTemplateException(string message)
    : DomainException(message)
{
}

public sealed class MissingTemplateVariableException(string variableName)
    : DomainException($"The variable '{variableName}' has no value in the current record.")
{
    public string VariableName { get; } = variableName;
}

public sealed class UnknownTemplateVariablesException(IReadOnlyList<string> variableNames)
    : DomainException(BuildMessage(variableNames))
{
    public IReadOnlyList<string> VariableNames { get; } = variableNames;

    private static string BuildMessage(IReadOnlyList<string> names)
        => $"The variables {string.Join(", ", names.Select(WrapVariable))} do not exist as dataset columns.";

    private static string WrapVariable(string name) => "{{" + name + "}}";
}

public sealed class MissingRecipientException()
    : DomainException("The recipient cannot be empty: check the dataset's recipient column.")
{
}
