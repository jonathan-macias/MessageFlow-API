using Microsoft.Extensions.DependencyInjection;

namespace MessageFlow.Application.Abstractions.Messaging;

/// <inheritdoc />
internal sealed class Dispatcher(IServiceProvider provider) : IDispatcher
{
    public async Task SendAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(command);
        await ValidationSupport.ValidateAsync(provider, command, cancellationToken);

        var handler = provider.GetRequiredService<ICommandHandler<TCommand>>();
        await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<TResponse> SendAsync<TCommand, TResponse>(
        TCommand command,
        CancellationToken cancellationToken = default)
        where TCommand : ICommand<TResponse>
    {
        ArgumentNullException.ThrowIfNull(command);
        await ValidationSupport.ValidateAsync(provider, command, cancellationToken);

        var handler = provider.GetRequiredService<ICommandHandler<TCommand, TResponse>>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var wrapperType = typeof(QueryWrapper<,>).MakeGenericType(query.GetType(), typeof(TResponse));
        var wrapper = (QueryWrapperBase<TResponse>)Activator.CreateInstance(wrapperType, query)!;
        return wrapper.HandleAsync(provider, cancellationToken);
    }
}
