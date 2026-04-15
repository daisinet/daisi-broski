using System.Text;
using Daisi.Broski.Docs;
using Daisi.Broski.Skimmer;
using Xunit;
using SkimmerApi = Daisi.Broski.Skimmer.Skimmer;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// End-to-end tests for PDF hyperlink extraction. Builds a
/// minimal PDF with a <c>/Link</c> annotation containing a URI
/// action, runs it through the converter, and checks that the
/// synthetic HTML carries an <c>&lt;a href&gt;</c> wrapping the
/// linked text — the Skimmer's ContentExtractor then surfaces
/// it in <c>ArticleContent.Links</c> and the MarkdownFormatter
/// renders it as <c>[text](url)</c>.
/// </summary>
public class PdfLinkTests
{
    private const string PdfCt = "application/pdf";
    private static readonly Uri Url = new("https://example.com/with-links.pdf");

    [Fact]
    public void Pdf_with_link_annotation_emits_anchor_tag()
    {
        var pdf = BuildPdfWithLink(
            linkedText: "Read the docs",
            url: "https://daisi.ai/docs");
        bool ok = DocDispatcher.TryConvert(pdf, PdfCt, Url, out var html);
        Assert.True(ok);
        Assert.Contains("<a href=\"https://daisi.ai/docs\">Read the docs</a>",
            html!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skim_of_linked_pdf_populates_Links_array()
    {
        var pdf = BuildPdfWithLink(
            linkedText: "Official Site",
            url: "https://example.org/landing");
        var article = await SkimmerApi.SkimAsync(
            new Uri("https://example.com/linkdoc.pdf"),
            new SkimmerOptions
            {
                ScriptingEnabled = false,
                Fetcher = new Engine.Net.HttpFetcherOptions
                {
                    Interceptor = _ => new Engine.Net.InterceptedResponse
                    {
                        Status = 200,
                        ContentType = PdfCt,
                        Body = pdf,
                    },
                },
            });
        Assert.Contains(article.Links,
            l => l.Href == "https://example.org/landing"
                 && l.Text.Contains("Official Site"));
    }

    [Fact]
    public void Markdown_output_renders_inline_link_syntax()
    {
        var pdf = BuildPdfWithLink(
            linkedText: "daisi.ai",
            url: "https://daisi.ai");
        bool ok = DocDispatcher.TryConvert(pdf, PdfCt, Url, out var html);
        Assert.True(ok);
        var dom = Daisi.Broski.Engine.Html.HtmlTreeBuilder.Parse(html!);
        var article = Daisi.Broski.Skimmer.ContentExtractor.Extract(dom, Url);
        var md = MarkdownFormatter.Format(article);
        // The URL resolver canonicalizes the bare host to a
        // trailing-slash form; match on that shape.
        Assert.Contains("[daisi.ai](https://daisi.ai/)", md,
            StringComparison.Ordinal);
    }

    // ---------- fixture ----------

    private static byte[] BuildPdfWithLink(string linkedText, string url)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WRaw(byte[] b) => ms.Write(b);

        W("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        long catalogOff = ms.Length;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        long pagesOff = ms.Length;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Page references font F1 and carries an /Annots array
        // with a single /Link annotation whose rect covers the
        // position where the content stream draws the linked
        // text. Tm places text at (100, 700); glyph advance is
        // the caller's problem, so the rect spans a generous
        // ~300 units wide.
        long pageOff = ms.Length;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
          "/Resources << /Font << /F1 4 0 R >> >> " +
          "/Contents 5 0 R /Annots [6 0 R] >>\nendobj\n");

        long fontOff = ms.Length;
        W("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        long contentsOff = ms.Length;
        var cs = Encoding.ASCII.GetBytes(
            $"BT /F1 12 Tf 1 0 0 1 100 700 Tm ({linkedText}) Tj ET");
        W($"5 0 obj\n<< /Length {cs.Length} >>\nstream\n");
        WRaw(cs);
        W("\nendstream\nendobj\n");

        // Link annotation: /Rect covers the baseline position
        // (100, 700) with generous padding. URI action carries
        // the target URL.
        long annotOff = ms.Length;
        W("6 0 obj\n<< /Type /Annot /Subtype /Link /Rect [90 690 400 720] " +
          $"/A << /Type /Action /S /URI /URI ({url}) >> >>\nendobj\n");

        long xrefOff = ms.Length;
        W("xref\n0 7\n");
        W("0000000000 65535 f \n");
        W($"{catalogOff:D10} 00000 n \n");
        W($"{pagesOff:D10} 00000 n \n");
        W($"{pageOff:D10} 00000 n \n");
        W($"{fontOff:D10} 00000 n \n");
        W($"{contentsOff:D10} 00000 n \n");
        W($"{annotOff:D10} 00000 n \n");
        W("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOff}\n%%EOF\n");
        return ms.ToArray();
    }
}
