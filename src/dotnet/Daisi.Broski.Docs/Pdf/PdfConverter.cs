using System.Text;

namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// PDF → article HTML converter. Parses the PDF structure
/// (xref, trailer, catalog, pages tree), decodes each page's
/// content stream, extracts text through the text-showing
/// operator interpreter, and emits a synthetic article whose
/// body is one <c>&lt;section&gt;</c> per page with extracted
/// text as <c>&lt;p&gt;</c> paragraphs. Title comes from the
/// <c>/Info /Title</c> entry when available, otherwise from the
/// URL filename.
///
/// <para>Milestone 2: covers PDFs that use the traditional xref
/// table, Flate-compressed content streams, and simple fonts
/// (Type1 / TrueType with StandardEncoding / WinAnsi / MacRoman
/// + Differences). Files that require object-stream xrefs,
/// composite CID fonts with ToUnicode CMaps, or encryption land
/// on the fallback path and emit the identified-but-unsupported
/// shell, so the CLI never returns "no content".</para>
/// </summary>
internal sealed class PdfConverter : IDocConverter
{
    public string Convert(byte[] body, Uri sourceUrl)
    {
        ArgumentNullException.ThrowIfNull(sourceUrl);
        try
        {
            var doc = PdfDocument.Load(body);
            var title = ExtractTitle(doc, sourceUrl);
            var author = ExtractInfoString(doc, "Author");
            var created = ExtractInfoString(doc, "CreationDate");
            var pages = new List<IReadOnlyList<LayoutBlock>>();
            var pageImages = new List<IReadOnlyList<PdfImage>>();
            foreach (var page in doc.Pages())
            {
                pageImages.Add(page.GetImages());
                var bytesOut = page.DecodedContentStream();
                if (bytesOut.Length == 0)
                {
                    pages.Add(Array.Empty<LayoutBlock>());
                    continue;
                }
                var runs = PdfTextExtractor.ExtractRuns(bytesOut, page.ResolveFont);
                // Overlay the page's /Link annotations onto the
                // runs. Any run whose baseline-left (x, y) falls
                // inside an annotation rect picks up the URI so
                // the HTML emitter can wrap it in <a>.
                runs = ApplyLinkAnnotations(runs, page.GetLinkAnnotations());
                pages.Add(PdfLayoutAnalyzer.Analyze(runs));
            }
            return Render(title, author, created, pages, pageImages, sourceUrl);
        }
        catch (Exception ex) when (
            ex is NotSupportedException
                or InvalidDataException
                or EndOfStreamException
                or IOException)
        {
            // Any failure in the parser surfaces as the
            // identified-but-unsupported shell. The user gets a
            // skim that tells them the URL served a PDF and that
            // full text extraction wasn't possible for this file
            // — typical causes are PDF 1.5+ xref streams (not yet
            // supported), encrypted PDFs, or image-only scanned
            // PDFs.
            return RenderUnsupported(sourceUrl, reason: ex.Message);
        }
    }

    /// <summary>Return a new run list with each run's
    /// <c>Href</c> populated when its baseline-left coordinate
    /// falls inside any link-annotation rect. First-match wins
    /// when multiple annotations overlap — real-world PDFs
    /// rarely stack link annotations, so a simple linear scan is
    /// fine.</summary>
    private static IReadOnlyList<PdfTextRun> ApplyLinkAnnotations(
        IReadOnlyList<PdfTextRun> runs,
        IReadOnlyList<PdfPage.PdfLinkAnnotation> annotations)
    {
        if (annotations.Count == 0) return runs;
        var result = new PdfTextRun[runs.Count];
        for (int i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            string? href = null;
            foreach (var a in annotations)
            {
                if (a.Contains(r.X, r.Y)) { href = a.Uri; break; }
            }
            result[i] = href is null ? r
                : new PdfTextRun(r.X, r.Y, r.Text, href);
        }
        return result;
    }

    // ---------- metadata extraction ----------

    private static string ExtractTitle(PdfDocument doc, Uri sourceUrl)
    {
        var info = doc.Info();
        if (info.Get("Title") is PdfString s && !string.IsNullOrWhiteSpace(s.AsText()))
        {
            return s.AsText();
        }
        return DeriveFilenameTitle(sourceUrl);
    }

