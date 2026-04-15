using System.Net;
using Daisi.Broski.Skimmer;
using Xunit;
using SkimmerApi = Daisi.Broski.Skimmer.Skimmer;

namespace Daisi.Broski.Engine.Tests.Skimmer;

/// <summary>
/// End-to-end tests for the Skimmer's doc-conversion path. Serves
/// binary docx / xlsx / pdf bytes through an embedded
/// <see cref="HttpListener"/> and asserts that
/// <see cref="SkimmerApi.SkimAsync"/> produces a populated
/// <c>ArticleContent</c> whose PlainText + metadata reflect the
/// converted document. Mirrors the existing
/// <see cref="SkimmerFacadeTests"/> shape so the two test classes
/// share a setup convention.
/// </summary>
public class SkimmerDocFacadeTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _baseUrl;
    private readonly Dictionary<string, (byte[] body, string contentType)> _routes = new();
    private readonly Task _listenerLoop;
    private readonly CancellationTokenSource _cts = new();

    public SkimmerDocFacadeTests()
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int port = Random.Shared.Next(40000, 60000);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                _baseUrl = $"http://127.0.0.1:{port}";
                _listenerLoop = Task.Run(LoopAsync);
                return;
            }
            catch (HttpListenerException)
            {
                _listener.Close();
            }
        }
        throw new InvalidOperationException("Could not bind a test HttpListener.");
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            if (_routes.TryGetValue(path, out var route))
            {
                ctx.Response.ContentType = route.contentType;
                ctx.Response.ContentLength64 = route.body.Length;
                await ctx.Response.OutputStream.WriteAsync(route.body);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
    }

    private void Serve(string path, byte[] body, string contentType)
        => _routes[path] = (body, contentType);

    // ---------- docx ----------

    [Fact]
    public async Task Skim_of_docx_url_extracts_body_text()
    {
        Serve("/report.docx",
            Docs.DocDispatcherTests.BuildMinimalDocx(
                bodyText: "The quarterly report body text for extraction."),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        var article = await SkimmerApi.SkimAsync(
            new Uri(_baseUrl + "/report.docx"),
            new SkimmerOptions { ScriptingEnabled = false });
        Assert.Contains("quarterly report", article.PlainText,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Docx_detected_from_magic_bytes_when_content_type_is_wrong()
    {
        // Sharepoint-style mis-serve: the bytes are a .docx, but
        // the server says application/octet-stream.
        Serve("/Download.ashx",
            Docs.DocDispatcherTests.BuildMinimalDocx(
                bodyText: "Detected via magic bytes despite wrong Content-Type."),
            "application/octet-stream");
        var article = await SkimmerApi.SkimAsync(
            new Uri(_baseUrl + "/Download.ashx"),
            new SkimmerOptions { ScriptingEnabled = false });
        Assert.Contains("magic bytes", article.PlainText,
            StringComparison.OrdinalIgnoreCase);
    }

    // ---------- xlsx ----------

    [Fact]
    public async Task Skim_of_xlsx_url_extracts_sheet_contents()
    {
        Serve("/book.xlsx",
            Docs.DocDispatcherTests.BuildMinimalXlsx(cellText: "CellValueAlpha"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var article = await SkimmerApi.SkimAsync(
            new Uri(_baseUrl + "/book.xlsx"),
            new SkimmerOptions { ScriptingEnabled = false });
        Assert.Contains("CellValueAlpha", article.PlainText,
            StringComparison.Ordinal);
    }

    // ---------- pdf ----------

    [Fact]
    public async Task Skim_of_valid_pdf_extracts_body_text()
    {
        var pdfBytes = new Docs.Pdf.MinimalPdf()
            .AddPage("BT /F1 12 Tf (Extracted PDF body text for the skimmer test) Tj ET")
            .Build();
        Serve("/paper.pdf", pdfBytes, "application/pdf");
        var article = await SkimmerApi.SkimAsync(
            new Uri(_baseUrl + "/paper.pdf"),
            new SkimmerOptions { ScriptingEnabled = false });
        Assert.Contains("Extracted PDF body text", article.PlainText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skim_of_corrupt_pdf_returns_unsupported_shell()
    {
        var payload = System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        Serve("/whitepaper.pdf", payload, "application/pdf");
        var article = await SkimmerApi.SkimAsync(
            new Uri(_baseUrl + "/whitepaper.pdf"),
            new SkimmerOptions { ScriptingEnabled = false });
        // Corrupt PDF path — missing startxref — lands on the
        // unsupported-shell fallback. The skim returns a populated
        // article rather than "no content found".
        Assert.Contains("PDF", article.PlainText, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(article.PlainText));
    }

    // ---------- HTML regression ----------

    [Fact]
    public async Task Skim_of_regular_html_still_works()
    {
        Serve("/page", System.Text.Encoding.UTF8.GetBytes("""
            <!doctype html><html><body>
              <article><p>Regular HTML article body with enough text to pass the extractor threshold so the test asserts cleanly.</p></article>
            </body></html>
            """), "text/html; charset=utf-8");
        var article = await SkimmerApi.SkimAsync(
            new Uri(_baseUrl + "/page"),
            new SkimmerOptions { ScriptingEnabled = false });
        Assert.Contains("Regular HTML article", article.PlainText,
            StringComparison.Ordinal);
    }
}
