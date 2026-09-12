using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;

namespace MessageFlow.Application.Abstractions.Messaging;

/// <summary>
/// Ejecuta todos los IValidator&lt;T&gt; registrados en el contenedor y agrega
/// los fallos en una sola <see cref="ValidationFailedException"/>.
/// </summary>
internal static class ValidationSupport
{
    internal static async Task ValidateAsync<T>(
        IServiceProvider serviceProvider,
        T message,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var validators = serviceProvider.GetServices<IValidator<T>>();
        if (!validators.Any())
        {
            return;
        }

        var failures = new List<ValidationFailure>();

        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(message, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count > 0)
        {
            throw new ValidationFailedException(failures);
        }
    }
}
