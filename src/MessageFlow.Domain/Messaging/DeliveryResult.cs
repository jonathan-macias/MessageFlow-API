namespace MessageFlow.Domain.Messaging;

/// <summary>
/// Resultado de la entrega de un mensaje por parte del proveedor.
/// <paramref name="IsTransient"/> distingue un fallo de infraestructura (proveedor caído,
/// sin configurar, sin red) de uno propio del destinatario (por ejemplo, número inválido):
/// el motor solo detiene la corrida por los primeros.
/// </summary>
public sealed record DeliveryResult(bool IsSuccess, string? ProviderMessageId, string? Error, bool IsTransient = false)
{
    public static DeliveryResult Success(string? providerMessageId) => new(true, providerMessageId, null);

    public static DeliveryResult Failure(string? error, bool isTransient = false) => new(false, null, error, isTransient);
}
