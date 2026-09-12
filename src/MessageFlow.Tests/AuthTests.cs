using FluentValidation;
using MessageFlow.Application.Auth;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Tests.TestSupport;

namespace MessageFlow.Tests;

/// <summary>
/// Tests de autenticación: validación de entrada, registro, login, JWT y Google OAuth.
/// Usa fakes en memoria (sin Identity real) para aislar la lógica de negocio.
/// </summary>
public class AuthTests
{
    // ── Validación de registro ──────────────────────────────────────────────
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public async Task Register_rechaza_email_invalido(string email)
    {
        var validator = new RegisterUserCommandValidator();
        var result = await validator.ValidateAsync(new RegisterUserCommand(email, "Password123!"));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Email");
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("nouppercase1!")]
    [InlineData("NOLOWERCASE1!")]
    [InlineData("NoNumber!")]
    public async Task Register_rechaza_password_debil(string password)
    {
        var validator = new RegisterUserCommandValidator();
        var result = await validator.ValidateAsync(new RegisterUserCommand("user@test.com", password));
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Password");
    }

    [Fact]
    public async Task Register_acepta_email_y_password_validos()
    {
        var validator = new RegisterUserCommandValidator();
        var result = await validator.ValidateAsync(new RegisterUserCommand("user@test.com", "Password123!"));
        Assert.True(result.IsValid);
    }

    // ── Validación de login ────────────────────────────────────────────────
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Login_rechaza_email_vacio(string email)
    {
        var validator = new LoginUserCommandValidator();
        var result = await validator.ValidateAsync(new LoginUserCommand(email, "Password123!"));
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Login_rechaza_password_vacio()
    {
        var validator = new LoginUserCommandValidator();
        var result = await validator.ValidateAsync(new LoginUserCommand("user@test.com", ""));
        Assert.False(result.IsValid);
    }

    // ── Handler de registro ────────────────────────────────────────────────
    [Fact]
    public async Task Register_handler_crea_usuario_y_devuelve_id()
    {
        var accountService = new FakeAccountService();
        var handler = new RegisterUserHandler(accountService);

        var result = await handler.HandleAsync(
            new RegisterUserCommand("new@test.com", "Password123!"));

        Assert.NotEqual(Guid.Empty, result.UserId);
        Assert.Equal("new@test.com", result.Email);
    }

    [Fact]
    public async Task Register_handler_falla_si_el_email_ya_existe()
    {
        var accountService = new FakeAccountService();
        await accountService.RegisterAsync("exists@test.com", "Password123!");
        var handler = new RegisterUserHandler(accountService);

        await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(new RegisterUserCommand("exists@test.com", "Password123!")));
    }

    // ── Handler de login ───────────────────────────────────────────────────
    [Fact]
    public async Task Login_handler_devuelve_jwt_con_datos_del_usuario()
    {
        var accountService = new FakeAccountService();
        var reg = await accountService.RegisterAsync("user@test.com", "Password123!");
        var tokenService = new FakeTokenService();
        var handler = new LoginUserHandler(accountService, tokenService);

        var result = await handler.HandleAsync(
            new LoginUserCommand("user@test.com", "Password123!"));

        Assert.StartsWith("fake-jwt-", result.AccessToken);
        Assert.Equal(reg.UserId, result.UserId);
        Assert.Equal("user@test.com", result.Email);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Login_handler_falla_con_credenciales_incorrectas()
    {
        var accountService = new FakeAccountService();
        await accountService.RegisterAsync("user@test.com", "Password123!");
        var handler = new LoginUserHandler(accountService, new FakeTokenService());

        await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(new LoginUserCommand("user@test.com", "WrongPassword!")));
    }

    [Fact]
    public async Task Login_handler_falla_si_el_usuario_no_existe()
    {
        var handler = new LoginUserHandler(new FakeAccountService(), new FakeTokenService());

        await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(new LoginUserCommand("nonexistent@test.com", "Password123!")));
    }

    // ── Handler de Google login ────────────────────────────────────────────
    [Fact]
    public async Task Google_login_crea_usuario_nuevo_si_no_existe()
    {
        var googleValidator = new FakeGoogleTokenValidator();
        var accountService = new FakeAccountService();
        var tokenService = new FakeTokenService();
        var handler = new GoogleLoginHandler(googleValidator, accountService, tokenService);

        var result = await handler.HandleAsync(new GoogleLoginCommand("fake-google-token"));

        Assert.StartsWith("fake-jwt-", result.AccessToken);
        Assert.Equal("google@test.com", result.Email);
    }

    [Fact]
    public async Task Google_login_devuelve_el_mismo_usuario_si_ya_existe()
    {
        var googleValidator = new FakeGoogleTokenValidator();
        var accountService = new FakeAccountService();
        var tokenService = new FakeTokenService();
        var handler = new GoogleLoginHandler(googleValidator, accountService, tokenService);

        var first = await handler.HandleAsync(new GoogleLoginCommand("fake-google-token"));
        var second = await handler.HandleAsync(new GoogleLoginCommand("fake-google-token"));

        Assert.Equal(first.UserId, second.UserId);
        Assert.Equal(first.Email, second.Email);
    }

    [Fact]
    public async Task Google_login_falla_si_el_token_es_invalido()
    {
        var googleValidator = new FakeGoogleTokenValidator { ShouldFail = true };
        var handler = new GoogleLoginHandler(googleValidator, new FakeAccountService(), new FakeTokenService());

        await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(new GoogleLoginCommand("invalid-token")));
    }

    // ── Validación de Google login command ─────────────────────────────────
    [Fact]
    public async Task Google_login_command_rechaza_token_vacio()
    {
        var validator = new GoogleLoginCommandValidator();
        var result = await validator.ValidateAsync(new GoogleLoginCommand(""));
        Assert.False(result.IsValid);
    }
}
