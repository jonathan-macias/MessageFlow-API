using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;

namespace MessageFlow.Application.Auth;

public sealed record LoginUserCommand(string Email, string Password) : ICommand<LoginUserResult>;

public sealed record LoginUserResult(string AccessToken, DateTimeOffset ExpiresAt, Guid UserId, string Email);

public sealed class LoginUserCommandValidator : AbstractValidator<LoginUserCommand>
{
    public LoginUserCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("The email is required.")
            .EmailAddress().WithMessage("The email format is not valid.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("The password is required.");
    }
}
