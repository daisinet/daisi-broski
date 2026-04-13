namespace Daisi.Broski.Surfer.WinUI;

/// <summary>
/// Windows App SDK / WinUI 3 application entry point. Boots the
/// shared MAUI app via <see cref="MauiProgram.CreateMauiApp"/>.
/// All cross-platform UI lives in the Blazor components hosted by
/// <see cref="Daisi.Broski.Surfer.MainPage"/>; this class just
/// owns the WinUI process lifecycle.
/// </summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
