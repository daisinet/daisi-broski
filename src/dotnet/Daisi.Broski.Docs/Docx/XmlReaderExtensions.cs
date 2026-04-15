using System.Text;
using System.Xml;

namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Shared <see cref="XmlReader"/> helpers used by every OOXML
/// reader in this project. The built-in
/// <see cref="XmlReader.ReadElementContentAsString()"/> advances
/// past the element's EndElement, which breaks the depth-scoped
/// iteration pattern these readers rely on — the caller's next
/// <c>Read()</c> skips a sibling. The helpers here stop exactly
/// on the EndElement so the outer loop stays in sync.
/// </summary>
internal static class XmlReaderExtensions
{
    /// <summary>Read the concatenated text content of the current
    /// element and leave the reader positioned on its EndElement.
    /// Returns <c>""</c> for an empty element without advancing.</summary>
    internal static string ReadInnerText(this XmlReader xr)
    {
        if (xr.IsEmptyElement) return "";
        var sb = new StringBuilder();
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType == XmlNodeType.Text
                || xr.NodeType == XmlNodeType.CDATA
                || xr.NodeType == XmlNodeType.SignificantWhitespace)
            {
                sb.Append(xr.Value);
            }
        }
        return sb.ToString();
    }

    /// <summary>Walk every descendant text node of the current
    /// element and concatenate their <c>Value</c>s. Unlike
    /// <see cref="ReadInnerText"/>, this descends into nested
    /// elements — useful for richly-formatted elements like
    /// <c>&lt;si&gt;</c> (shared-string item) or <c>&lt;is&gt;</c>
    /// (inline string) that may wrap their text in <c>&lt;r&gt;</c>
    /// run elements. Leaves the reader on the EndElement.</summary>
    internal static string ReadDescendantText(this XmlReader xr)
    {
        if (xr.IsEmptyElement) return "";
        var sb = new StringBuilder();
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType == XmlNodeType.Text
                || xr.NodeType == XmlNodeType.CDATA
                || xr.NodeType == XmlNodeType.SignificantWhitespace)
            {
                sb.Append(xr.Value);
            }
        }
        return sb.ToString();
    }
}
