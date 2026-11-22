using System.Security.Claims;
using System.Threading.RateLimiting;

namespace MessageFlow.Api.Security;

/// <summary>Nombres de las políticas de límite de peticiones (usados en [EnableRateLimiting]).</summary>
public static class RateLimitPolicies
{
    /// <summary>Login: frena la fuerza bruta de contraseñas. Por IP.</summary>
    public const string AuthLogin = "auth-login";

    /// <summary>Registro y login con Google: frena la creación masiva de cuentas. Por IP.</summary>
    public const string AuthRegister = "auth-register";

    /// <summary>Endpoints de IA (consumen cuota de Gemini). Por usuario.</summary>
    public const string Ai = "ai";

    /// <summary>Disparo manual de un Flow ("Send Now"). Por usuario.</summary>
    public const string Execute = "execute";
}

public static class RateLimitingExtensions
{
    /// <summary>
    /// Límites por IP en autenticación y por usuario en IA y envíos. La IP real llega por
    /// X-Forwarded-For (ASPNETCORE_FORWARDEDHEADERS_ENABLED en el despliegue detrás del túnel).
    /// Las respuestas rechazadas son 429.
    /// </summary>
    public static IServiceCollection AddMessageFlowRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(RateLimitPolicies.AuthLogin, http =>
                FixedWindow($"login:{ClientIp(http)}", permitLimit: 5, TimeSpan.FromMinutes(1)));

            options.AddPolicy(RateLimitPolicies.AuthRegister, http =>
                FixedWindow($"register:{ClientIp(http)}", permitLimit: 3, TimeSpan.FromHours(1)));

            options.AddPolicy(RateLimitPolicies.Ai, http =>
                FixedWindow($"ai:{UserOrIp(http)}", permitLimit: 20, TimeSpan.FromMinutes(1)));

            options.AddPolicy(RateLimitPolicies.Execute, http =>
                FixedWindow($"execute:{UserOrIp(http)}", permitLimit: 10, TimeSpan.FromMinutes(1)));
        });

        return services;
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permitLimit, TimeSpan window)
        => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
        });

    private static string ClientIp(HttpContext http)
        => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string UserOrIp(HttpContext http)
        => http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ClientIp(http);
}
