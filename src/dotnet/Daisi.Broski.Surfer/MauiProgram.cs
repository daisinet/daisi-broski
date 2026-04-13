using Daisi.Broski.Engine;
using Daisi.Broski.Engine.Net;
using Microsoft.Extensions.Logging;

namespace Daisi.Broski.Surfer;

/// <summary>
/// MAUI application bootstrap — registers the <c>BlazorWebView</c>,
/// engine services, and the <c>SkimSession</c> singleton that holds
/// the user's current page + history. Wired by both the WinUI head
/// (<c>Platforms/Windows/App.xaml.cs</c>) and any future platform
/// heads.
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();

        // One PageLoader for the whole app — it owns the HttpFetcher
        // and its cookie jar, so navigations inside the surfer share
        // session state the way a real browser would.
        builder.Services.AddSingleton(_ =>
            new PageLoader(new HttpFetcherOptions()));

        // Per-app skim history + currently-displayed article. Singleton
        // so navigating between pages doesn't reset the back stack.
        builder.Services.AddSingleton<Services.SkimSession>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
