using System.Security.Claims;
using MessageFlow.Application.Abstractions;
using Microsoft.AspNetCore.Http;

namespace MessageFlow.Infrastructure.CurrentUser;

/// <summary>
/// Resuelve la identidad del usuario desde HttpContext.User (JWT claims).
/// Se registra como Scoped para que cada request HTTP tenga su propia instancia.
/// En background se usa SystemCurrentUser (UserId = null, "system" en auditoría).
/// </summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    public string? UserId => httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);

    public string? Email => httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Email);

    public bool IsAuthenticated => httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated ?? false;
}
