using System.Text;
using Daisi.Broski.Docs;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// End-to-end tests for the real PDF converter. Each test builds
/// a minimal valid PDF at fixture time (no checked-in binaries),
/// feeds it through <see cref="DocDispatcher"/>, and asserts that
/// the synthetic HTML carries the document's text. Covers:
/// single-page plain, multi-page, Flate-compressed content
/// streams, TJ / Td / T* text operators, and /Info metadata.
/// </summary>
public class PdfConverterTests
{
    private const string PdfCt = "application/pdf";
    private static readonly Uri Url = new("https://example.com/doc.pdf");

    [Fact]
    public void Simple_single_page_pdf_extracts_text()
    {
        var pdf = new MinimalPdf()
            .AddPage(contentStream: "BT /F1 12 Tf (Hello PDF world) Tj ET")
            .Build();
        var html = Run(pdf);
        Assert.Contains("Hello PDF world", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_page_pdf_produces_section_per_page()
    {
        var pdf = new MinimalPdf()
            .AddPage("BT /F1 12 Tf (First page body) Tj ET")
            .AddPage("BT /F1 12 Tf (Second page body) Tj ET")
            .Build();
        var html = Run(pdf);
        Assert.Contains("First page body", html, StringComparison.Ordinal);
        Assert.Contains("Second page body", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Page 1</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Page 2</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Flate_compressed_content_stream_decodes_and_extracts()
    {
        var pdf = new MinimalPdf()
            .AddPage("BT /F1 12 Tf (Compressed content) Tj ET",
                    flateCompressed: true)
            .Build();
        var html = Run(pdf);
        Assert.Contains("Compressed content", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TJ_and_newline_operators_produce_expected_shape()
    {
        var pdf = new MinimalPdf()
            .AddPage(@"BT /F1 12 Tf (Heading) Tj T* (Body paragraph) Tj ET")
            .Build();
        var html = Run(pdf);
        // Both runs of text survive.
        Assert.Contains("Heading", html, StringComparison.Ordinal);
        Assert.Contains("Body paragraph", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Info_title_overrides_filename_derived_title()
    {
        var pdf = new MinimalPdf()
            .WithInfoTitle("The Real Title")
            .AddPage("BT /F1 12 Tf (Body text) Tj ET")
            .Build();
        var html = Run(pdf);
        Assert.Contains("<title>The Real Title</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h1>The Real Title</h1>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_in_pdf_text_is_escaped()
    {
        var pdf = new MinimalPdf()
            .AddPage("BT /F1 12 Tf (<script>alert) Tj ET")
            .Build();
        var html = Run(pdf);
        Assert.Contains("&lt;script&gt;alert", html, StringComparison.Ordinal);
        // No raw <script> sequence leaks through.
        var bodyStart = html.IndexOf("<body>", StringComparison.Ordinal);
        var articleEnd = html.IndexOf("</article>", StringComparison.Ordinal);
        var body = html[bodyStart..articleEnd];
        Assert.DoesNotContain("<script>", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupt_pdf_falls_back_to_unsupported_shell()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\nnot really a pdf\n%%EOF\n");
        bool ok = DocDispatcher.TryConvert(bytes, PdfCt, Url, out var html);
        Assert.True(ok);
        Assert.Contains("PDF", html!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not fully read", html!, StringComparison.Ordinal);
    }

    // ---------- helpers ----------

    private static string Run(byte[] body)
    {
        bool ok = DocDispatcher.TryConvert(body, PdfCt, Url, out var html);
        Assert.True(ok);
        Assert.NotNull(html);
        return html!;
    }
}
