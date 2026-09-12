using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;

namespace MessageFlow.Application.Auth;

/// <summary>
/// Login/registro con Google OAuth. El frontend envía el id_token obtenido del
/// flujo de Google; el backend lo valida, crea el usuario si no existe y devuelve
/// nuestro propio JWT de Message Flow.
/// </summary>
public sealed record GoogleLoginCommand(string IdToken) : ICommand<LoginUserResult>;

public sealed class GoogleLoginCommandValidator : AbstractValidator<GoogleLoginCommand>
{
    public GoogleLoginCommandValidator()
    {
        RuleFor(x => x.IdToken)
            .NotEmpty().WithMessage("El token de Google es obligatorio.");
    }
}
