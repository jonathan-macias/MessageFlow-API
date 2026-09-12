using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MessageFlow.Application.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace MessageFlow.Infrastructure.Users;

/// <summary>
/// Generación de JWT para autenticación. Configurado desde appsettings.json
/// (Jwt:Issuer, Jwt:Audience, Jwt:Secret, Jwt:ExpirationMinutes).
/// Los secretos NUNCA se hardcodean — se leen de configuración.
/// </summary>
internal sealed class TokenService(IConfiguration configuration) : ITokenService
{
    public (string AccessToken, DateTimeOffset ExpiresAt) GenerateToken(
        Guid userId, string email, IReadOnlyList<string>? roles = null)
    {
        var secret = configuration["Jwt:Secret"]
            ?? throw new InvalidOperationException("Falta la configuración 'Jwt:Secret'.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expirationMinutes = int.TryParse(configuration["Jwt:ExpirationMinutes"], out var minutes)
            ? minutes
            : 60;

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(expirationMinutes);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (roles is not null)
        {
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
        }

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        var tokenHandler = new JwtSecurityTokenHandler();
        return (tokenHandler.WriteToken(token), expiresAt);
    }
}
