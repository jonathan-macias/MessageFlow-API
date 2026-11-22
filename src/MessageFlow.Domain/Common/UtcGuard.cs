using System.Runtime.CompilerServices;

namespace MessageFlow.Domain.Common;

internal static class UtcGuard
{
    public static DateTimeOffset RequireUtc(
        this DateTimeOffset value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
        => value.Offset == TimeSpan.Zero
            ? value
            : throw new Exceptions.DomainException($"The value '{paramName}' must be expressed in UTC (zero offset).");
}
