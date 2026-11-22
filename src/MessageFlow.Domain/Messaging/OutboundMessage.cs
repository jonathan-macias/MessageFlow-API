using MessageFlow.Domain.Enums;

namespace MessageFlow.Domain.Messaging;

/// <summary>
/// Mensaje ya renderizado y listo para entregar (§32). Contiene QUÉ enviar (cuerpo)
/// y A QUIÉN (destinatario); el CÓMO lo resuelve el <see cref="IMessageProvider"/>.
/// </summary>
public sealed record OutboundMessage(Recipient Recipient, string Body, Channel Channel)
{
    public static OutboundMessage Create(Recipient recipient, string? body, Channel channel)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new Exceptions.DomainException("The message body cannot be empty.");
        }

        if (!Enum.IsDefined(channel))
        {
            throw new Exceptions.DomainException($"Unknown channel: '{channel}'.");
        }

        return new OutboundMessage(recipient, body.Trim(), channel);
    }
}
