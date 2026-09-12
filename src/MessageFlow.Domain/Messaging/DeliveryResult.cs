namespace MessageFlow.Domain.Messaging;

/// <summary>Resultado de la entrega de un mensaje por parte del proveedor.</summary>
public sealed record DeliveryResult(bool IsSuccess, string? ProviderMessageId, string? Error)
{
    public static DeliveryResult Success(string? providerMessageId) => new(true, providerMessageId, null);

    public static DeliveryResult Failure(string? error) => new(false, null, error);
}
