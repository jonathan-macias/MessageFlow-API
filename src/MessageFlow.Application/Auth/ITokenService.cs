namespace MessageFlow.Application.Auth;

/// <summary>
/// Generación de tokens JWT para la API. Implementada en Infrastructure
/// con System.IdentityModel.Tokens.Jwt.
/// </summary>
public interface ITokenService
{
    /// <summary>Genera un JWT para el usuario especificado.</summary>
    (string AccessToken, DateTimeOffset ExpiresAt) GenerateToken(Guid userId, string email, IReadOnlyList<string>? roles = null);
}
