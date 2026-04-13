namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Wires the two <see cref="JsStorage"/> instances on a
/// <see cref="JsEngine"/> to an <see cref="IStorageBackend"/>.
/// On every <see cref="JsEngine.AttachDocument"/> call the
/// manager:
///
/// <list type="bullet">
/// <item>extracts the origin (<c>scheme://host[:port]</c>) from
///   the new page URL;</item>
/// <item>if the origin changed, saves any pending localStorage
///   writes from the previous origin, clears both storages,
///   and reloads localStorage from disk for the new origin;</item>
/// <item>keeps a one-way subscription on
///   <see cref="JsStorage.Changed"/> so each successful write
///   persists immediately.</item>
/// </list>
///
/// <para>
/// The manager is installed by <see cref="BuiltinBrowserHost"/>
/// during engine construction and is safe to leave pointing at
/// the no-op <see cref="NullStorageBackend"/> — in that case the
/// writes still happen in-memory and the on-attach reload is a
/// no-op. The embedder upgrades the backend via
/// <see cref="JsEngine.SetStorageBackend"/> before the first
/// <c>AttachDocument</c> call so the origin of interest loads
/// cleanly.
/// </para>
///
/// <para>
/// sessionStorage is deliberately transient: it is cleared on
/// every origin transition and never persisted. Real browsers
/// scope sessionStorage to the (origin, tab) pair; in a
/// headless engine without tabs the closest equivalent is
/// "survive as long as the origin doesn't change".
/// </para>
/// </summary>
internal sealed class JsStorageManager
{
    private readonly JsStorage _local;
    private readonly JsStorage _session;
    private readonly JsEngine _engine;
    private string? _currentOrigin;
    private bool _loading;

    public JsStorageManager(JsStorage local, JsStorage session, JsEngine engine)
    {
        _local = local;
        _session = session;
        _engine = engine;
        _local.Changed += OnLocalChanged;
    }

    /// <summary>Called by <see cref="JsEngine.AttachDocument"/>
    /// with the new page URL. Safe to call repeatedly with the
    /// same URL; the origin-change branch is a no-op when the
    /// effective origin hasn't moved.</summary>
    public void OnPageLoaded(Uri? pageUrl)
    {
        var newOrigin = ExtractOrigin(pageUrl);
        if (newOrigin == _currentOrigin)
        {
            // Same origin (or both null) — keep the in-memory
            // state as-is. A reload-in-place shouldn't clobber
            // the user's session data.
            return;
        }

        // Flush any pending writes on the old origin so they
        // survive the transition. OnLocalChanged is a no-op
        // while _currentOrigin is null (engine construction
        // before any document has attached).
        if (_currentOrigin is not null)
        {
            _engine.StorageBackend.Save(_currentOrigin, _local.Items);
        }

        _currentOrigin = newOrigin;

        // Clear both storages; sessionStorage stays empty for
        // the new origin, localStorage is re-hydrated from disk.
        _loading = true;
        try
        {
            _local.Items.Clear();
            _session.Items.Clear();
            if (newOrigin is not null)
            {
                foreach (var kv in _engine.StorageBackend.Load(newOrigin))
                {
                    _local.Items[kv.Key] = kv.Value;
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Called when the embedder swaps in a new
    /// backend after the manager was already wired. Triggers
    /// a reload for the current origin so the new backend's
    /// on-disk state wins over whatever the old backend left
    /// in-memory.</summary>
    public void OnBackendChanged()
    {
        if (_currentOrigin is null) return;
        _loading = true;
        try
        {
            _local.Items.Clear();
            foreach (var kv in _engine.StorageBackend.Load(_currentOrigin))
            {
                _local.Items[kv.Key] = kv.Value;
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnLocalChanged()
    {
        // Don't persist mid-load — the manager is the one
        // populating the dictionary, and firing Save from
        // inside Load creates an N^2 rewrite loop.
        if (_loading) return;
        if (_currentOrigin is null) return;
        _engine.StorageBackend.Save(_currentOrigin, _local.Items);
    }

    /// <summary>Pull the <c>scheme://host[:port]</c> origin out
    /// of the given URL. Returns <c>null</c> for URLs that don't
    /// have a meaningful origin (<c>about:blank</c>, <c>data:</c>,
    /// <c>file://</c> — localStorage has no persistent home for
    /// these per the WHATWG origin spec).</summary>
    internal static string? ExtractOrigin(Uri? url)
    {
        if (url is null) return null;
        if (url.Scheme != "http" && url.Scheme != "https") return null;
        return url.IsDefaultPort
            ? $"{url.Scheme}://{url.Host}"
            : $"{url.Scheme}://{url.Host}:{url.Port}";
    }
}
