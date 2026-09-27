using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Execution;
using MessageFlow.Application.Abstractions.Files;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.AI;
using MessageFlow.Application.Auth;
using MessageFlow.Application.Datasets;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Messaging;
using MessageFlow.Infrastructure.AI;
using MessageFlow.Infrastructure.CurrentUser;
using MessageFlow.Infrastructure.Messaging;
using MessageFlow.Infrastructure.Persistence;
using MessageFlow.Infrastructure.Persistence.Files;
using MessageFlow.Infrastructure.Persistence.Repositories;
using MessageFlow.Infrastructure.Scheduling;
using MessageFlow.Infrastructure.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector.EntityFrameworkCore;

namespace MessageFlow.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registra PostgreSQL (Npgsql), ASP.NET Core Identity, DbContext con interceptor
    /// de auditoría, repositorios de Application, lectura Excel y almacenamiento de archivos.
    /// </summary>
    public static IServiceCollection AddMessageFlowInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton(DatasetImportOptions.Default);
        services.AddSingleton<IExcelReaderFactory, MiniExcelReaderFactory>();
        services.AddSingleton<IDatasetFileStorage, LocalDiskDatasetFileStorage>();

        // Canal WhatsApp real vía Evolution API.
        services.AddSingleton(new Messaging.EvolutionApiOptions());
        services.AddHttpClient<IMessageProvider, Messaging.EvolutionApiClient>();

        // AI Message Generator: Gemini API for generating WhatsApp message templates.
        services.AddSingleton(new GeminiOptions());
        services.AddHttpClient<IGenerativeAIService, GeminiService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        // AI semantic search (RAG): same provider, embeddings endpoint.
        services.AddHttpClient<IEmbeddingService, GeminiEmbeddingService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
        });

        // ICurrentUser: resuelve dinámicamente según contexto.
        // - HttpContextCurrentUser cuando hay request HTTP (API).
        // - SystemCurrentUser cuando no hay HttpContext (background workers).
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser>(sp =>
        {
            var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
            if (httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true)
            {
                return new HttpContextCurrentUser(httpContextAccessor);
            }
            return new SystemCurrentUser();
        });

        // ASP.NET Core Identity: UserManager + SignInManager + roles + stores EF Core.
        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = false;
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = false;
        })
        .AddEntityFrameworkStores<MessageFlowDbContext>()
        .AddDefaultTokenProviders();

        // Auth services: AccountService, TokenService, GoogleTokenValidator.
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IGoogleTokenValidator, GoogleTokenValidator>();

        services.AddScoped<AuditSaveChangesInterceptor>();

        services.AddDbContext<MessageFlowDbContext>((serviceProvider, options) =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsAssembly(typeof(DependencyInjection).Assembly.FullName);

                // Habilita el mapeo float[] <-> vector(n) que usa pgvector.
                npgsqlOptions.UseVector();
            });
            options.AddInterceptors(serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>());
        });

        services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<MessageFlowDbContext>());
        services.AddScoped<IFlowRepository, FlowRepository>();
        services.AddScoped<IDatasetRepository, DatasetRepository>();
        services.AddScoped<IDatasetEmbeddingRepository, DatasetEmbeddingRepository>();
        services.AddScoped<IFlowExecutionRepository, FlowExecutionRepository>();

        // Scheduler en background (Fase 8).
        services.AddSingleton(new FlowSchedulingOptions());
        services.AddHostedService<FlowSchedulerWorker>();

        // Indexado de embeddings en background. Siempre se registra; si está
        // deshabilitado o falta la API key, el worker no hace nada.
        services.AddSingleton(new EmbeddingIndexingOptions());
        services.AddHostedService<DatasetEmbeddingWorker>();

        return services;
    }
}
