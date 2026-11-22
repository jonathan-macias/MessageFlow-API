using FluentValidation;
using MessageFlow.Application.Abstractions.Messaging;

namespace MessageFlow.Application.Auth;

public sealed record RegisterUserCommand(string Email, string Password) : ICommand<RegisterUserResult>;

public sealed record RegisterUserResult(Guid UserId, string Email);

public sealed class RegisterUserCommandValidator : AbstractValidator<RegisterUserCommand>
{
    public RegisterUserCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("The email is required.")
            .EmailAddress().WithMessage("The email format is not valid.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("The password is required.")
            .MinimumLength(8).WithMessage("The password must be at least 8 characters long.")
            .Matches("[A-Z]").WithMessage("The password must contain at least one uppercase letter.")
            .Matches("[a-z]").WithMessage("The password must contain at least one lowercase letter.")
            .Matches("[0-9]").WithMessage("The password must contain at least one number.");
    }
}
