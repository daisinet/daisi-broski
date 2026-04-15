using System.Text;
using System.Xml;
using Daisi.Broski.Docs.Docx;

namespace Daisi.Broski.Docs.Xlsx;

/// <summary>
/// Reads <c>xl/sharedStrings.xml</c> into a positional
/// <see cref="string"/> array. Worksheet cells with <c>t="s"</c>
/// reference entries in this array by zero-based index. Inline
/// strings (<c>t="inlineStr"</c>) bypass this part.
///
/// <para>Each string item (<c>&lt;si&gt;</c>) may contain either a
/// single <c>&lt;t&gt;</c> element or a sequence of rich-text runs
/// (<c>&lt;r&gt;…&lt;t&gt;…&lt;/t&gt;…&lt;/r&gt;</c>). We concatenate
/// the text content in either case — rich-text formatting inside
/// sharedStrings is rare outside explicitly styled headers and
/// doesn't survive the round-trip to article text.</para>
/// </summary>
internal static class SharedStringsReader
{
    internal static IReadOnlyList<string> Load(OpcPackage package)
    {
        var path = OpcPackage.ResolveRelative(
            package.MainDocumentPath, "sharedStrings.xml");
        using var stream = package.TryOpenPart(path);
        if (stream is null) return Array.Empty<string>();
        return ReadFrom(stream);
    }

    private static IReadOnlyList<string> ReadFrom(Stream stream)
    {
        var list = new List<string>(256);
        using var xr = XmlReader.Create(stream, OpcPackage.NewReaderSettings());
        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.LocalName != "si") continue;
            list.Add(ReadSharedStringItem(xr));
        }
        return list;
    }

    private static string ReadSharedStringItem(XmlReader xr)
    {
        if (xr.IsEmptyElement) return "";
        var sb = new StringBuilder();
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            // Both <t> directly under <si> and <t> nested inside
            // <r>…</r> rich-text runs carry the visible text.
            // ReadInnerText leaves the reader on the EndElement so
            // the outer depth-scoped loop stays in sync.
            if (xr.LocalName == "t")
            {
                sb.Append(xr.ReadInnerText());
            }
        }
        return sb.ToString();
    }
}
