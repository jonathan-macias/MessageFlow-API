using FluentValidation.Results;
using MessageFlow.Application.Abstractions.Messaging;

namespace MessageFlow.Application.Abstractions.Messaging;

/// <summary>Handler para comandos sin respuesta.</summary>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Handler para comandos con respuesta.</summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<TResponse> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Handler para consultas de solo lectura.</summary>
public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<TResponse> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Errores de validación agregados (uno o más validadores fallidos).
/// La API lo mapea a ProblemDetails 400/422 en la fase de endpoints.
/// </summary>
public sealed class ValidationFailedException(IReadOnlyList<ValidationFailure> failures)
    : Exception("La solicitud no pasó la validación.")
{
    public IReadOnlyList<ValidationFailure> Failures { get; } = failures;
}
