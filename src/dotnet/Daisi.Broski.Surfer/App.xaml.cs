namespace Daisi.Broski.Surfer;

/// <summary>
/// MAUI <see cref="Application"/> entry point. Creates the single
/// window that hosts <see cref="MainPage"/> (a <c>BlazorWebView</c>
/// pointed at the Surfer's Razor components).
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage())
        {
            Title = "Daisi Broski Surfer",
            Width = 1280,
            Height = 800,
        };
    }
}
