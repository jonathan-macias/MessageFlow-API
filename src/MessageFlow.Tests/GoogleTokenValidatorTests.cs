using Google.Apis.Auth;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Infrastructure.Users;

namespace MessageFlow.Tests;

/// <summary>
/// El correo de Google solo es una identidad válida si Google lo marca como verificado;
/// de lo contrario se podría enlazar la sesión a una cuenta ajena con el mismo email.
/// </summary>
public class GoogleTokenValidatorTests
{
    [Fact]
    public void Correo_verificado_se_mapea_a_la_informacion_del_usuario()
    {
        var payload = new GoogleJsonWebSignature.Payload
        {
            Email = "ana@example.com",
            EmailVerified = true,
            Subject = "google-sub-1",
            Name = "Ana",
        };

        var info = GoogleTokenValidator.ToUserInfo(payload);

        Assert.Equal("ana@example.com", info.Email);
        Assert.Equal("google-sub-1", info.ExternalId);
        Assert.Equal("Ana", info.DisplayName);
    }

    [Fact]
    public void Correo_no_verificado_se_rechaza()
    {
        var payload = new GoogleJsonWebSignature.Payload
        {
            Email = "ana@example.com",
            EmailVerified = false,
            Subject = "google-sub-1",
        };

        Assert.Throws<DomainException>(() => GoogleTokenValidator.ToUserInfo(payload));
    }

    [Fact]
    public void Token_sin_correo_se_rechaza()
    {
        var payload = new GoogleJsonWebSignature.Payload
        {
            Email = null,
            EmailVerified = true,
            Subject = "google-sub-1",
        };

        Assert.Throws<DomainException>(() => GoogleTokenValidator.ToUserInfo(payload));
    }
}
