using System.IO.Compression;
using System.Xml;

namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Minimal Open Packaging Convention reader — the container format
/// shared by docx, xlsx, pptx. Wraps a
/// <see cref="ZipArchive"/> and exposes helpers for reading the
/// package's structural manifests: <c>[Content_Types].xml</c> (which
/// maps part paths to content types) and <c>_rels/.rels</c> (which
/// identifies the main document part). Higher-level format readers
/// layer on top of this.
///
/// <para>Hand-rolled rather than using <see cref="System.IO.Packaging"/>
/// because that type ships as a separate NuGet package; the repo
/// policy forbids third-party package references in product code.</para>
/// </summary>
internal sealed class OpcPackage : IDisposable
{
    private readonly MemoryStream _stream;
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, string> _overrides;
    private readonly Dictionary<string, string> _defaults;

    /// <summary>Absolute path inside the package (always starting
    /// with <c>/</c>) of the <c>officeDocument</c> relationship
    /// target — <c>word/document.xml</c> for docx,
    /// <c>xl/workbook.xml</c> for xlsx. Derived once at open time
    /// from <c>_rels/.rels</c>.</summary>
    internal string MainDocumentPath { get; }

    private OpcPackage(
        MemoryStream stream, ZipArchive zip,
        Dictionary<string, string> overrides,
        Dictionary<string, string> defaults,
        string mainDocumentPath)
    {
        _stream = stream;
        _zip = zip;
        _overrides = overrides;
        _defaults = defaults;
        MainDocumentPath = mainDocumentPath;
    }

    public static OpcPackage Open(byte[] body)
    {
        // ZipArchive expects a seekable stream and doesn't take
        // ownership of the buffer — wrap the body in a new
        // MemoryStream and keep both alive together for the life
        // of the package.
        var ms = new MemoryStream(body, writable: false);
        var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var (overrides, defaults) = ReadContentTypes(zip);
        var mainPath = ResolveMainDocument(zip);
        return new OpcPackage(ms, zip, overrides, defaults, mainPath);
    }

    /// <summary>Open the named part as a stream. Part paths in the
    /// OPC are relative to the package root and may or may not
    /// start with a leading slash — callers can pass either form;
    /// we normalize before looking up.</summary>
    /// <exception cref="FileNotFoundException">When the part isn't
    /// in the package.</exception>
    internal Stream OpenPart(string partPath)
    {
        var normalized = Normalize(partPath);
        var entry = _zip.GetEntry(normalized)
            ?? throw new FileNotFoundException(
                $"OPC part not found: {partPath}", partPath);
        return entry.Open();
    }

    /// <summary>Like <see cref="OpenPart"/> but returns null for a
    /// missing part. Useful for the many OOXML parts that are
    /// optional (styles, numbering, theme, etc.).</summary>
    internal Stream? TryOpenPart(string partPath)
    {
        var normalized = Normalize(partPath);
        return _zip.GetEntry(normalized)?.Open();
    }

    /// <summary>True when the package contains a part at the given
    /// path. Useful for format-detection beyond
    /// [Content_Types].xml.</summary>
    internal bool HasPart(string partPath)
        => _zip.GetEntry(Normalize(partPath)) is not null;

    /// <summary>Find the content type for a part path. Consults the
    /// <c>&lt;Override&gt;</c> entries first, falling back to the
    /// <c>&lt;Default&gt;</c> map keyed on extension.</summary>
    internal string? GetContentType(string partPath)
    {
        var key = "/" + Normalize(partPath);
        if (_overrides.TryGetValue(key, out var v)) return v;
        int dot = partPath.LastIndexOf('.');
        if (dot < 0) return null;
        var ext = partPath[(dot + 1)..].ToLowerInvariant();
        return _defaults.TryGetValue(ext, out var d) ? d : null;
    }

