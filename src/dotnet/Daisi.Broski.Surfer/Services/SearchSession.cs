using Daisi.Broski.Gofer.Search;

namespace Daisi.Broski.Surfer.Services;

/// <summary>
/// Cross-page state for the /search experience. Keeps the user's
/// last query, last source selection, and last hit list so that
/// clicking a result → reading it → hitting Back returns to the
/// same result page instead of a blank search box. Also carries
/// the <see cref="ReturnToSearch"/> flag Home reads to know when
/// its back button should route to /search instead of walking
/// the SkimSession history.
/// </summary>
public sealed class SearchSession
{
    /// <summary>The last query the user ran. Empty on first launch.</summary>
    public string Query { get; set; } = "";

    private SearchSource _selected = (SearchSource)SurferSettings.SearchSources;

    /// <summary>Selected provider flags. Defaults to the
    /// user's last-persisted choice (Scholarly+Community on
    /// first launch). Every assignment writes through to
    /// <see cref="SurferSettings.SearchSources"/> so the
    /// picker re-opens on the same set across app restarts.</summary>
    public SearchSource Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            SurferSettings.SearchSources = (int)value;
        }
    }

    /// <summary>Last run's hit list. Null until the first search.</summary>
    public IReadOnlyList<SearchResult>? Hits { get; set; }

    /// <summary>Set when the user opens a hit in the Reader — the
    /// signal the Home page's back button checks to decide
    /// whether "back" should drop the user off at the Reader's
    /// skim history (default) or back at /search.</summary>
    public bool ReturnToSearch { get; set; }
}
