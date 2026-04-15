using System.Text;

namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Emits a synthetic article HTML document from a flat docx block
/// list. Consecutive list items collapse into a single
/// <c>&lt;ul&gt;</c> or <c>&lt;ol&gt;</c>. Nested lists (an ilvl
/// higher than the previous item's) open a nested list inside the
/// current item. Headings, paragraphs, and tables pass through as
/// the obvious HTML equivalents.
/// </summary>
internal static class DocxHtmlEmitter
{
    internal static string Render(
        IReadOnlyList<DocxBlock> blocks,
        CorePropertiesReader.CoreProperties core,
        Uri sourceUrl)
    {
        var sb = new StringBuilder(1024);
        sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">");
        string title = core.Title ?? DeriveFilenameTitle(sourceUrl);
        sb.Append("<title>");
        HtmlWriter.AppendEscaped(sb, title);
        sb.Append("</title>");
        if (!string.IsNullOrEmpty(core.Creator))
        {
            sb.Append("<meta name=\"author\" content=\"");
            sb.Append(HtmlWriter.EscapeAttr(core.Creator));
            sb.Append("\">");
        }
        if (!string.IsNullOrEmpty(core.Created))
        {
            sb.Append("<meta property=\"article:published_time\" content=\"");
            sb.Append(HtmlWriter.EscapeAttr(core.Created));
            sb.Append("\">");
        }
        if (!string.IsNullOrEmpty(core.Description))
        {
            sb.Append("<meta name=\"description\" content=\"");
            sb.Append(HtmlWriter.EscapeAttr(core.Description));
            sb.Append("\">");
        }
        sb.Append("</head><body><article>");
        // Always emit an <h1> so the extractor has a clear title
        // signal even when the body lacks an explicit heading.
        if (!HasLeadingHeading(blocks))
        {
            sb.Append("<h1>");
            HtmlWriter.AppendEscaped(sb, title);
            sb.Append("</h1>");
        }
        RenderBlocks(sb, blocks);
        sb.Append("</article></body></html>");
        return sb.ToString();
    }

    private static bool HasLeadingHeading(IReadOnlyList<DocxBlock> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is DocxHeading) return true;
            // Allow leading empty paragraphs.
            if (b is DocxParagraph p && p.Inlines.Count == 0) continue;
            return false;
        }
        return false;
    }

    private static void RenderBlocks(StringBuilder sb, IReadOnlyList<DocxBlock> blocks)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b is DocxListItem)
            {
                i = RenderList(sb, blocks, i) - 1;
            }
            else
            {
                RenderBlock(sb, b);
            }
        }
    }

    /// <summary>Render the run of consecutive list items starting at
    /// <paramref name="start"/> as a nested list structure; return
    /// the index of the first non-list-item block after the run.</summary>
    private static int RenderList(
        StringBuilder sb, IReadOnlyList<DocxBlock> blocks, int start)
    {
        var first = (DocxListItem)blocks[start];
        bool ordered = first.Ordered;
        sb.Append(ordered ? "<ol>" : "<ul>");
        int i = start;
        while (i < blocks.Count && blocks[i] is DocxListItem item
               && item.Ordered == ordered && item.Level == first.Level)
        {
            sb.Append("<li>");
            RenderInlines(sb, item.Inlines);
            // Peek for nested items (higher level).
            if (i + 1 < blocks.Count
                && blocks[i + 1] is DocxListItem nxt
                && nxt.Level > first.Level)
            {
                int nested = RenderList(sb, blocks, i + 1);
                i = nested - 1;
            }
            sb.Append("</li>");
            i++;
        }
        sb.Append(ordered ? "</ol>" : "</ul>");
        return i;
    }

    private static void RenderBlock(StringBuilder sb, DocxBlock block)
    {
        switch (block)
        {
            case DocxHeading h:
                int lvl = Math.Clamp(h.Level, 1, 6);
                sb.Append("<h").Append(lvl).Append('>');
                RenderInlines(sb, h.Inlines);
                sb.Append("</h").Append(lvl).Append('>');
                break;
            case DocxParagraph p:
                if (p.Inlines.Count == 0) break;
                sb.Append("<p>");
                RenderInlines(sb, p.Inlines);
                sb.Append("</p>");
                break;
            case DocxTable t:
                RenderTable(sb, t);
                break;
        }
    }

    private static void RenderTable(StringBuilder sb, DocxTable table)
    {
        if (table.Rows.Count == 0) return;
        sb.Append("<table>");
        // Plain <tr><td> throughout — docx has no structural header
        // marker (styled header rows are a visual convention, not a
        // schema distinction), so inventing <thead>/<th> would
        // misrepresent the document in the common case. The
        // skimmer's extractor doesn't depend on that distinction.
        foreach (var row in table.Rows)
        {
            sb.Append("<tr>");
            foreach (var cell in row.Cells)
            {
                sb.Append("<td>");
                RenderBlocks(sb, cell.Blocks);
                sb.Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }

    private static void RenderInlines(StringBuilder sb, IReadOnlyList<DocxInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case DocxTextRun text:
                    RenderTextRun(sb, text);
                    break;
                case DocxHyperlink hl:
                    if (!string.IsNullOrEmpty(hl.Href))
                    {
                        sb.Append("<a href=\"");
                        sb.Append(HtmlWriter.EscapeAttr(hl.Href));
                        sb.Append("\">");
                        RenderInlines(sb, hl.Inlines);
                        sb.Append("</a>");
                    }
                    else
                    {
                        RenderInlines(sb, hl.Inlines);
                    }
                    break;
                case DocxLineBreak:
                    sb.Append("<br>");
                    break;
            }
        }
    }

    private static void RenderTextRun(StringBuilder sb, DocxTextRun run)
    {
        if (run.Text.Length == 0) return;
        if (run.Bold) sb.Append("<strong>");
        if (run.Italic) sb.Append("<em>");
        if (run.Underline) sb.Append("<u>");
        HtmlWriter.AppendEscaped(sb, run.Text);
        if (run.Underline) sb.Append("</u>");
        if (run.Italic) sb.Append("</em>");
        if (run.Bold) sb.Append("</strong>");
    }

    private static string DeriveFilenameTitle(Uri sourceUrl)
    {
        string path = sourceUrl.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string name = slash < 0 ? path : path[(slash + 1)..];
        if (name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        name = Uri.UnescapeDataString(name);
        return string.IsNullOrWhiteSpace(name) ? "Document" : name;
    }
}