    private static string? ExtractInfoString(PdfDocument doc, string key)
    {
        var info = doc.Info();
        if (info.Get(key) is PdfString s)
        {
            var t = s.AsText();
            if (!string.IsNullOrWhiteSpace(t)) return t.Trim();
        }
        return null;
    }

    private static string DeriveFilenameTitle(Uri url)
    {
        string path = url.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string name = slash < 0 ? path : path[(slash + 1)..];
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        name = Uri.UnescapeDataString(name);
        return string.IsNullOrWhiteSpace(name) ? "PDF Document" : name;
    }

    // ---------- HTML rendering ----------

    private static string Render(
        string title, string? author, string? created,
        IReadOnlyList<IReadOnlyList<LayoutBlock>> pages,
        IReadOnlyList<IReadOnlyList<PdfImage>> pageImages,
        Uri sourceUrl)
    {
        var sb = new StringBuilder(1024);
        sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<title>");
        HtmlWriter.AppendEscaped(sb, title);
        sb.Append("</title>");
        if (!string.IsNullOrEmpty(author))
        {
            sb.Append("<meta name=\"author\" content=\"");
            sb.Append(HtmlWriter.EscapeAttr(author));
            sb.Append("\">");
        }
        if (!string.IsNullOrEmpty(created))
        {
            sb.Append("<meta property=\"article:published_time\" content=\"");
            sb.Append(HtmlWriter.EscapeAttr(created));
            sb.Append("\">");
        }
        // First image across all pages becomes the article's
        // hero — the Skimmer's ContentExtractor reads
        // /og:image from the synthetic HTML head and surfaces it
        // through ArticleContent.HeroImage.
        var firstImage = FirstImage(pageImages);
        if (firstImage is not null)
        {
            sb.Append("<meta property=\"og:image\" content=\"");
            sb.Append(HtmlWriter.EscapeAttr(firstImage.ToDataUri()));
            sb.Append("\">");
        }
        sb.Append("</head><body><article>");
        sb.Append("<h1>");
        HtmlWriter.AppendEscaped(sb, title);
        sb.Append("</h1>");

        bool anyTextEmitted = false;
        for (int i = 0; i < pages.Count; i++)
        {
            var blocks = pages[i];
            var images = i < pageImages.Count ? pageImages[i]
                : Array.Empty<PdfImage>();
            if (blocks.Count == 0 && images.Count == 0) continue;
            anyTextEmitted = true;
            sb.Append("<section>");
            if (pages.Count > 1)
            {
                sb.Append("<h2>Page ").Append(i + 1).Append("</h2>");
            }
            foreach (var img in images)
            {
                sb.Append("<img src=\"");
                sb.Append(HtmlWriter.EscapeAttr(img.ToDataUri()));
                sb.Append("\" alt=\"\"");
                if (img.Width > 0)
                    sb.Append(" width=\"").Append(img.Width).Append('"');
                if (img.Height > 0)
                    sb.Append(" height=\"").Append(img.Height).Append('"');
                sb.Append(">");
            }
            foreach (var block in blocks) RenderBlock(sb, block);
            sb.Append("</section>");
        }
        if (!anyTextEmitted)
        {
            sb.Append("<p>This PDF contained no extractable text — typically ");
            sb.Append("means the document is a scan without OCR, or uses a ");
            sb.Append("font-encoding path not yet supported by the converter. ");
            sb.Append("Source: <a href=\"");
            sb.Append(HtmlWriter.EscapeAttr(sourceUrl.AbsoluteUri));
            sb.Append("\">");
            HtmlWriter.AppendEscaped(sb, sourceUrl.AbsoluteUri);
            sb.Append("</a>.</p>");
        }
        sb.Append("</article></body></html>");
        return sb.ToString();
    }

    private static PdfImage? FirstImage(
        IReadOnlyList<IReadOnlyList<PdfImage>> pageImages)
    {
        foreach (var page in pageImages)
        {
            if (page.Count > 0) return page[0];
        }
        return null;
    }

