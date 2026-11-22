using Google.Apis.Auth;
using MessageFlow.Application.Auth;
using MessageFlow.Domain.Exceptions;
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
            ?? throw new InvalidOperationException("Missing configuration 'Authentication:Google:ClientId'.");

        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = [clientId],
        };

        GoogleJsonWebSignature.Payload payload;

        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
        }
        catch (InvalidJwtException)
        {
            // Un token mal formado o caducado es un error del cliente (400), no un fallo del servidor.
            throw new DomainException("The Google token is not valid.");
        }

        return ToUserInfo(payload);
    }

    /// <summary>
    /// El correo solo sirve como identidad si Google lo marca como verificado: de lo
    /// contrario se podría enlazar la sesión a una cuenta ajena con ese mismo email.
    /// </summary>
    internal static GoogleUserInfo ToUserInfo(GoogleJsonWebSignature.Payload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Email) || !payload.EmailVerified)
        {
            throw new DomainException("The Google account's email is not verified.");
        }

        return new GoogleUserInfo(
            Email: payload.Email,
            ExternalId: payload.Subject,
            DisplayName: payload.Name);
    }
}
