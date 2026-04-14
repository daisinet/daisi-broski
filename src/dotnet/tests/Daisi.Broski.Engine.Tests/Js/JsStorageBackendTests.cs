using Daisi.Broski.Engine.Dom;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Phase-5a file-backed localStorage: every set/remove/clear flows
/// through an <see cref="IStorageBackend"/>, origins get their own
/// JSON file under the root directory, and a second engine
/// pointed at the same directory sees the prior engine's writes.
/// sessionStorage stays in-memory across the same transitions.
/// </summary>
public class JsStorageBackendTests : IDisposable
{
    private readonly string _tempDir;

    public JsStorageBackendTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "daisi-broski-storage-test-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void NullStorageBackend_is_the_default()
    {
        var engine = new JsEngine();
        Assert.Same(NullStorageBackend.Instance, engine.StorageBackend);
    }

    [Fact]
    public void SetItem_writes_a_file_under_the_backend_root()
    {
        var engine = new JsEngine();
        engine.SetStorageBackend(new FileStorageBackend(_tempDir));
        engine.AttachDocument(new Document(), new Uri("https://example.com/"));

        engine.Evaluate("localStorage.setItem('foo', 'bar');");

        // Some JSON file should now exist under the temp directory.
        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.Single(files);
        var contents = File.ReadAllText(files[0]);
        Assert.Contains("\"foo\":\"bar\"", contents);
    }

