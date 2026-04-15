using System.Xml;
using Daisi.Broski.Docs.Docx;

namespace Daisi.Broski.Docs.Xlsx;

/// <summary>
/// Reads <c>xl/styles.xml</c> with only enough fidelity to decide
/// "is this cell's style a date format?" Cells whose style index
/// points at a built-in date numFmt (14-22, 27-36, 45-47, 50-58) or
/// a custom numFmt string containing <c>y</c>, <c>m</c>, <c>d</c>,
/// <c>h</c>, <c>s</c> in the date-pattern positions render via a
/// date formatter rather than as a raw number. Everything else
/// renders as the cell's text representation.
/// </summary>
internal sealed class XlsxStyles
{
    private readonly int[] _styleToNumFmt;
    private readonly Dictionary<int, string> _customFormats;

    private XlsxStyles(int[] styleToNumFmt, Dictionary<int, string> customFormats)
    {
        _styleToNumFmt = styleToNumFmt;
        _customFormats = customFormats;
    }

    /// <summary>True when cell style index <paramref name="styleIdx"/>
    /// resolves to a date/time format.</summary>
    internal bool IsDateStyle(int styleIdx)
    {
        if (styleIdx < 0 || styleIdx >= _styleToNumFmt.Length) return false;
        int fmtId = _styleToNumFmt[styleIdx];
        if (IsBuiltinDateNumFmt(fmtId)) return true;
        if (_customFormats.TryGetValue(fmtId, out var custom))
            return LooksLikeDateFormat(custom);
        return false;
    }

    private static bool IsBuiltinDateNumFmt(int fmtId) => fmtId switch
    {
        14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 => true,
        27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35 or 36 => true,
        45 or 46 or 47 => true,
        50 or 51 or 52 or 53 or 54 or 55 or 56 or 57 or 58 => true,
        _ => false,
    };

    /// <summary>A very light heuristic: a format string with any of
    /// <c>y</c> / <c>m</c> / <c>d</c> / <c>h</c> / <c>s</c> outside
    /// a quoted literal is a date/time. False positives (a number
    /// format like <c>"Mbps"</c>) are rare and would just render as
    /// a date instead of a number — still readable.</summary>
    internal static bool LooksLikeDateFormat(string fmt)
    {
        if (string.IsNullOrEmpty(fmt)) return false;
        bool inQuotes = false;
        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (inQuotes) continue;
            if (c is 'y' or 'Y' or 'd' or 'D' or 's' or 'S') return true;
            // 'm' / 'M' is ambiguous in xlsx — it means minutes when
            // adjacent to h:, month otherwise. Presence of any 'm'
            // alone implies a date/time.
            if (c is 'm' or 'M') return true;
            if (c is 'h' or 'H') return true;
        }
        return false;
    }

    internal static XlsxStyles Load(OpcPackage package)
    {
        var path = OpcPackage.ResolveRelative(
            package.MainDocumentPath, "styles.xml");
        using var stream = package.TryOpenPart(path);
        if (stream is null)
            return new XlsxStyles(Array.Empty<int>(), new());

        var customFormats = new Dictionary<int, string>();
        var styleToNumFmt = new List<int>();
        using var xr = XmlReader.Create(stream, OpcPackage.NewReaderSettings());

        // Two sections we care about:
        //  <numFmts><numFmt numFmtId="N" formatCode="..."/>...</numFmts>
        //  <cellXfs><xf numFmtId="N" applyNumberFormat="1"/>...</cellXfs>
        // Everything else (fonts, fills, borders, cellStyleXfs) is skipped.
        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            switch (xr.LocalName)
            {
                case "numFmt":
                    {
                        var idStr = xr.GetAttribute("numFmtId");
                        var code = xr.GetAttribute("formatCode");
                        if (int.TryParse(idStr, out int id) && code is not null)
                            customFormats[id] = code;
                        break;
                    }
                case "cellXfs":
                    ReadCellXfs(xr, styleToNumFmt);
                    break;
            }
        }
        return new XlsxStyles(styleToNumFmt.ToArray(), customFormats);
    }

    private static void ReadCellXfs(XmlReader xr, List<int> styleToNumFmt)
    {
        if (xr.IsEmptyElement) return;
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.LocalName != "xf") continue;
            var idStr = xr.GetAttribute("numFmtId");
            styleToNumFmt.Add(int.TryParse(idStr, out int id) ? id : 0);
            if (!xr.IsEmptyElement) xr.Skip();
        }
    }
}

/// <summary>Compatibility alias — the main converter calls
/// <c>StylesReader.Load</c>; keep the class-as-namespace shape
/// the dispatcher + test code expect while the real parser lives
/// on <see cref="XlsxStyles"/>.</summary>
internal static class StylesReader
{
    internal static XlsxStyles Load(OpcPackage package) => XlsxStyles.Load(package);
}
