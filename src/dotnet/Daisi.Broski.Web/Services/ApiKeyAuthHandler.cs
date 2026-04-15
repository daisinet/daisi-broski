using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Daisi.Broski.Web.Services;

/// <summary>
/// ASP.NET authentication handler for the personal-access-token
/// scheme. Accepts tokens via <c>X-Api-Key</c> header,
/// <c>Authorization: Bearer db_…</c>, or an <c>api_key</c>
/// query-string parameter (handy for GET /skim?url=...&amp;api_key=...
/// from a browser address bar). Tokens are validated through
/// <see cref="ApiKeyService"/>, which updates the last-used
/// timestamp as a side-effect.
///
/// <para>Registered with the scheme name <c>ApiKey</c>; the
/// <c>/api/v1/skim</c> endpoint requires it via
/// <c>RequireAuthorization("ApiKey")</c>.</para>
/// </summary>
public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider serviceProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
        {
            var auth = Request.Headers.Authorization.FirstOrDefault();
            if (auth is not null && auth.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                token = auth["Bearer ".Length..];
            }
        }
        if (string.IsNullOrEmpty(token))
        {
            token = Request.Query["api_key"].FirstOrDefault();
        }
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();

        using var scope = serviceProvider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var key = await svc.ValidateTokenAsync(token);
        if (key is null) return AuthenticateResult.Fail("Invalid or expired API key.");

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, key.UserName),
            new Claim(ClaimTypes.Sid, key.UserId),
            new Claim(ClaimTypes.GroupSid, key.AccountId),
            new Claim("apiKeyId", key.id),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        // Stash the validated key id for the usage logger (P4);
        // it'll read this off HttpContext.Items when recording
        // the request.
        Context.Items["apiKeyId"] = key.id;
        Context.Items["accountId"] = key.AccountId;
        Context.Items["userId"] = key.UserId;
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name));
    }
}
