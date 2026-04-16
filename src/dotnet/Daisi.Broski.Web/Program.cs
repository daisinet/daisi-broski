using Daisi.Broski.Web.Components;
using Daisi.Broski.Web.Data;
using Daisi.Broski.Web.Endpoints;
using Daisi.Broski.Web.Services;
using Daisi.SDK.Models;
using Daisi.SDK.Web.Extensions;
using Daisi.SDK.Web.Services;
using Microsoft.AspNetCore.Authentication;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server with interactive rendering — same stack daisi-
// business-crm and daisi-git use. Interactive server components
// run on the server and stream UI updates to the browser over
// SignalR, so button clicks on admin pages fire C# handlers
// directly without extra JS plumbing.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();
builder.Services.AddHttpContextAccessor();

// Daisi SSO: AddDaisiForWeb wires cookie-auth schemes and the
// AuthService; AddDaisiMiddleware registers the middleware that
// handles /sso/authorize + /sso/callback + logout;
// AddDaisiCookieKeyProvider plugs in the cookie-based client-
// key resolver that gets read on every request.
builder.Services.AddDaisiForWeb()
                .AddDaisiMiddleware()
                .AddDaisiCookieKeyProvider();

// API-key bearer scheme for /api/v1/* — independent from the
// cookie-backed SSO scheme used by /admin. Registered here but
// only required by endpoints that opt in (skim endpoints call
// RequireAuthorization(SchemeName)).
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(
        ApiKeyAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(ApiKeyAuthHandler.SchemeName, p => p
        .AddAuthenticationSchemes(ApiKeyAuthHandler.SchemeName)
        .RequireAuthenticatedUser());
});

// Data layer + domain services. Cosmo is the singleton client
// wrapper; ApiKeyService / SkimService are injected per scope
// where they need DI access to the logged-in user.
builder.Services.AddSingleton<BroskiWebCosmo>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<UsageLogService>();
builder.Services.AddSingleton<SkimService>();

var app = builder.Build();

// Load Daisi SDK static settings from config (including any
// values supplied via the shared user-secrets store keyed to
// UserSecretsId=daisigit-web-2026). The SDK reads SSO URLs +
// signing key + Orc endpoint from this single static sink.
DaisiStaticSettings.LoadFromConfiguration(
    builder.Configuration.AsEnumerable()
        .Where(kv => kv.Value is not null)
        .ToDictionary(kv => kv.Key, kv => kv.Value!));

app.UseStaticFiles();

// Authentication must run before authorization so downstream
// RequireAuthorization() calls on minimal-API endpoints see
// the ApiKey scheme's ClaimsPrincipal.
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// The Daisi middleware handles /sso/authorize and /sso/callback
// transparently, plus any URL containing "logout" (which
// triggers a global logout via GlobalLogoutAsync).
app.UseDaisiMiddleware();

// Custom gate: redirect unauthenticated users who try to enter
// the admin area to /welcome. Mirrors the daisi-git pattern —
// friendlier than sending a first-time visitor straight to the
// SSO login. Paths that don't require auth (landing, api,
// static assets, SSO plumbing) pass through untouched.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
    bool needsAuth = path.StartsWith("/admin");
    if (needsAuth)
    {
        var authService = context.RequestServices.GetRequiredService<AuthService>();
        bool authed = false;
        try { authed = await authService.IsAuthenticatedAsync(); } catch { }
        if (!authed)
        {
            context.Response.Redirect("/welcome");
            return;
        }
    }
    await next();
});

SkimApiEndpoints.MapSkimApi(app.MapGroup("/api/v1"));
GoferApiEndpoints.MapGoferApi(app.MapGroup("/api/v1/gofer"));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
