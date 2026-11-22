using MessageFlow.Api.Security;
using MessageFlow.Application.Abstractions.Messaging;
using MessageFlow.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MessageFlow.Api.Controllers;

/// <summary>
/// Autenticación y registro de usuarios. Los endpoints de register y login son
/// públicos (AllowAnonymous); el callback de Google también.
/// El frontend utiliza el JWT devuelto por login/Google para llamar al resto de la API.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>
    /// Registra un usuario nuevo con email y contraseña.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthRegister)]
    [ProducesResponseType(typeof(RegisterUserResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register([FromBody] RegisterUserCommand command, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync<RegisterUserCommand, RegisterUserResult>(command, cancellationToken);
        return CreatedAtAction(null, new { id = result.UserId }, result);
    }

    /// <summary>
    /// Inicia sesión con email y contraseña. Devuelve un JWT para usar en el
    /// header Authorization de las peticiones subsiguientes.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthLogin)]
    [ProducesResponseType(typeof(LoginUserResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginUserCommand command, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync<LoginUserCommand, LoginUserResult>(command, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Login/registro con Google OAuth. El frontend envía el id_token obtenido
    /// del flujo de Google; el backend lo valida y devuelve nuestro JWT.
    /// </summary>
    [HttpPost("google")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthLogin)]
    [ProducesResponseType(typeof(LoginUserResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginCommand command, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync<GoogleLoginCommand, LoginUserResult>(command, cancellationToken);
        return Ok(result);
    }
}
