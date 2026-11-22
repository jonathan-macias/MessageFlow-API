using System.Security.Claims;
using MessageFlow.Application.Auth;
using Microsoft.AspNetCore.Identity;

namespace MessageFlow.Infrastructure.Users;

/// <summary>
/// Implementación de IAccountService sobre ASP.NET Core Identity.
/// Maneja registro, login y resolución de usuarios externos (Google).
/// Los errores de Identity se propagan como DomainException para que
/// el GlobalExceptionHandler los mapee a 400/409.
/// </summary>
internal sealed class AccountService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager) : IAccountService
{
    public async Task<(Guid UserId, string Email)> RegisterAsync(
        string email, string password, CancellationToken ct = default)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true, // Sin confirmación de email por ahora
        };

        var result = await userManager.CreateAsync(user, password);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new Domain.Exceptions.DomainException($"Error al registrar usuario: {errors}");
        }

        // Asignar rol por defecto
        await userManager.AddToRoleAsync(user, "User");

        return (user.Id, user.Email!);
    }

    public async Task<(Guid UserId, string Email)?> ValidateCredentialsAsync(
        string email, string password, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(email);

        if (user is null)
        {
            return null;
        }

        // lockoutOnFailure: cuenta los intentos fallidos y bloquea temporalmente la cuenta. Una cuenta
        // bloqueada también devuelve null: el cliente ve el mismo 401 genérico, sin distinguir el motivo.
        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            return null;
        }

        return (user.Id, user.Email!);
    }

    public async Task<(Guid UserId, string Email, bool IsNewUser)> FindOrCreateExternalAsync(
        string provider,
        string externalId,
        string email,
        string? displayName,
        CancellationToken ct = default)
    {
        // Buscar si ya existe un login externo con este provider+externalId
        var loginInfo = new UserLoginInfo(provider, externalId, provider);
        var user = await userManager.FindByLoginAsync(loginInfo.LoginProvider, loginInfo.ProviderKey);

        if (user is not null)
        {
            return (user.Id, user.Email!, false);
        }

        // Buscar por email
        user = await userManager.FindByEmailAsync(email);

        if (user is not null)
        {
            // Asociar el login externo al usuario existente. Se conservan ambos métodos de
            // acceso (contraseña y Google).
            var linkResult = await userManager.AddLoginAsync(user, loginInfo);

            if (!linkResult.Succeeded)
            {
                var linkErrors = string.Join("; ", linkResult.Errors.Select(e => e.Description));
                throw new Domain.Exceptions.DomainException($"No se pudo enlazar la cuenta de {provider}: {linkErrors}");
            }

            return (user.Id, user.Email!, false);
        }

        // Crear usuario nuevo
        user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = true,
            AuthProvider = provider,
            ExternalProviderId = externalId,
        };

        var result = await userManager.CreateAsync(user);

        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            throw new Domain.Exceptions.DomainException($"Error al crear usuario desde {provider}: {errors}");
        }

        var newLoginResult = await userManager.AddLoginAsync(user, loginInfo);

        if (!newLoginResult.Succeeded)
        {
            var loginErrors = string.Join("; ", newLoginResult.Errors.Select(e => e.Description));
            throw new Domain.Exceptions.DomainException($"No se pudo enlazar la cuenta de {provider}: {loginErrors}");
        }

        await userManager.AddToRoleAsync(user, "User");

        return (user.Id, user.Email!, true);
    }
}
