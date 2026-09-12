using Google.Apis.Auth;
using MessageFlow.Application.Auth;
using Microsoft.Extensions.Configuration;

namespace MessageFlow.Infrastructure.Users;

/// <summary>
/// Validación de id_tokens de Google OAuth usando Google.Apis.Auth.
/// Requiere Google:ClientId en configuración para validar el audience.
/// </summary>
internal sealed class GoogleTokenValidator(IConfiguration configuration) : IGoogleTokenValidator
{
    public async Task<GoogleUserInfo> ValidateAsync(string idToken, CancellationToken ct = default)
    {
        var clientId = configuration["Authentication:Google:ClientId"]
            ?? throw new InvalidOperationException("Falta la configuración 'Authentication:Google:ClientId'.");

        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = [clientId],
        };

        var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);

        return new GoogleUserInfo(
            Email: payload.Email,
            ExternalId: payload.Subject,
            DisplayName: payload.Name);
    }
}