    /// <summary>Enumerate every part whose content type matches
    /// <paramref name="contentType"/> (exact, case-sensitive). Used
    /// by the workbook reader to locate the single main-workbook
    /// part without threading the path through.</summary>
    internal IEnumerable<string> PartsOfType(string contentType)
    {
        foreach (var (path, ct) in _overrides)
        {
            if (ct == contentType) yield return path.TrimStart('/');
        }
        foreach (var entry in _zip.Entries)
        {
            int dot = entry.FullName.LastIndexOf('.');
            if (dot < 0) continue;
            var ext = entry.FullName[(dot + 1)..].ToLowerInvariant();
            if (_defaults.TryGetValue(ext, out var d) && d == contentType)
            {
                // Only return if not already yielded via override.
                var key = "/" + Normalize(entry.FullName);
                if (!_overrides.ContainsKey(key))
                    yield return entry.FullName;
            }
        }
    }

    /// <summary>Read <c>[Content_Types].xml</c>; returns
    /// (overrides by normalized part path, defaults by lowercase
    /// extension). An empty dictionary for each map is a valid
    /// result — the caller uses the main-document path resolution
    /// as the real canary.</summary>
    private static (Dictionary<string, string> overrides,
                    Dictionary<string, string> defaults)
        ReadContentTypes(ZipArchive zip)
    {
        var overrides = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var defaults = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var entry = zip.GetEntry("[Content_Types].xml");
        if (entry is null) return (overrides, defaults);
        using var stream = entry.Open();
        using var xr = XmlReader.Create(stream, NewReaderSettings());
        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.LocalName == "Override")
            {
                var part = xr.GetAttribute("PartName");
                var ct = xr.GetAttribute("ContentType");
                if (!string.IsNullOrEmpty(part) && !string.IsNullOrEmpty(ct))
                    overrides[part] = ct;
            }
            else if (xr.LocalName == "Default")
            {
                var ext = xr.GetAttribute("Extension");
                var ct = xr.GetAttribute("ContentType");
                if (!string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(ct))
                    defaults[ext.ToLowerInvariant()] = ct;
            }
        }
        return (overrides, defaults);
    }

    /// <summary>Read <c>_rels/.rels</c> and return the package
    /// relationship whose Type is <c>officeDocument</c>. Targets
    /// land as paths relative to the package root (leading slash
    /// stripped).</summary>
    private static string ResolveMainDocument(ZipArchive zip)
    {
        var entry = zip.GetEntry("_rels/.rels")
            ?? throw new InvalidDataException(
                "OPC package missing required _rels/.rels part.");
        using var stream = entry.Open();
        using var xr = XmlReader.Create(stream, NewReaderSettings());
        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.LocalName != "Relationship") continue;
            var type = xr.GetAttribute("Type") ?? "";
            var target = xr.GetAttribute("Target") ?? "";
            if (type.EndsWith("/officeDocument", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(target))
            {
                return Normalize(target);
            }
        }
        throw new InvalidDataException(
            "OPC package _rels/.rels has no officeDocument relationship.");
    }

    /// <summary>Normalize a part path by trimming leading slashes
    /// and folding back-slashes to forward. ZipArchive uses
    /// forward-slash paths without leading slash.</summary>
    internal static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var s = path.Replace('\\', '/');
        while (s.StartsWith('/')) s = s[1..];
        return s;
    }

    /// <summary>Resolve a relative part path (<c>worksheets/sheet1.xml</c>)
    /// against a containing part (<c>xl/workbook.xml</c>) to the
    /// absolute package path (<c>xl/worksheets/sheet1.xml</c>).
    /// Handles absolute targets (leading slash means
    /// package-rooted).</summary>
    internal static string ResolveRelative(string basePart, string target)
    {
        if (string.IsNullOrEmpty(target)) return basePart;
        if (target.StartsWith('/') || target.StartsWith('\\')) return Normalize(target);
        int slash = basePart.LastIndexOf('/');
        var baseDir = slash < 0 ? "" : basePart[..slash];
        var combined = string.IsNullOrEmpty(baseDir)
            ? target : baseDir + "/" + target;
        return Normalize(CollapseDots(combined));
    }

    private static string CollapseDots(string path)
    {
        var parts = path.Split('/', StringSplitOptions.None).ToList();
        var stack = new List<string>(parts.Count);
        foreach (var p in parts)
        {
            if (p == "" || p == ".") continue;
            if (p == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(p);
        }
        return string.Join('/', stack);
    }

    internal static XmlReaderSettings NewReaderSettings() => new()
    {
        // Untrusted input — never resolve DTDs / external entities.
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
    };

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }
}
