using System.Xml;

namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Resolves <c>r:id</c> → target URL / internal part path by parsing
/// the relationships file that lives alongside the main document
/// (<c>word/_rels/document.xml.rels</c>). Used primarily for
/// hyperlinks (external targets stored with
/// <c>TargetMode="External"</c>) and embedded image references.
/// </summary>
internal sealed class DocxRelationships
{
    private readonly Dictionary<string, Relationship> _byId;
    private readonly string _basePart;

    private DocxRelationships(
        Dictionary<string, Relationship> byId, string basePart)
    {
        _byId = byId;
        _basePart = basePart;
    }

    /// <summary>Look up a target by its rId. Returns null when the
    /// rId isn't in the relationships file — callers should just
    /// drop the reference rather than fail.</summary>
    internal Relationship? GetById(string? rid)
    {
        if (string.IsNullOrEmpty(rid)) return null;
        return _byId.TryGetValue(rid, out var rel) ? rel : null;
    }

    /// <summary>Resolve an internal target to a package-root-relative
    /// part path. External targets are returned as-is.</summary>
    internal string Resolve(Relationship rel) => rel.External
        ? rel.Target
        : OpcPackage.ResolveRelative(_basePart, rel.Target);

    internal static DocxRelationships Load(OpcPackage package, string basePart)
    {
        var relsPath = RelsPathFor(basePart);
        var byId = new Dictionary<string, Relationship>(StringComparer.Ordinal);
        using var stream = package.TryOpenPart(relsPath);
        if (stream is null) return new DocxRelationships(byId, basePart);
        using var xr = XmlReader.Create(stream, OpcPackage.NewReaderSettings());
        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.LocalName != "Relationship") continue;
            var id = xr.GetAttribute("Id");
            var type = xr.GetAttribute("Type");
            var target = xr.GetAttribute("Target");
            var mode = xr.GetAttribute("TargetMode");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target)) continue;
            byId[id] = new Relationship(
                id, type ?? "", target,
                External: string.Equals(mode, "External", StringComparison.Ordinal));
        }
        return new DocxRelationships(byId, basePart);
    }

    /// <summary>Convention: for a part at <c>foo/bar.xml</c>, its
    /// relationships file lives at <c>foo/_rels/bar.xml.rels</c>.</summary>
    internal static string RelsPathFor(string partPath)
    {
        partPath = OpcPackage.Normalize(partPath);
        int slash = partPath.LastIndexOf('/');
        var dir = slash < 0 ? "" : partPath[..slash];
        var name = slash < 0 ? partPath : partPath[(slash + 1)..];
        if (string.IsNullOrEmpty(dir)) return "_rels/" + name + ".rels";
        return dir + "/_rels/" + name + ".rels";
    }

    internal readonly record struct Relationship(
        string Id, string Type, string Target, bool External);
}
