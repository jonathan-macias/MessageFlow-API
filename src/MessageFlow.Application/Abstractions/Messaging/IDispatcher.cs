namespace MessageFlow.Application.Abstractions.Messaging;

/// <summary>
/// Dispatcher CQRS propio (decisión aprobada: sin MediatR).
/// Ejecuta automáticamente los validadores de FluentValidation registrados
/// para el tipo del mensaje antes de invocar el handler correspondiente.
/// </summary>
public interface IDispatcher
{
    /// <summary>Envía un comando sin respuesta.</summary>
    Task SendAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand;

    /// <summary>Envía un comando con respuesta.</summary>
    Task<TResponse> SendAsync<TCommand, TResponse>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand<TResponse>;

    /// <summary>Envía una consulta de solo lectura.</summary>
    Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default);
}
