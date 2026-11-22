using System.Text;
using MessageFlow.Api.Middleware;
using MessageFlow.Api.Observability;
using MessageFlow.Api.Security;
using MessageFlow.Application;
using MessageFlow.Application.Abstractions.Execution;
using MessageFlow.Application.AI;
using MessageFlow.Infrastructure;
using MessageFlow.Infrastructure.AI;
using MessageFlow.Infrastructure.Messaging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Application: CQRS propio, validadores FluentValidation, motor y scheduler.
builder.Services.AddMessageFlowApplication();

// Infrastructure: PostgreSQL (Npgsql), Identity, repositorios EF Core, almacenamiento de
// archivos, proveedor de WhatsApp y worker del scheduler.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Falta la cadena de conexión 'ConnectionStrings:Default'.");

builder.Services.AddMessageFlowInfrastructure(connectionString);

// Observabilidad: métricas OpenTelemetry (/metrics), salud de PostgreSQL (/health) y
// errores recientes (/ops/recent-errors) que consume Sentinel.
builder.Services.AddMessageFlowObservability(connectionString);

// La configuración puede ajustar el scheduler sin tocar código; la re-registración
// sustituye los valores por defecto registrados por Infrastructure.
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Scheduling").Get<FlowSchedulingOptions>() ?? new FlowSchedulingOptions());

// Credenciales/endpoint de Evolution API (WhatsApp): nunca hardcodeadas. Si faltan, la
// aplicación arranca igualmente (se avisa en el log al iniciar) y los envíos fallan con
// un error claro, en lugar de impedir el arranque o detener el scheduler.
var evolutionOptions = builder.Configuration.GetSection(EvolutionApiOptions.SectionName).Get<EvolutionApiOptions>()
    ?? new EvolutionApiOptions();
builder.Services.AddSingleton(evolutionOptions);

// Gemini AI service configuration.
var geminiOptions = builder.Configuration.GetSection(GeminiOptions.SectionName).Get<GeminiOptions>()
    ?? new GeminiOptions();

// Búsqueda semántica: límites de consulta y ritmo del worker de indexado.
var semanticSearchOptions = builder.Configuration
    .GetSection(SemanticSearchOptions.SectionName)
    .Get<SemanticSearchOptions>() ?? new SemanticSearchOptions();

var embeddingIndexingOptions = builder.Configuration
    .GetSection($"{SemanticSearchOptions.SectionName}:Indexing")
    .Get<EmbeddingIndexingOptions>() ?? new EmbeddingIndexingOptions();

if (semanticSearchOptions.Enabled)
{
    // Falla al arrancar, y no con un error 500 en la primera consulta, si la
    // configuración no tiene sentido o no coincide con el ancho de la columna vector.
    semanticSearchOptions.Validate();
    embeddingIndexingOptions.Validate();
    geminiOptions.Validate();
}

// La re-registración sustituye los valores por defecto registrados por Application/Infrastructure.
builder.Services.AddSingleton(geminiOptions);
builder.Services.AddSingleton(semanticSearchOptions);
builder.Services.AddSingleton(embeddingIndexingOptions);

// ── Autenticación (JWT + Google) ──────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Falta la configuración 'Jwt:Secret'.");

// HS256 firma con una clave simétrica: una corta se adivina por fuerza bruta y permitiría falsificar tokens.
const int MinJwtSecretBytes = 32;
if (Encoding.UTF8.GetByteCount(jwtSecret) < MinJwtSecretBytes)
{
    throw new InvalidOperationException(
        $"'Jwt:Secret' debe tener al menos {MinJwtSecretBytes} bytes. Genera uno con: openssl rand -hex 32");
}

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    })
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"]
            ?? throw new InvalidOperationException("Falta 'Authentication:Google:ClientId'.");
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]
            ?? throw new InvalidOperationException("Falta 'Authentication:Google:ClientSecret'.");
        options.SaveTokens = false;
    });

// ── Autorización: fallback policy = todos los endpoints requieren auth por defecto ──
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
        JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build());

// ── Límite de peticiones (auth por IP; IA y envíos por usuario) ─────────────────
builder.Services.AddMessageFlowRateLimiting();

// ── CORS para frontend React en desarrollo ──────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:5173", "https://localhost:7110", "http://localhost:3001"];

        policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// ── Controllers + OpenAPI + ProblemDetails ──────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();

if (string.IsNullOrWhiteSpace(evolutionOptions.BaseUrl)
    || string.IsNullOrWhiteSpace(evolutionOptions.Instance)
    || string.IsNullOrWhiteSpace(evolutionOptions.GlobalApiKey))
{
    app.Logger.LogWarning(
        "Evolution API no está configurada (EvolutionApi:BaseUrl, Instance y GlobalApiKey): " +
        "la aplicación arranca, pero los envíos de WhatsApp fallarán hasta que se configure.");
}

// La documentación de la API (OpenAPI + Swagger UI) solo se publica en Development o si se
// activa explícitamente con Swagger:Enabled=true.
var documentationEnabled = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled");

// ── Pipeline middleware ─────────────────────────────────────────────────────────
app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
{
    // Cabecera HSTS (solo sobre HTTPS; detrás del túnel el esquema llega por X-Forwarded-Proto).
    app.UseHsts();
}

app.UseHttpsRedirection();

if (!documentationEnabled)
{
    // Debe ir antes de los archivos estáticos: index.html es el Swagger UI.
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;

        if (path == "/" || path.StartsWithSegments("/index.html") || path.StartsWithSegments("/openapi"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
    });
}

app.UseMessageFlowMetricsProtection();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

// Después de la autenticación: las políticas por usuario necesitan el id del usuario.
app.UseRateLimiter();

if (documentationEnabled)
{
    app.MapOpenApi().AllowAnonymous();

    // Swagger UI served from wwwroot/index.html via UseStaticFiles above.
}

app.MapControllers();

app.MapMessageFlowObservability();

app.Run();
