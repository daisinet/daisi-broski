using System.Xml;

namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Resolves <c>&lt;w:numPr&gt;</c> (numId + ilvl) → (ordered?, level).
/// Numbering.xml has two layers: <c>&lt;w:num&gt;</c> instances each
/// reference a <c>&lt;w:abstractNum&gt;</c> definition, and every
/// abstractNum contains per-level <c>&lt;w:lvl&gt;</c> entries with
/// a <c>&lt;w:numFmt w:val="decimal|bullet|..."/&gt;</c> that tells
/// us ordered vs unordered.
///
/// <para>We care only about ordered / unordered — any numFmt other
/// than <c>bullet</c> is treated as ordered. Actual numbering values
/// (<c>1.</c>, <c>a.</c>, Roman, etc.) are rendered by the HTML
/// <c>&lt;ol&gt;</c> default counter, not reconstructed from the
/// abstract format.</para>
/// </summary>
internal sealed class DocxNumbering
{
    private readonly Dictionary<int, AbstractNum> _abstractById;
    private readonly Dictionary<int, int> _numIdToAbstract;

    private DocxNumbering(
        Dictionary<int, AbstractNum> abstractById,
        Dictionary<int, int> numIdToAbstract)
    {
        _abstractById = abstractById;
        _numIdToAbstract = numIdToAbstract;
    }

    /// <summary>True when the <c>numId</c> resolves to an ordered
    /// list at the given ilvl. An unknown numId or ilvl falls back
    /// to unordered (the less surprising default).</summary>
    internal bool IsOrdered(int numId, int ilvl)
    {
        if (!_numIdToAbstract.TryGetValue(numId, out int absId)) return false;
        if (!_abstractById.TryGetValue(absId, out var abs)) return false;
        return abs.IsOrderedAt(ilvl);
    }

    internal static DocxNumbering Load(OpcPackage package)
    {
        var path = OpcPackage.ResolveRelative(
            package.MainDocumentPath, "numbering.xml");
        using var stream = package.TryOpenPart(path);
        if (stream is null) return new DocxNumbering(new(), new());
        return Read(stream);
    }

    private static DocxNumbering Read(Stream stream)
    {
        var abstractById = new Dictionary<int, AbstractNum>();
        var numIdToAbstract = new Dictionary<int, int>();
        using var xr = XmlReader.Create(stream, OpcPackage.NewReaderSettings());

        int? currentAbstractId = null;
        Dictionary<int, bool>? currentLevels = null;
        int? currentLvlIndex = null;
        int? currentNumId = null;

        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element)
            {
                switch (xr.LocalName)
                {
                    case "abstractNum":
                        {
                            var v = xr.GetAttribute("abstractNumId", NsWord);
                            if (int.TryParse(v, out int id))
                            {
                                currentAbstractId = id;
                                currentLevels = new Dictionary<int, bool>();
                            }
                            break;
                        }
                    case "lvl":
                        {
                            var v = xr.GetAttribute("ilvl", NsWord);
                            currentLvlIndex = int.TryParse(v, out int ilvl)
                                ? ilvl : null;
                            break;
                        }
                    case "numFmt":
                        {
                            if (currentLevels is not null && currentLvlIndex is int lvl)
                            {
                                var val = xr.GetAttribute("val", NsWord);
                                currentLevels[lvl] = !string.Equals(
                                    val, "bullet", StringComparison.Ordinal);
                            }
                            break;
                        }
                    case "num":
                        {
                            var v = xr.GetAttribute("numId", NsWord);
                            currentNumId = int.TryParse(v, out int n)
                                ? n : null;
                            break;
                        }
                    case "abstractNumId":
                        {
                            if (currentNumId is int nid)
                            {
                                var v = xr.GetAttribute("val", NsWord);
                                if (int.TryParse(v, out int abs))
                                    numIdToAbstract[nid] = abs;
                            }
                            break;
                        }
                }
            }
            else if (xr.NodeType == XmlNodeType.EndElement)
            {
                switch (xr.LocalName)
                {
                    case "abstractNum":
                        if (currentAbstractId is int abs && currentLevels is not null)
                            abstractById[abs] = new AbstractNum(currentLevels);
                        currentAbstractId = null;
                        currentLevels = null;
                        break;
                    case "lvl":
                        currentLvlIndex = null;
                        break;
                    case "num":
                        currentNumId = null;
                        break;
                }
            }
        }
        return new DocxNumbering(abstractById, numIdToAbstract);
    }

    private sealed record AbstractNum(IReadOnlyDictionary<int, bool> Ordered)
    {
        internal bool IsOrderedAt(int ilvl)
            => Ordered.TryGetValue(ilvl, out var v) && v;
    }

    private const string NsWord =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
}
