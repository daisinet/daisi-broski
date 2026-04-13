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

        // Per-app skim history + currently-displayed article.
        // Singleton so navigating between pages doesn't reset the
        // back stack. The session uses Skimmer.SkimAsync internally
        // so each navigation gets a fresh fetcher (cookie isolation
        // between visits) — matches a privacy-mode browser.
        builder.Services.AddSingleton<Services.SkimSession>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
