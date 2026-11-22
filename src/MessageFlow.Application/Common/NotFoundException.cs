namespace MessageFlow.Application.Common;

/// <summary>El recurso solicitado no existe. La API lo mapea a 404.</summary>
public sealed class NotFoundException(string resource, object key)
    : Exception($"{resource} with identifier '{key}' was not found.")
{
    public string Resource { get; } = resource;

    public object Key { get; } = key;
}
