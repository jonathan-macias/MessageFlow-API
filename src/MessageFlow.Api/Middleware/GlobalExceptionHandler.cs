using MessageFlow.Api.Observability;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Common;
using MessageFlow.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace MessageFlow.Api.Middleware;

/// <summary>
/// Mapeo central de excepciones a ProblemDetails (RFC 7807, §22). Los Controllers
/// nunca capturan excepciones: los mensajes del dominio son seguros para el cliente
/// (4xx) y los imprevistos se ocultan tras un 500 genérico.
/// </summary>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    RecentErrorsBuffer recentErrors) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, detail, extensions) = Map(exception);

        httpContext.Response.StatusCode = status;

        // Solo los 5xx son incidentes; los 4xx son errores del cliente y harían ruido.
        if (status >= StatusCodes.Status500InternalServerError)
        {
            recentErrors.Add(new RecentError(
                DateTimeOffset.UtcNow,
                httpContext.Request.Method,
                httpContext.Request.Path,
                status,
                exception.GetType().FullName ?? exception.GetType().Name,
                // El buffer lo consulta un tercero (Sentinel): se acota para no arrastrar datos de usuario.
                exception.Message.Length > 300 ? exception.Message[..300] : exception.Message,
                System.Diagnostics.Activity.Current?.TraceId.ToString()));
        }

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Instance = httpContext.Request.Path,
                Extensions = extensions,
            },
        });
    }

    private static (int Status, string Title, string? Detail, Dictionary<string, object?> Extensions) Map(Exception exception)
        => exception switch
        {
            ValidationFailedException validation => (
                StatusCodes.Status400BadRequest,
                "La solicitud no pasó la validación.",
                null,
                new Dictionary<string, object?>
                {
                    ["errors"] = validation.Failures
                        .GroupBy(f => f.PropertyName)
                        .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray()),
                    ["code"] = "validation_failed",
                }),

            // Estado transitorio del recurso, no un error de la solicitud: el cliente
            // debe poder reintentar sin que el usuario perciba un fallo suyo. El "code"
            // es lo que permite distinguirlo sin comparar el texto del mensaje.
            SemanticIndexingInProgressException indexing => (
                StatusCodes.Status409Conflict,
                "El índice semántico del dataset se está construyendo.",
                indexing.Message,
                new Dictionary<string, object?>
                {
                    ["code"] = SemanticIndexingInProgressException.ErrorCode,
                    ["datasetId"] = indexing.DatasetId,
                    ["status"] = indexing.Status,
                }),

            NotFoundException notFound => (
                StatusCodes.Status404NotFound,
                "Recurso no encontrado.",
                notFound.Message,
                EmptyExtensions()),

            // Transiciones de estado y duplicados son conflictos con el estado actual.
            InvalidFlowTransitionException or
            FlowNotModifiableException or
            FlowNotExecutableException or
            InvalidExecutionTransitionException or
            ClosedExecutionException or
            DuplicateExecutionItemException or
            DuplicateColumnException => (
                StatusCodes.Status409Conflict,
                "Conflicto con el estado actual del recurso.",
                exception.Message,
                EmptyExtensions()),

            DomainException domain => (
                StatusCodes.Status400BadRequest,
                "Solicitud inválida.",
                domain.Message,
                EmptyExtensions()),

            _ => (
                StatusCodes.Status500InternalServerError,
                "Error interno del servidor.",
                null,
                EmptyExtensions()),
        };

    private static Dictionary<string, object?> EmptyExtensions() => [];
}