    private static void RenderBlock(StringBuilder sb, LayoutBlock block)
    {
        switch (block)
        {
            case LayoutTable t:
                RenderTable(sb, t);
                break;
            case LayoutParagraph p:
                if (p.Lines.Count == 0) break;
                sb.Append("<p>");
                for (int i = 0; i < p.Lines.Count; i++)
                {
                    if (i > 0) sb.Append("<br>");
                    // Cell/line content arrives from the layout
                    // analyzer already HTML-escaped and with <a>
                    // tags already in place for linked runs. Emit
                    // raw — re-escaping would turn &lt; into
                    // &amp;lt; and clobber link markup.
                    sb.Append(p.Lines[i]);
                }
                sb.Append("</p>");
                break;
        }
    }

    private static void RenderTable(StringBuilder sb, LayoutTable t)
    {
        if (t.Rows.Count == 0) return;
        sb.Append("<table>");
        foreach (var row in t.Rows)
        {
            sb.Append("<tr>");
            foreach (var cell in row)
            {
                sb.Append("<td>");
                sb.Append(cell); // already-escaped HTML; see RenderBlock note
                sb.Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }

    // Old paragraph-from-string renderer retained for the
    // unsupported-shell fallback path that emits literal strings.
    private static void RenderParagraphsLegacy(StringBuilder sb, string text)
    {
        // Split on blank lines (two or more consecutive \n) into
        // paragraph blocks; preserve single \n within a paragraph
        // as <br> so the visible line breaks from the content
        // stream carry through.
        int i = 0;
        while (i < text.Length)
        {
            int paraStart = i;
            int paraEnd = FindParagraphEnd(text, i);
            if (paraEnd > paraStart)
            {
                sb.Append("<p>");
                WriteParagraph(sb, text, paraStart, paraEnd);
                sb.Append("</p>");
            }
            i = SkipBlankLines(text, paraEnd);
        }
    }

    private static int FindParagraphEnd(string text, int start)
    {
        int i = start;
        while (i < text.Length)
        {
            if (text[i] == '\n'
                && i + 1 < text.Length && text[i + 1] == '\n')
            {
                return i;
            }
            i++;
        }
        return text.Length;
    }

    private static int SkipBlankLines(string text, int from)
    {
        int i = from;
        while (i < text.Length && text[i] == '\n') i++;
        return i;
    }

    private static void WriteParagraph(
        StringBuilder sb, string text, int start, int end)
    {
        bool atLineStart = true;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                if (!atLineStart) sb.Append("<br>");
                atLineStart = true;
                continue;
            }
            atLineStart = false;
            HtmlWriter.AppendEscaped(sb, c.ToString());
        }
    }

    /// <summary>Fallback shell emitted when the parser hit a case
    /// it can't handle yet. Keeps the caller's contract: the
    /// skimmer always gets a populated article rather than "no
    /// content found".</summary>
    private static string RenderUnsupported(Uri sourceUrl, string reason)
    {
        var title = DeriveFilenameTitle(sourceUrl);
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<title>");
        HtmlWriter.AppendEscaped(sb, title);
        sb.Append("</title></head><body><article>");
        sb.Append("<h1>");
        HtmlWriter.AppendEscaped(sb, title);
        sb.Append("</h1>");
        sb.Append("<p>This URL served a PDF document that the daisi-broski ");
        sb.Append("PDF parser could not fully read in this build. Typical ");
        sb.Append("causes are PDF 1.5+ cross-reference streams, encrypted ");
        sb.Append("PDFs, or composite CID fonts without a <code>ToUnicode</code> CMap ");
        sb.Append("— all land in a subsequent milestone. Source: <a href=\"");
        sb.Append(HtmlWriter.EscapeAttr(sourceUrl.AbsoluteUri));
        sb.Append("\">");
        HtmlWriter.AppendEscaped(sb, sourceUrl.AbsoluteUri);
        sb.Append("</a>.</p>");
        if (!string.IsNullOrEmpty(reason))
        {
            sb.Append("<p><em>Parser reported: ");
            HtmlWriter.AppendEscaped(sb, reason);
            sb.Append("</em></p>");
        }
        sb.Append("</article></body></html>");
        return sb.ToString();
    }
}
