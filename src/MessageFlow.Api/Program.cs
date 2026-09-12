using System.Text;
using MessageFlow.Api.Middleware;
using MessageFlow.Application;
using MessageFlow.Application.Abstractions.Execution;
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

// La configuración puede ajustar el scheduler sin tocar código; la re-registración
// sustituye los valores por defecto registrados por Infrastructure.
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Scheduling").Get<FlowSchedulingOptions>() ?? new FlowSchedulingOptions());

// Credenciales/endpoint de Evolution API (WhatsApp): nunca hardcodeadas.
builder.Services.AddSingleton(
    builder.Configuration.GetSection(EvolutionApiOptions.SectionName).Get<EvolutionApiOptions>()
    ?? throw new InvalidOperationException("Falta la sección 'EvolutionApi' en la configuración."));

// Gemini AI service configuration.
builder.Services.AddSingleton(
    builder.Configuration.GetSection(GeminiOptions.SectionName).Get<GeminiOptions>()
    ?? new GeminiOptions());

// ── Autenticación (JWT + Google) ──────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? throw new InvalidOperationException("Falta la configuración 'Jwt:Secret'.");

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

// ── Pipeline middleware ─────────────────────────────────────────────────────────
app.UseExceptionHandler();

app.UseHttpsRedirection();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

//if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();

    // Swagger UI served from wwwroot/index.html via UseStaticFiles above.
}

app.MapControllers();

app.Run();
