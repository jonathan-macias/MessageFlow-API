using MessageFlow.Domain.Enums;

namespace MessageFlow.Domain.Exceptions;

/// <summary>
/// El proveedor del canal (p. ej. Evolution API) falló de forma transitoria varias veces
/// seguidas: la corrida se detiene en lugar de intentar cada fila restante contra un
/// servicio caído. La ejecución queda como Failed con este mensaje.
/// </summary>
public sealed class ProviderUnavailableException(Channel channel, int consecutiveFailures, long lastRowNumber)
    : DomainException(
        $"The provider for channel '{channel}' is unavailable: {consecutiveFailures} consecutive failures. " +
        $"The execution stopped at row {lastRowNumber}; run it again once the service has recovered.")
{
    public Channel Channel { get; } = channel;

    public int ConsecutiveFailures { get; } = consecutiveFailures;

    public long LastRowNumber { get; } = lastRowNumber;
}
