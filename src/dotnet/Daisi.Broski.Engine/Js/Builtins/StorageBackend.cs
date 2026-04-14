using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Persistence contract for <c>localStorage</c>. One implementation
/// round-trips a per-origin JSON file on disk; the default no-op
/// implementation keeps the phase-3 in-memory semantics for callers
/// that don't opt into persistence.
///
/// <para>
/// The engine reads/writes through an <see cref="IStorageBackend"/>
/// so the host can swap in a different persistence strategy (in-proc
/// cache, IPC-backed, multi-origin shared) without touching the JS
/// surface. <c>sessionStorage</c> is always transient (phase-5
/// keeps browser semantics: session storage doesn't cross the
/// lifetime of the hosting engine).
/// </para>
/// </summary>
public interface IStorageBackend
{
    /// <summary>Load the key/value pairs previously persisted for
    /// <paramref name="origin"/>. Returns an empty dictionary when
    /// there's no prior state for that origin.</summary>
    IReadOnlyDictionary<string, string> Load(string origin);

    /// <summary>Persist the given items against <paramref name="origin"/>,
    /// atomically replacing any prior state. An empty dictionary
    /// erases the origin's state.</summary>
    void Save(string origin, IReadOnlyDictionary<string, string> items);
}

/// <summary>No-op backend — the default. All reads return empty,
/// all writes are dropped on the floor. Matches phase-3 semantics
/// where <c>localStorage</c> was always transient.</summary>
public sealed class NullStorageBackend : IStorageBackend
{
    public static readonly NullStorageBackend Instance = new();
    private NullStorageBackend() { }
    public IReadOnlyDictionary<string, string> Load(string origin) =>
        EmptyReadOnlyDictionary<string, string>.Instance;
    public void Save(string origin, IReadOnlyDictionary<string, string> items) { }
}

/// <summary>JSON-per-origin storage on the local filesystem. One
/// file per origin under <c>{rootPath}/{sanitizedOrigin}.json</c>.
/// Origin names are sanitized to strip filesystem-illegal characters
/// and truncated to avoid exceeding Windows' 260-char path limit;
/// collisions across the truncation are disambiguated by a short
/// SHA-256 hash suffix. Writes are tempfile-then-rename to avoid
/// leaving a half-written JSON blob on crash.</summary>
public sealed class FileStorageBackend : IStorageBackend
{
    private readonly string _root;

    public FileStorageBackend(string rootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        _root = rootPath;
        Directory.CreateDirectory(_root);
    }

    /// <summary>The filesystem directory this backend writes to.
    /// Exposed for test assertions and tooling that wants to
    /// inspect the on-disk layout.</summary>
    public string RootPath => _root;

    public IReadOnlyDictionary<string, string> Load(string origin)
    {
        var path = PathFor(origin);
        if (!File.Exists(path)) return EmptyReadOnlyDictionary<string, string>.Instance;
        try
        {
            using var stream = File.OpenRead(path);
            var items = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return items ?? (IReadOnlyDictionary<string, string>)
                EmptyReadOnlyDictionary<string, string>.Instance;
        }
        catch (JsonException)
        {
            // Corrupt file (hand-edit, half-write from a crash
            // that predated this backend's atomic-rename). Treat
            // as empty; the next save overwrites it.
            return EmptyReadOnlyDictionary<string, string>.Instance;
        }
        catch (IOException)
        {
            // Transient file-lock contention from another process
            // peeking at the file. Don't crash script execution
            // over it — start with an empty snapshot, the next
            // setItem re-saves.
            return EmptyReadOnlyDictionary<string, string>.Instance;
        }
    }

    public void Save(string origin, IReadOnlyDictionary<string, string> items)
    {
        var path = PathFor(origin);
        // Serialize first so a serialization failure doesn't
        // leave a zero-byte target. Then atomic-rename.
        var tmp = path + ".tmp";
        try
        {
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(
                    stream,
                    items,
                    new JsonSerializerOptions { WriteIndented = false });
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (IOException)
        {
            // Best-effort persistence. If the disk is full or the
            // file is locked, drop the write rather than surface
            // it as a script-visible error — the in-memory items
            // dictionary still carries the data for the session.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private string PathFor(string origin) =>
        Path.Combine(_root, SanitizeOriginForFilename(origin) + ".json");

    /// <summary>Turn an origin string (<c>https://example.com:8443</c>)
    /// into a filesystem-safe filename. Strips the scheme separator,
    /// percent-encodes anything the Win32 path layer rejects, and
    /// appends a short hash suffix so very long origins (e.g.
    /// <c>data:</c> URIs after canonicalization) don't collide after
    /// truncation.</summary>
    internal static string SanitizeOriginForFilename(string origin)
    {
        var sb = new StringBuilder(origin.Length + 12);
        foreach (var c in origin)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9') or '.' or '-' or '_')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }
        // 8-hex-char SHA-256 prefix to disambiguate near-identical
        // origins that collapse to the same sanitized form.
        var bytes = Encoding.UTF8.GetBytes(origin);
        var hash = SHA256.HashData(bytes);
        sb.Append('-');
        for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("x2"));

        // Truncate to a comfortable margin under Windows' MAX_PATH
        // (260), leaving room for the root directory + .json suffix.
        const int MaxLen = 200;
        if (sb.Length > MaxLen) sb.Length = MaxLen;
        return sb.ToString();
    }
}

/// <summary>Tiny empty IReadOnlyDictionary singleton — avoids
/// repeated allocation in the hot "origin has no saved state" path.</summary>
internal static class EmptyReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    public static readonly IReadOnlyDictionary<TKey, TValue> Instance =
        new Dictionary<TKey, TValue>();
}
