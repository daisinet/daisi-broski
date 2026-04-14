using System.Text.Json;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Persistence contract for IndexedDB. Mirrors
/// <see cref="IStorageBackend"/> at a higher granularity —
/// each call moves a whole database snapshot rather than a
/// single key/value pair, since IDB transactions naturally
/// commit one snapshot at a time.
///
/// <para>
/// The default <see cref="NullIndexedDbBackend"/> keeps
/// IndexedDB transient (in-memory only). Setting an opt-in
/// <see cref="FileIndexedDbBackend"/> on
/// <see cref="JsEngine.IndexedDbBackend"/> persists
/// each database as a single JSON file under the configured
/// root directory.
/// </para>
/// </summary>
public interface IIndexedDbBackend
{
    /// <summary>Load a previously persisted snapshot for
    /// <paramref name="dbName"/>. Returns <c>null</c> when no
    /// prior state exists.</summary>
    IndexedDbSnapshot? Load(string dbName);

    /// <summary>Atomically replace the stored snapshot for
    /// <paramref name="dbName"/>.</summary>
    void Save(string dbName, IndexedDbSnapshot snapshot);

    /// <summary>Drop all state for <paramref name="dbName"/>.
    /// Used by <c>indexedDB.deleteDatabase</c>.</summary>
    void Delete(string dbName);
}

/// <summary>No-op backend — the default. All loads return
/// <c>null</c>; all saves are dropped on the floor. The
/// in-memory snapshot inside <see cref="JsIDBDatabase"/>
/// still keeps data alive for the lifetime of the engine.</summary>
public sealed class NullIndexedDbBackend : IIndexedDbBackend
{
    public static readonly NullIndexedDbBackend Instance = new();
    private NullIndexedDbBackend() { }
    public IndexedDbSnapshot? Load(string dbName) => null;
    public void Save(string dbName, IndexedDbSnapshot snapshot) { }
    public void Delete(string dbName) { }
}

/// <summary>JSON-per-database backend. Each
/// <c>indexedDB.open(name)</c> reads <c>{root}/{name}.json</c>;
/// each transaction commit writes the new snapshot via
/// tempfile-then-rename.</summary>
public sealed class FileIndexedDbBackend : IIndexedDbBackend
{
    private readonly string _root;

    public FileIndexedDbBackend(string rootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        _root = rootPath;
        Directory.CreateDirectory(_root);
    }

    public string RootPath => _root;

    public IndexedDbSnapshot? Load(string dbName)
    {
        var path = PathFor(dbName);
        if (!File.Exists(path)) return null;
        try
        {
            using var s = File.OpenRead(path);
            return JsonSerializer.Deserialize<IndexedDbSnapshot>(s);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public void Save(string dbName, IndexedDbSnapshot snapshot)
    {
        var path = PathFor(dbName);
        var tmp = path + ".tmp";
        try
        {
            using (var s = File.Create(tmp))
            {
                JsonSerializer.Serialize(s, snapshot);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (IOException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public void Delete(string dbName)
    {
        var path = PathFor(dbName);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private string PathFor(string dbName) =>
        Path.Combine(_root,
            FileStorageBackend.SanitizeOriginForFilename(dbName) + ".json");
}

/// <summary>One database's persisted state. Schema-less by
/// design — values are JSON-string-encoded so the backend
/// doesn't need to know about JS engine internals.</summary>
public sealed class IndexedDbSnapshot
{
    public int Version { get; set; }
    public Dictionary<string, IndexedDbStoreSnapshot> Stores { get; set; } = new();
}

/// <summary>One object store's contents. Items are stored
/// in insertion order; values are JSON strings produced by
/// <c>JSON.stringify</c>.</summary>
public sealed class IndexedDbStoreSnapshot
{
    public List<IndexedDbItem> Items { get; set; } = new();
}

public sealed class IndexedDbItem
{
    public string Key { get; set; } = "";
    /// <summary>JSON-encoded value. Decoded back to a JS
    /// value via <c>JSON.parse</c> on read.</summary>
    public string Value { get; set; } = "";
}
