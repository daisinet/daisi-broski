using System.Xml;

namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Streams <c>word/document.xml</c> and produces the flat block list
/// that the HTML emitter consumes. Paragraphs become paragraphs,
/// headings, or list items based on the style + numPr lookup;
/// tables carry nested blocks per cell; runs carry the formatting
/// we preserve (<c>b</c>, <c>i</c>, <c>u</c>).
///
/// <para>Unknown wordprocessingml elements are walked transparently
/// so their text content still shows up. This keeps the converter
/// usable against the long tail of odd-but-valid docx files without
/// needing every feature fully modeled.</para>
/// </summary>
internal static class DocxBodyReader
{
    private const string NsWord =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string NsRel =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    internal static IReadOnlyList<DocxBlock> Read(
        OpcPackage package,
        DocxStyles styles,
        DocxNumbering numbering,
        DocxRelationships relationships)
    {
        var blocks = new List<DocxBlock>();
        using var stream = package.OpenPart(package.MainDocumentPath);
        using var xr = XmlReader.Create(stream, OpcPackage.NewReaderSettings());
        // Advance to <w:body>. The document part is always
        // <w:document><w:body>...</w:body></w:document>.
        if (!AdvanceTo(xr, "body", NsWord)) return blocks;
        // Read body children.
        if (xr.IsEmptyElement) return blocks;
        var bodyDepth = xr.Depth;
        while (xr.Read() && xr.Depth > bodyDepth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) { xr.Skip(); continue; }
            switch (xr.LocalName)
            {
                case "p":
                    blocks.Add(ReadParagraph(xr, styles, numbering, relationships));
                    break;
                case "tbl":
                    blocks.Add(ReadTable(xr, styles, numbering, relationships));
                    break;
                case "sectPr":
                    // Section properties have no text content.
                    xr.Skip();
                    break;
                default:
                    // Unknown top-level: walk its descendants and
                    // collect any text into a paragraph. Beats
                    // silently dropping content.
                    var inlines = CollectFallbackInlines(xr);
                    if (inlines.Count > 0)
                        blocks.Add(new DocxParagraph(inlines));
                    break;
            }
        }
        return blocks;
    }

    private static DocxBlock ReadParagraph(
        XmlReader xr, DocxStyles styles, DocxNumbering numbering,
        DocxRelationships relationships)
    {
        int? headingLevel = null;
        (int numId, int ilvl)? numPr = null;
        var inlines = new List<DocxInline>();

        if (xr.IsEmptyElement) return new DocxParagraph(inlines);
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) { xr.Skip(); continue; }
            switch (xr.LocalName)
            {
                case "pPr":
                    (headingLevel, numPr) = ReadParagraphProperties(xr, styles);
                    break;
                case "r":
                    inlines.AddRange(ReadRun(xr, defaultBold: false,
                        defaultItalic: false, defaultUnderline: false));
                    break;
                case "hyperlink":
                    inlines.Add(ReadHyperlink(xr, relationships));
                    break;
                default:
                    // Drop unknown pPr-siblings (bookmarks, ranges,
                    // etc.) but keep their text.
                    inlines.AddRange(CollectFallbackInlines(xr));
                    break;
            }
        }
        if (headingLevel is int h) return new DocxHeading(h, inlines);
        if (numPr is { } np)
        {
            bool ordered = numbering.IsOrdered(np.numId, np.ilvl);
            return new DocxListItem(ordered, np.ilvl, inlines);
        }
        return new DocxParagraph(inlines);
    }

    private static (int? heading, (int numId, int ilvl)? numPr)
        ReadParagraphProperties(XmlReader xr, DocxStyles styles)
    {
        int? heading = null;
        int? numId = null;
        int? ilvl = null;
        if (xr.IsEmptyElement) return (null, null);
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) continue;
            switch (xr.LocalName)
            {
                case "pStyle":
                    var styleId = xr.GetAttribute("val", NsWord);
                    heading = styles.HeadingLevelFor(styleId);
                    break;
                case "outlineLvl":
                    var lvlVal = xr.GetAttribute("val", NsWord);
                    if (int.TryParse(lvlVal, out int ov)
                        && ov >= 0 && ov < 6)
                        heading = ov + 1;
                    break;
                case "numPr":
                    (numId, ilvl) = ReadNumPr(xr);
                    break;
            }
        }
        (int numId, int ilvl)? np = numId is int n && ilvl is int l
            ? (n, l) : null;
        // If the style implies a heading, heading wins over list —
        // Word's default behavior.
        return (heading, heading is null ? np : null);
    }

    private static (int? numId, int? ilvl) ReadNumPr(XmlReader xr)
    {
        int? numId = null;
        int? ilvl = 0;
        if (xr.IsEmptyElement) return (null, null);
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) continue;
            var val = xr.GetAttribute("val", NsWord);
            if (xr.LocalName == "numId" && int.TryParse(val, out int nid))
                numId = nid;
            else if (xr.LocalName == "ilvl" && int.TryParse(val, out int iv))
                ilvl = iv;
        }
        return (numId, ilvl);
    }

    private static IEnumerable<DocxInline> ReadRun(
        XmlReader xr, bool defaultBold, bool defaultItalic, bool defaultUnderline)
    {
        bool bold = defaultBold, italic = defaultItalic, underline = defaultUnderline;
        var inlines = new List<DocxInline>();
        if (xr.IsEmptyElement) return inlines;
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) continue;
            switch (xr.LocalName)
            {
                case "rPr":
                    (bold, italic, underline) = ReadRunProperties(
                        xr, bold, italic, underline);
                    break;
                case "t":
                    // ReadElementContentAsString() advances past the
                    // EndElement, which then makes the outer loop's
                    // Read() skip the next sibling (a <w:br/> after
                    // a <w:t>, for instance). Read text manually so
                    // we stop on the EndElement and let the outer
                    // loop advance normally.
                    var text = ReadElementText(xr);
                    inlines.Add(new DocxTextRun(text, bold, italic, underline));
                    break;
                case "tab":
                    inlines.Add(new DocxTextRun("\t", bold, italic, underline));
                    break;
                case "br":
                    inlines.Add(new DocxLineBreak());
                    break;
                default:
                    // Drop other run children — drawing, fldChar,
                    // symbol, etc. Their meaningful text (if any)
                    // comes through in adjacent runs.
                    xr.Skip();
                    break;
            }
        }
        return inlines;
    }

    private static (bool bold, bool italic, bool underline) ReadRunProperties(
        XmlReader xr, bool bold, bool italic, bool underline)
    {
        if (xr.IsEmptyElement) return (bold, italic, underline);
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) continue;
            switch (xr.LocalName)
            {
                case "b": bold = BoolVal(xr); break;
                case "i": italic = BoolVal(xr); break;
                case "u":
                    // <w:u w:val="none"/> explicitly disables.
                    var uv = xr.GetAttribute("val", NsWord);
                    underline = !string.Equals(uv, "none", StringComparison.Ordinal);
                    break;
            }
        }
        return (bold, italic, underline);
    }

    /// <summary>A <c>&lt;w:b/&gt;</c> without any attribute means
    /// "on"; <c>&lt;w:b w:val="0"/&gt;</c> or <c>"false"</c> means
    /// off. Matches Word's interpretation.</summary>
    private static bool BoolVal(XmlReader xr)
    {
        var v = xr.GetAttribute("val", NsWord);
        if (string.IsNullOrEmpty(v)) return true;
        return v is not ("0" or "false");
    }

    private static DocxInline ReadHyperlink(
        XmlReader xr, DocxRelationships relationships)
    {
        var rid = xr.GetAttribute("id", NsRel);
        string? href = null;
        if (!string.IsNullOrEmpty(rid))
        {
            var rel = relationships.GetById(rid);
            if (rel is { } r) href = r.External ? r.Target : relationships.Resolve(r);
        }
        // Anchor-only links (intra-document) — carry the anchor as
        // an #anchor fragment so at least the text shows up in
        // markdown. Absolute external URL is what matters for
        // article navigation.
        if (href is null)
        {
            var anchor = xr.GetAttribute("anchor", NsWord);
            if (!string.IsNullOrEmpty(anchor)) href = "#" + anchor;
        }
        var inlines = new List<DocxInline>();
        if (xr.IsEmptyElement) return new DocxHyperlink(href ?? "", inlines);
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) continue;
            if (xr.LocalName == "r")
                inlines.AddRange(ReadRun(xr, false, false, false));
            else
                xr.Skip();
        }
        return new DocxHyperlink(href ?? "", inlines);
    }

    private static DocxBlock ReadTable(
        XmlReader xr, DocxStyles styles, DocxNumbering numbering,
        DocxRelationships relationships)
    {
        var rows = new List<DocxTableRow>();
        if (xr.IsEmptyElement) return new DocxTable(rows);
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) { xr.Skip(); continue; }
            if (xr.LocalName == "tr")
                rows.Add(ReadTableRow(xr, styles, numbering, relationships));
            else
                xr.Skip();
        }
        return new DocxTable(rows);
    }

    private static DocxTableRow ReadTableRow(
        XmlReader xr, DocxStyles styles, DocxNumbering numbering,
        DocxRelationships relationships)
    {
        var cells = new List<DocxTableCell>();
        if (xr.IsEmptyElement) return new DocxTableRow(cells);
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) { xr.Skip(); continue; }
            if (xr.LocalName == "tc")
                cells.Add(ReadTableCell(xr, styles, numbering, relationships));
            else
                xr.Skip();
        }
        return new DocxTableRow(cells);
    }

    private static DocxTableCell ReadTableCell(
        XmlReader xr, DocxStyles styles, DocxNumbering numbering,
        DocxRelationships relationships)
    {
        var blocks = new List<DocxBlock>();
        if (xr.IsEmptyElement) return new DocxTableCell(blocks);
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.NamespaceURI != NsWord) { xr.Skip(); continue; }
            switch (xr.LocalName)
            {
                case "p":
                    blocks.Add(ReadParagraph(xr, styles, numbering, relationships));
                    break;
                case "tbl":
                    blocks.Add(ReadTable(xr, styles, numbering, relationships));
                    break;
                default:
                    xr.Skip();
                    break;
            }
        }
        return new DocxTableCell(blocks);
    }

    /// <summary>Collect all descendant text from an unknown element
    /// as one or more text runs. No formatting preserved — we don't
    /// know the element, we shouldn't infer.</summary>
    private static IReadOnlyList<DocxInline> CollectFallbackInlines(XmlReader xr)
    {
        var inlines = new List<DocxInline>();
        if (xr.IsEmptyElement) return inlines;
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType == XmlNodeType.Text || xr.NodeType == XmlNodeType.CDATA)
            {
                inlines.Add(new DocxTextRun(xr.Value ?? "",
                    Bold: false, Italic: false, Underline: false));
            }
        }
        return inlines;
    }

    /// <summary>Read the text content of the current element and
    /// leave the reader positioned on its EndElement — unlike the
    /// built-in <see cref="XmlReader.ReadElementContentAsString()"/>,
    /// which advances past the EndElement and would cause the
    /// caller's depth-scoped loop to skip a sibling on its next
    /// iteration.</summary>
    private static string ReadElementText(XmlReader xr)
    {
        if (xr.IsEmptyElement) return "";
        var sb = new System.Text.StringBuilder();
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

    /// <summary>Advance <paramref name="xr"/> to the next element
    /// with the given local name and namespace. Returns true when
    /// found, false when the document ends first.</summary>
    private static bool AdvanceTo(XmlReader xr, string localName, string ns)
    {
        while (xr.Read())
        {
            if (xr.NodeType == XmlNodeType.Element
                && xr.LocalName == localName
                && xr.NamespaceURI == ns) return true;
        }
        return false;
    }
}
