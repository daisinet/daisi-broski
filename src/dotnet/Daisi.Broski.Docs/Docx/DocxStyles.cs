using System.Xml;

namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Loads <c>word/styles.xml</c> into a style-id → style-info table.
/// The body reader consults this table when it sees
/// <c>&lt;w:pStyle w:val="Heading1"/&gt;</c> to decide whether a
/// paragraph becomes an <c>&lt;h1&gt;</c>, a list item, or a plain
/// paragraph.
///
/// <para>Style resolution in real docx is a deep rabbit hole
/// (docDefaults, latent styles, style chains via
/// <c>w:basedOn</c>). This reader handles the common case used by
/// Word and every major exporter: a style chain of at most three
/// hops, heading level derived from <c>w:outlineLvl</c> or a fallback
/// name convention (<c>Heading1</c>, <c>Heading 1</c>). Unresolved
/// chains degrade to "no heading level" — the paragraph becomes a
/// <c>&lt;p&gt;</c>, which is the graceful outcome.</para>
/// </summary>
internal sealed class DocxStyles
{
    private readonly Dictionary<string, Style> _byId;

    private DocxStyles(Dictionary<string, Style> byId) => _byId = byId;

    /// <summary>Look up a style by id. Returns null for unknown ids
    /// (common when a docx references a built-in style that wasn't
    /// explicitly defined in the part).</summary>
    internal Style? Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _byId.TryGetValue(id, out var s) ? s : null;
    }

    /// <summary>Walk the <c>basedOn</c> chain to the first style in
    /// the chain that carries a heading level. Returns null when no
    /// style in the chain is a heading. Cycles (rare but possible
    /// in damaged files) are bounded by a 4-hop cap.</summary>
    internal int? HeadingLevelFor(string? id)
    {
        var s = Get(id);
        for (int hops = 0; s is not null && hops < 4; hops++)
        {
            if (s.HeadingLevel is int lvl) return lvl;
            s = Get(s.BasedOn);
        }
        return null;
    }

    internal static DocxStyles Load(OpcPackage package)
    {
        var stylesPath = OpcPackage.ResolveRelative(
            package.MainDocumentPath, "styles.xml");
        using var stream = package.TryOpenPart(stylesPath);
        if (stream is null) return new DocxStyles(new());
        return new DocxStyles(ReadFrom(stream));
    }

    private static Dictionary<string, Style> ReadFrom(Stream stream)
    {
        var byId = new Dictionary<string, Style>(StringComparer.Ordinal);
        using var xr = XmlReader.Create(stream, OpcPackage.NewReaderSettings());
        string? currentId = null;
        string? currentName = null;
        string? basedOn = null;
        int? outlineLvl = null;
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "style")
            {
                currentId = xr.GetAttribute("styleId", NsWord);
                currentName = null;
                basedOn = null;
                outlineLvl = null;
                if (xr.IsEmptyElement)
                {
                    if (currentId is not null)
                        byId[currentId] = BuildStyle(currentId, currentName, basedOn, outlineLvl);
                }
                continue;
            }
            if (xr.NodeType == XmlNodeType.Element)
            {
                switch (xr.LocalName)
                {
                    case "name":
                        currentName = xr.GetAttribute("val", NsWord);
                        break;
                    case "basedOn":
                        basedOn = xr.GetAttribute("val", NsWord);
                        break;
                    case "outlineLvl":
                        var v = xr.GetAttribute("val", NsWord);
                        if (int.TryParse(v, out int lvl)) outlineLvl = lvl;
                        break;
                }
            }
            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "style")
            {
                if (currentId is not null)
                    byId[currentId] = BuildStyle(currentId, currentName, basedOn, outlineLvl);
                currentId = null;
            }
        }
        return byId;
    }

    private static Style BuildStyle(
        string id, string? name, string? basedOn, int? outlineLvl)
    {
        int? heading = outlineLvl is int o && o >= 0 && o < 6
            ? o + 1
            : NameBasedHeadingLevel(name);
        return new Style(id, name, basedOn, heading);
    }

    /// <summary>Heuristic: Word's built-in Heading styles are named
    /// <c>heading 1</c> through <c>heading 9</c> (the space is
    /// significant); export convention is <c>Heading 1</c>. We
    /// match case-insensitively on either spelling. Returns null
    /// if the name doesn't parse.</summary>
    private static int? NameBasedHeadingLevel(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var n = name.Replace(" ", "", StringComparison.Ordinal)
                    .ToLowerInvariant();
        if (!n.StartsWith("heading", StringComparison.Ordinal)) return null;
        var rest = n[7..];
        if (rest.Length == 0) return null;
        if (int.TryParse(rest, out int lvl) && lvl >= 1 && lvl <= 6) return lvl;
        return null;
    }

    internal sealed record Style(
        string Id, string? Name, string? BasedOn, int? HeadingLevel);

    private const string NsWord =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
}
