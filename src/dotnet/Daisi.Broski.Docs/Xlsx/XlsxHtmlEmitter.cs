using System.Text;
using Daisi.Broski.Docs.Docx;

namespace Daisi.Broski.Docs.Xlsx;

/// <summary>
/// Renders a list of <see cref="RenderedSheet"/> to the synthetic
/// article HTML. One <c>&lt;h2&gt;</c> + <c>&lt;table&gt;</c> per
/// sheet, all wrapped in a single <c>&lt;article&gt;</c> so the
/// extractor latches onto it as the content root. The workbook's
/// title (from core properties, else the filename) becomes the
/// <c>&lt;h1&gt;</c>.
/// </summary>
internal static class XlsxHtmlEmitter
{
    internal static string Render(
        IReadOnlyList<RenderedSheet> sheets,
        CorePropertiesReader.CoreProperties core,
        Uri sourceUrl)
    {
        var sb = new StringBuilder(1024);
        string title = core.Title ?? DeriveFilenameTitle(sourceUrl);

        sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">");
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
        sb.Append("</head><body><article>");
        sb.Append("<h1>");
        HtmlWriter.AppendEscaped(sb, title);
        sb.Append("</h1>");

        foreach (var sheet in sheets)
        {
            sb.Append("<h2>");
            HtmlWriter.AppendEscaped(sb, sheet.Name);
            sb.Append("</h2>");
            RenderTable(sb, sheet.Worksheet);
        }
        sb.Append("</article></body></html>");
        return sb.ToString();
    }

    private static void RenderTable(StringBuilder sb, Worksheet worksheet)
    {
        if (worksheet.Rows.Count == 0)
        {
            sb.Append("<p><em>(empty sheet)</em></p>");
            return;
        }
        // Mark cells subsumed by merges; the anchor cell renders
        // with colspan/rowspan, the rest of the range is elided.
        var suppressed = BuildSuppressMap(worksheet);

        sb.Append("<table>");
        for (int r = 0; r < worksheet.Rows.Count; r++)
        {
            var row = worksheet.Rows[r];
            // Skip rows that ended up entirely blank after padding —
            // they're visual noise in the rendered article.
            if (IsBlankRow(row)) continue;
            sb.Append("<tr>");
            EmitRow(sb, row, r, worksheet.Merges, suppressed);
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }

    private static void EmitRow(
        StringBuilder sb, IReadOnlyList<string> row, int rowIdx,
        IReadOnlyList<MergedRange> merges, HashSet<(int, int)> suppressed)
    {
        for (int c = 0; c < row.Count; c++)
        {
            if (suppressed.Contains((rowIdx, c))) continue;
            int colspan = 1, rowspan = 1;
            var anchor = FindAnchor(merges, rowIdx, c);
            if (anchor is { } m)
            {
                colspan = m.ColEnd - m.ColStart + 1;
                rowspan = m.RowEnd - m.RowStart + 1;
            }
            sb.Append("<td");
            if (colspan > 1) sb.Append(" colspan=\"").Append(colspan).Append('"');
            if (rowspan > 1) sb.Append(" rowspan=\"").Append(rowspan).Append('"');
            sb.Append('>');
            HtmlWriter.AppendEscaped(sb, row[c]);
            sb.Append("</td>");
        }
    }

    private static MergedRange? FindAnchor(
        IReadOnlyList<MergedRange> merges, int r, int c)
    {
        foreach (var m in merges)
        {
            if (m.RowStart == r && m.ColStart == c) return m;
        }
        return null;
    }

    /// <summary>Build the set of (row, col) coordinates that are
    /// subsumed by a merge (anchor excluded). O(merges × cells) but
    /// real-world sheets rarely top 50 merges, so this stays
    /// negligible.</summary>
    private static HashSet<(int, int)> BuildSuppressMap(Worksheet worksheet)
    {
        var s = new HashSet<(int, int)>();
        foreach (var m in worksheet.Merges)
        {
            for (int r = m.RowStart; r <= m.RowEnd; r++)
            {
                for (int c = m.ColStart; c <= m.ColEnd; c++)
                {
                    if (r == m.RowStart && c == m.ColStart) continue;
                    s.Add((r, c));
                }
            }
        }
        return s;
    }

    private static bool IsBlankRow(IReadOnlyList<string> row)
    {
        foreach (var cell in row)
        {
            if (!string.IsNullOrEmpty(cell)) return false;
        }
        return true;
    }

    private static string DeriveFilenameTitle(Uri sourceUrl)
    {
        string path = sourceUrl.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string name = slash < 0 ? path : path[(slash + 1)..];
        if (name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        name = Uri.UnescapeDataString(name);
        return string.IsNullOrWhiteSpace(name) ? "Workbook" : name;
    }
}
