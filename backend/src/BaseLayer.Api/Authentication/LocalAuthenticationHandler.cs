using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
namespace BaseLayer.Api.Authentication;
// Registered only in Development with an explicit, strong, local-only token.
public sealed class LocalAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = Encoding.UTF8.GetBytes("Bearer " + configuration["LocalDemo:Token"]);
        var actual = Encoding.UTF8.GetBytes(Request.Headers.Authorization.ToString());
        var result = CryptographicOperations.FixedTimeEquals(expected, actual)
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "local-demo-user")], Scheme.Name)), Scheme.Name))
            : AuthenticateResult.Fail("Invalid development credential.");
        return Task.FromResult(result);
    }
}
