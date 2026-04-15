using System.Xml;

namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Reads <c>docProps/core.xml</c> — the OPC "core properties" part,
/// shared by docx / xlsx / pptx — for the document title, author, and
/// creation date. Used by both converters to populate the synthetic
/// HTML head and by the article extractor's metadata pass.
/// </summary>
internal static class CorePropertiesReader
{
    internal sealed class CoreProperties
    {
        public string? Title { get; init; }
        public string? Creator { get; init; }
        public string? Created { get; init; }
        public string? Description { get; init; }
    }

    internal static CoreProperties Load(OpcPackage package)
    {
        using var stream = package.TryOpenPart("docProps/core.xml");
        if (stream is null) return new CoreProperties();
        using var xr = XmlReader.Create(stream, OpcPackage.NewReaderSettings());

        string? title = null, creator = null, created = null, desc = null;
        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            // Core properties use two namespaces (dc: for the Dublin
            // Core items, dcterms: for created/modified). The
            // element's LocalName alone is unambiguous here.
            string? value = null;
            switch (xr.LocalName)
            {
                case "title":
                    value = ReadElementValue(xr);
                    title = value;
                    break;
                case "creator":
                    value = ReadElementValue(xr);
                    creator = value;
                    break;
                case "created":
                    value = ReadElementValue(xr);
                    created = value;
                    break;
                case "description":
                    value = ReadElementValue(xr);
                    desc = value;
                    break;
            }
        }
        return new CoreProperties
        {
            Title = NullIfEmpty(title),
            Creator = NullIfEmpty(creator),
            Created = NullIfEmpty(created),
            Description = NullIfEmpty(desc),
        };
    }

    private static string ReadElementValue(XmlReader xr)
    {
        if (xr.IsEmptyElement) return "";
        return xr.ReadElementContentAsString();
    }

    private static string? NullIfEmpty(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