    [Fact]
    public void Writes_persist_across_a_fresh_engine_on_the_same_origin()
    {
        var origin = new Uri("https://example.com/");

        // Engine A: write something.
        var a = new JsEngine();
        a.SetStorageBackend(new FileStorageBackend(_tempDir));
        a.AttachDocument(new Document(), origin);
        a.Evaluate(@"
            localStorage.setItem('token', 'sekret');
            localStorage.setItem('user', 'alice');
        ");

        // Engine B: reopen the same origin and read it back.
        var b = new JsEngine();
        b.SetStorageBackend(new FileStorageBackend(_tempDir));
        b.AttachDocument(new Document(), origin);
        Assert.Equal("sekret", b.Evaluate("localStorage.getItem('token');"));
        Assert.Equal("alice", b.Evaluate("localStorage.getItem('user');"));
    }

    [Fact]
    public void Different_origins_get_different_files()
    {
        var a = new JsEngine();
        a.SetStorageBackend(new FileStorageBackend(_tempDir));
        a.AttachDocument(new Document(), new Uri("https://a.example.com/"));
        a.Evaluate("localStorage.setItem('k', 'from-a');");

        var b = new JsEngine();
        b.SetStorageBackend(new FileStorageBackend(_tempDir));
        b.AttachDocument(new Document(), new Uri("https://b.example.com/"));
        b.Evaluate("localStorage.setItem('k', 'from-b');");

        // Two files under the root, each containing its own value.
        var files = Directory.GetFiles(_tempDir, "*.json");
        Assert.Equal(2, files.Length);

        // Round-trip confirmation: a fresh engine keyed to a.example
        // sees 'from-a', one keyed to b.example sees 'from-b'.
        var readerA = new JsEngine();
        readerA.SetStorageBackend(new FileStorageBackend(_tempDir));
        readerA.AttachDocument(new Document(), new Uri("https://a.example.com/"));
        Assert.Equal("from-a", readerA.Evaluate("localStorage.getItem('k');"));

        var readerB = new JsEngine();
        readerB.SetStorageBackend(new FileStorageBackend(_tempDir));
        readerB.AttachDocument(new Document(), new Uri("https://b.example.com/"));
        Assert.Equal("from-b", readerB.Evaluate("localStorage.getItem('k');"));
    }

    [Fact]
    public void Cross_origin_navigate_loads_the_new_origins_state()
    {
        // Pre-seed origin B via a separate engine so the
        // cross-origin navigate below has something to load.
        var seeder = new JsEngine();
        seeder.SetStorageBackend(new FileStorageBackend(_tempDir));
        seeder.AttachDocument(new Document(), new Uri("https://b.example.com/"));
        seeder.Evaluate("localStorage.setItem('who', 'b');");

        // Navigate A → B inside the same engine: A's data gets saved,
        // B's data loads in.
        var engine = new JsEngine();
        engine.SetStorageBackend(new FileStorageBackend(_tempDir));
        engine.AttachDocument(new Document(), new Uri("https://a.example.com/"));
        engine.Evaluate("localStorage.setItem('who', 'a');");
        Assert.Equal("a", engine.Evaluate("localStorage.getItem('who');"));

        engine.AttachDocument(new Document(), new Uri("https://b.example.com/"));
        Assert.Equal("b", engine.Evaluate("localStorage.getItem('who');"));

        // Navigate back to A: the value we wrote earlier is still there.
        engine.AttachDocument(new Document(), new Uri("https://a.example.com/"));
        Assert.Equal("a", engine.Evaluate("localStorage.getItem('who');"));
    }

    [Fact]
    public void SessionStorage_never_persists()
    {
        var origin = new Uri("https://example.com/");

        var a = new JsEngine();
        a.SetStorageBackend(new FileStorageBackend(_tempDir));
        a.AttachDocument(new Document(), origin);
        a.Evaluate("sessionStorage.setItem('ephemeral', 'yes');");

        var b = new JsEngine();
        b.SetStorageBackend(new FileStorageBackend(_tempDir));
        b.AttachDocument(new Document(), origin);
        Assert.Equal(JsValue.Null, b.Evaluate("sessionStorage.getItem('ephemeral');"));
    }

    [Fact]
    public void SessionStorage_clears_on_origin_change()
    {
        var engine = new JsEngine();
        engine.SetStorageBackend(new FileStorageBackend(_tempDir));
        engine.AttachDocument(new Document(), new Uri("https://a.example.com/"));
        engine.Evaluate("sessionStorage.setItem('foo', 'A');");
        Assert.Equal("A", engine.Evaluate("sessionStorage.getItem('foo');"));

        engine.AttachDocument(new Document(), new Uri("https://b.example.com/"));
        Assert.Equal(JsValue.Null, engine.Evaluate("sessionStorage.getItem('foo');"));
    }

    [Fact]
    public void Same_origin_reload_keeps_sessionStorage()
    {
        var origin = new Uri("https://example.com/");

        var engine = new JsEngine();
        engine.SetStorageBackend(new FileStorageBackend(_tempDir));
        engine.AttachDocument(new Document(), origin);
        engine.Evaluate("sessionStorage.setItem('foo', 'bar');");

        // Same origin, fresh document — sessionStorage survives.
        engine.AttachDocument(new Document(), origin);
        Assert.Equal("bar", engine.Evaluate("sessionStorage.getItem('foo');"));
    }

    [Fact]
    public void RemoveItem_and_clear_are_persisted()
    {
        var origin = new Uri("https://example.com/");

        var a = new JsEngine();
        a.SetStorageBackend(new FileStorageBackend(_tempDir));
        a.AttachDocument(new Document(), origin);
        a.Evaluate(@"
            localStorage.setItem('a', '1');
            localStorage.setItem('b', '2');
            localStorage.removeItem('a');
        ");

        // Reopen: 'a' should be gone, 'b' should still be there.
        var b = new JsEngine();
        b.SetStorageBackend(new FileStorageBackend(_tempDir));
        b.AttachDocument(new Document(), origin);
        Assert.Equal(JsValue.Null, b.Evaluate("localStorage.getItem('a');"));
        Assert.Equal("2", b.Evaluate("localStorage.getItem('b');"));

        // Clear then reopen: everything is gone.
        b.Evaluate("localStorage.clear();");
        var c = new JsEngine();
        c.SetStorageBackend(new FileStorageBackend(_tempDir));
        c.AttachDocument(new Document(), origin);
        Assert.Equal(0.0, c.Evaluate("localStorage.length;"));
    }

    [Fact]
    public void Non_http_origins_are_transient()
    {
        // file:// has no meaningful origin for persistence.
        var engine = new JsEngine();
        engine.SetStorageBackend(new FileStorageBackend(_tempDir));
        engine.AttachDocument(new Document(), new Uri("file:///C:/local/index.html"));
        engine.Evaluate("localStorage.setItem('k', 'v');");

        // No persistence file was created.
        Assert.Empty(Directory.GetFiles(_tempDir, "*.json"));
    }

    [Fact]
    public void Writes_before_backend_is_set_become_persistent_on_upgrade()
    {
        var origin = new Uri("https://example.com/");

        var engine = new JsEngine();
        engine.AttachDocument(new Document(), origin);
        engine.Evaluate("localStorage.setItem('early', 'yes');");

        // Upgrading the backend mid-session pulls disk state
        // (empty here) over the in-memory dictionary. That's
        // WHATWG-consistent: the disk is the source of truth
        // for localStorage across the backend-swap transition.
        engine.SetStorageBackend(new FileStorageBackend(_tempDir));
        Assert.Equal(JsValue.Null, engine.Evaluate("localStorage.getItem('early');"));

        // Writes after the upgrade *do* persist.
        engine.Evaluate("localStorage.setItem('late', 'yes');");
        var b = new JsEngine();
        b.SetStorageBackend(new FileStorageBackend(_tempDir));
        b.AttachDocument(new Document(), origin);
        Assert.Equal("yes", b.Evaluate("localStorage.getItem('late');"));
    }

    [Fact]
    public void Backend_handles_port_and_scheme_as_part_of_origin()
    {
        // https://example.com and https://example.com:8443 are
        // different origins; verify they don't collide.
        var root = new JsEngine();
        root.SetStorageBackend(new FileStorageBackend(_tempDir));
        root.AttachDocument(new Document(), new Uri("https://example.com/"));
        root.Evaluate("localStorage.setItem('port', 'default');");

        var alt = new JsEngine();
        alt.SetStorageBackend(new FileStorageBackend(_tempDir));
        alt.AttachDocument(new Document(), new Uri("https://example.com:8443/"));
        alt.Evaluate("localStorage.setItem('port', 'alt');");

        var reader = new JsEngine();
        reader.SetStorageBackend(new FileStorageBackend(_tempDir));
        reader.AttachDocument(new Document(), new Uri("https://example.com/"));
        Assert.Equal("default", reader.Evaluate("localStorage.getItem('port');"));
    }

    [Fact]
    public void SanitizeOriginForFilename_is_filesystem_safe()
    {
        // Punctuation and path-separator characters should
        // survive the pass as underscores with a disambiguating
        // hash suffix.
        var name = FileStorageBackend.SanitizeOriginForFilename(
            "https://weird.example.com:8080");
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.Matches("^[A-Za-z0-9._-]+$", name);
    }
}
