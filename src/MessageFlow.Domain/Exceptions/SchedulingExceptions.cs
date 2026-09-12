namespace MessageFlow.Domain.Exceptions;

public sealed class UnsupportedTimeZoneException(string message)
    : DomainException(message)
{
}

public sealed class InvalidRecurrenceException(string message)
    : DomainException(message)
{
}
