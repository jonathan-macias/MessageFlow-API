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
using Microsoft.Extensions.Logging;
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

        // AI text generation (message templates, dataset queries, grounded answers).
        // Strategy: one ILlmClient per provider (Gemini, OpenAI); the configured primary is wrapped
        // by FallbackLlmClient, which retries once and then switches to the fallback provider.
        // Prompts and answer validation live in LlmGenerativeAIService, shared by every provider.
        services.AddSingleton(new GeminiOptions());
        services.AddSingleton(new OpenAiOptions());
        services.AddSingleton(new AiOptions());

        services.AddHttpClient<GeminiLlmClient>(client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddHttpClient<OpenAiLlmClient>(client => client.Timeout = TimeSpan.FromSeconds(60));

        services.AddTransient<IGenerativeAIService>(serviceProvider =>
        {
            var aiOptions = serviceProvider.GetRequiredService<AiOptions>();

            ILlmClient Resolve(string provider) => provider == AiOptions.OpenAi
                ? serviceProvider.GetRequiredService<OpenAiLlmClient>()
                : serviceProvider.GetRequiredService<GeminiLlmClient>();

            var primary = Resolve(aiOptions.NormalizedProvider);
            var secondary = aiOptions.NormalizedFallbackProvider is { } fallback ? Resolve(fallback) : null;

            return new LlmGenerativeAIService(new FallbackLlmClient(
                primary,
                secondary,
                serviceProvider.GetRequiredService<ILogger<FallbackLlmClient>>()));
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
            var httpContext = httpContextAccessor.HttpContext;

            if (httpContext?.User?.Identity?.IsAuthenticated == true)
            {
                return new HttpContextCurrentUser(httpContextAccessor);
            }

            // Sin HttpContext es un proceso interno (scheduler, indexado). Con HttpContext pero sin
            // usuario autenticado es una petición anónima: nunca debe ver datos de otros usuarios.
            return httpContext is null ? new SystemCurrentUser() : new AnonymousCurrentUser();
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

            // Bloqueo temporal tras intentos fallidos (se combina con el límite por IP del login).
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
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
