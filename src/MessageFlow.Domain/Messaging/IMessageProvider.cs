using MessageFlow.Domain.Enums;

namespace MessageFlow.Domain.Messaging;

/// <summary>
/// Abstracción del canal de envío (§30): desacopla el núcleo del Flow de WhatsApp
/// u otro proveedor. Las implementaciones viven en Infrastructure; el dominio solo
/// conoce esta interfaz. Un Flow selecciona su proveedor por <see cref="Channel"/>.
/// </summary>
public interface IMessageProvider
{
    /// <summary>Canal que atiende este proveedor (WhatsApp, y en el futuro Email/Sms).</summary>
    Channel Channel { get; }

    /// <summary>Entrega un mensaje renderizado. Debe ser idempotente cuando el proveedor lo permita.</summary>
    Task<DeliveryResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);
}
