namespace MessageFlow.Domain.Exceptions;

/// <summary>
/// Excepción base de reglas de negocio. La capa API la mapea a ProblemDetails (422/409).
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
