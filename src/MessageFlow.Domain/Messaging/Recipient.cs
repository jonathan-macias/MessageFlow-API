using MessageFlow.Domain.Exceptions;

namespace MessageFlow.Domain.Messaging;

/// <summary>
/// Destinatario de un mensaje: a quién enviar (§31). El identificador de contacto
/// (p. ej. teléfono WhatsApp) proviene de la columna destinataria del dataset.
/// </summary>
public sealed record Recipient(Guid DatasetRowId, string Destination)
{
    public static Recipient Create(Guid datasetRowId, string? destination)
    {
        if (datasetRowId == Guid.Empty)
        {
            throw new Exceptions.DomainException("El destinatario debe referenciar una fila del dataset.");
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new MissingRecipientException();
        }

        return new Recipient(datasetRowId, destination.Trim());
    }
}
