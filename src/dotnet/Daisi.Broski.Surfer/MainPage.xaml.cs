namespace Daisi.Broski.Surfer;

/// <summary>
/// The MAUI page that hosts the Blazor <c>BlazorWebView</c> control.
/// All UI is rendered by the Blazor components rooted at
/// <see cref="Components.Routes"/>; this code-behind exists only
/// because XAML pages must have a partial class.
/// </summary>
public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }
}
