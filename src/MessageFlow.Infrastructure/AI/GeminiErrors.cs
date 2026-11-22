using System.Net;

namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Mensajes de error de Gemini aptos para el cliente. El cuerpo crudo de la respuesta (que puede
/// traer detalles del proyecto o de la cuenta) solo se escribe en el log, nunca en la respuesta HTTP.
/// </summary>
internal static class GeminiErrors
{
    public static string ToClientMessage(HttpStatusCode status)
        => status == HttpStatusCode.TooManyRequests
            ? "The AI service reached its usage limit. Try again in a few minutes."
            : $"The AI service returned an error ({(int)status}). Try again later.";
}
