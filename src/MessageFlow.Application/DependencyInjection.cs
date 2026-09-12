using System.Reflection;
using FluentValidation;
using MessageFlow.Application.Abstractions.Execution;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.AI;
using MessageFlow.Application.Executions;
using Microsoft.Extensions.DependencyInjection;

namespace MessageFlow.Application;

/// <summary>
/// Punto único de registro de la capa Application en el contenedor de DI.
/// Escanea el ensamblado registrando handlers de CQRS y validadores de FluentValidation.
/// </summary>
public static class DependencyInjection
{
    private static readonly IReadOnlyList<Type> HandlerInterfaces =
    [
        typeof(ICommandHandler<>),
        typeof(ICommandHandler<,>),
        typeof(IQueryHandler<,>),
    ];

    public static IServiceCollection AddMessageFlowApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new FlowExecutionEngineOptions());
        services.AddSingleton(new FlowSchedulingOptions());
        services.AddScoped<IFlowExecutionEngine, FlowExecutionEngine>();
        services.AddScoped<IScheduledExecutionProcessor, ScheduledExecutionProcessor>();
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddScoped<DatasetQueryExecutor>();
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);
        RegisterHandlers(services, assembly);

        return services;
    }

    private static void RegisterHandlers(IServiceCollection services, Assembly assembly)
    {
        var concreteTypes = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false });

        foreach (var type in concreteTypes)
        {
            foreach (var @interface in type.GetInterfaces())
            {
                if (@interface.IsGenericType && HandlerInterfaces.Contains(@interface.GetGenericTypeDefinition()))
                {
                    services.AddTransient(@interface, type);
                }
            }
        }
    }
}
