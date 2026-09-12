using MessageFlow.Application.Abstractions.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MessageFlow.Application.Abstractions.Messaging;

/// <summary>
/// Adaptador que permite despachar consultas cuyo tipo concreto solo se conoce en runtime,
/// manteniendo la validación tipada de FluentValidation dentro del wrapper genérico.
/// </summary>
internal abstract class QueryWrapperBase<TResponse>
{
    public abstract Task<TResponse> HandleAsync(IServiceProvider provider, CancellationToken cancellationToken);
}

internal sealed class QueryWrapper<TQuery, TResponse>(TQuery query) : QueryWrapperBase<TResponse>
    where TQuery : IQuery<TResponse>
{
    public override async Task<TResponse> HandleAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        await ValidationSupport.ValidateAsync(provider, query, cancellationToken);

        var handler = provider.GetRequiredService<IQueryHandler<TQuery, TResponse>>();
        return await handler.HandleAsync(query, cancellationToken);
    }
}
