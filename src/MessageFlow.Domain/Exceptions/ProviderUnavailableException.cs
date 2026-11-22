using MessageFlow.Domain.Enums;

namespace MessageFlow.Domain.Exceptions;

/// <summary>
/// El proveedor del canal (p. ej. Evolution API) falló de forma transitoria varias veces
/// seguidas: la corrida se detiene en lugar de intentar cada fila restante contra un
/// servicio caído. La ejecución queda como Failed con este mensaje.
/// </summary>
public sealed class ProviderUnavailableException(Channel channel, int consecutiveFailures, long lastRowNumber)
    : DomainException(
        $"El proveedor del canal '{channel}' no está disponible: {consecutiveFailures} fallos consecutivos. " +
        $"La ejecución se detuvo en la fila {lastRowNumber}; vuelve a ejecutarla cuando el servicio se recupere.")
{
    public Channel Channel { get; } = channel;

    public int ConsecutiveFailures { get; } = consecutiveFailures;

    public long LastRowNumber { get; } = lastRowNumber;
}
