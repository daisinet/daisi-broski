namespace Daisi.Broski.Surfer.Services;

/// <summary>
/// Thin wrapper around <see cref="Preferences.Default"/>
/// for the three UI preferences we persist: the selected
/// format tab, whether scripting is enabled, and the
/// Reader column width. Keeping the keys + defaults
/// centralized here means <see cref="SkimSession"/> and
/// the Home component can't drift on naming — they each
/// call the same getter/setter pair.
///
/// <para>
/// MAUI's Preferences backs to platform-native storage:
/// the Windows Settings container on WinUI, NSUserDefaults
/// on Mac Catalyst, SharedPreferences on Android. Values
/// survive app restarts and process crashes; there's no
/// explicit flush step.
/// </para>
/// </summary>
public static class SurferSettings
{
    // Namespaced keys so other apps sharing the same
    // preferences store (unlikely, but possible on mobile)
    // can't collide with ours.
    private const string KeyFormat = "surfer.format";
    private const string KeyJs = "surfer.js-enabled";
    private const string KeyWideReader = "surfer.wide-reader";

    /// <summary>Active format tab — "Reader" / "Snapshot"
    /// / "Markdown" / "JSON" / "Links". Default: Reader.</summary>
    public static string Format
    {
        get => Preferences.Default.Get(KeyFormat, "Reader");
        set => Preferences.Default.Set(KeyFormat, value);
    }

    /// <summary>Whether page scripts run before extraction.
    /// Default: true (modern SPAs need it).</summary>
    public static bool ScriptingEnabled
    {
        get => Preferences.Default.Get(KeyJs, true);
        set => Preferences.Default.Set(KeyJs, value);
    }

    /// <summary>Reader column width — false = narrow
    /// (~740px), true = wide (~1200px). Default: narrow,
    /// which matches the research-backed comfortable
    /// reading line-length of 60-80 characters.</summary>
    public static bool WideReader
    {
        get => Preferences.Default.Get(KeyWideReader, false);
        set => Preferences.Default.Set(KeyWideReader, value);
    }
}
