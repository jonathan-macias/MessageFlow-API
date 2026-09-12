using System.Runtime.CompilerServices;

namespace MessageFlow.Domain.Common;

internal static class UtcGuard
{
    public static DateTimeOffset RequireUtc(
        this DateTimeOffset value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
        => value.Offset == TimeSpan.Zero
            ? value
            : throw new Exceptions.DomainException($"El valor '{paramName}' debe expresarse en UTC (offset cero).");
}
