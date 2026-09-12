namespace MessageFlow.Application.Auth;

/// <summary>
/// Validación de tokens de Google OAuth. Implementada en Infrastructure
/// con Google.Apis.Auth.
/// </summary>
public interface IGoogleTokenValidator
{
    /// <summary>
    /// Valida un id_token de Google y devuelve la información del usuario.
    /// Lanza excepción si el token es inválido.
    /// </summary>
    Task<GoogleUserInfo> ValidateAsync(string idToken, CancellationToken ct = default);
}

public sealed record GoogleUserInfo(string Email, string ExternalId, string? DisplayName);
