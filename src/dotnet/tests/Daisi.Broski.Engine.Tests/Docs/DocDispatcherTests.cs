using System.IO.Compression;
using System.Text;
using Daisi.Broski.Docs;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs;

/// <summary>
/// DocDispatcher is the central routing point that decides — from the
/// HTTP Content-Type, body magic bytes, and URL suffix — whether a
/// fetched response is a doc we know how to convert. These tests pin
/// the priority order (Content-Type wins over magic, magic over
/// suffix), the disambiguation between docx / xlsx / other OOXML
/// packages, and the pass-through behavior for HTML responses.
/// </summary>
public class DocDispatcherTests
{
    private const string DocxCt =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string XlsxCt =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string PdfCt = "application/pdf";

    private static Uri Url(string path) => new("https://example.com" + path);

    // ---------- Content-Type wins ----------

    [Fact]
    public void Pdf_content_type_dispatches_to_pdf()
    {
        var payload = Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        bool ok = DocDispatcher.TryConvert(
            payload, PdfCt, Url("/file.bin"), out var html);
        Assert.True(ok);
        Assert.NotNull(html);
        // PDF converter is a stub in this milestone; the dispatcher
        // still claims the response and emits an article that makes
        // the unsupported-but-identified state obvious to the caller.
        Assert.Contains("pdf", html!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Docx_content_type_dispatches_to_docx()
    {
        var zip = BuildMinimalDocx();
        bool ok = DocDispatcher.TryConvert(
            zip, DocxCt, Url("/file.bin"), out var html);
        Assert.True(ok);
        Assert.NotNull(html);
        Assert.Contains("<article", html!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Xlsx_content_type_dispatches_to_xlsx()
    {
        var zip = BuildMinimalXlsx();
        bool ok = DocDispatcher.TryConvert(
            zip, XlsxCt, Url("/file.bin"), out var html);
        Assert.True(ok);
        Assert.NotNull(html);
        Assert.Contains("<table", html!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Content_type_params_are_ignored()
    {
        var payload = Encoding.ASCII.GetBytes("%PDF-1.4\n");
        bool ok = DocDispatcher.TryConvert(
            payload, "application/pdf; charset=binary", Url("/f"), out _);
        Assert.True(ok);
    }

    // ---------- Magic bytes catch missing / wrong Content-Type ----------

    [Fact]
    public void Pdf_magic_bytes_dispatch_when_content_type_missing()
    {
        var payload = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n");
        bool ok = DocDispatcher.TryConvert(
            payload, contentType: null, Url("/unknown"), out _);
        Assert.True(ok);
    }

    [Fact]
    public void Docx_zip_is_distinguished_from_xlsx_by_inner_content_types()
    {
        // Content-Type says generic octet-stream; magic-byte path must
        // peek [Content_Types].xml to know docx vs xlsx.
        var zip = BuildMinimalDocx();
        bool ok = DocDispatcher.TryConvert(
            zip, "application/octet-stream", Url("/file"), out var html);
        Assert.True(ok);
        Assert.Contains("<article", html!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<table", html!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Xlsx_zip_is_distinguished_from_docx_by_inner_content_types()
    {
        var zip = BuildMinimalXlsx();
        bool ok = DocDispatcher.TryConvert(
            zip, "application/octet-stream", Url("/file"), out var html);
        Assert.True(ok);
        Assert.Contains("<table", html!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_office_zip_is_not_dispatched()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var s = entry.Open();
            s.Write("plain"u8);
        }
        bool ok = DocDispatcher.TryConvert(
            ms.ToArray(), "application/zip", Url("/file.zip"), out _);
        Assert.False(ok);
    }

    // ---------- URL suffix as tie-breaker ----------

    [Fact]
    public void Url_suffix_triggers_pdf_dispatch_with_no_other_signals()
    {
        // Empty body served with no content-type, but URL says .pdf.
        // The PDF converter (stub in this milestone) still emits its
        // unsupported-shell article so the dispatcher must claim it.
        bool ok = DocDispatcher.TryConvert(
            Array.Empty<byte>(), contentType: null, Url("/doc.pdf"), out var html);
        Assert.True(ok);
        Assert.Contains("pdf", html!, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Pass-through (no claim) ----------

    [Fact]
    public void Html_response_is_not_claimed()
    {
        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><html><body><p>hi</p></body></html>");
        bool ok = DocDispatcher.TryConvert(
            body, "text/html; charset=utf-8", Url("/"), out _);
        Assert.False(ok);
    }

    [Fact]
    public void Empty_unknown_response_is_not_claimed()
    {
        bool ok = DocDispatcher.TryConvert(
            Array.Empty<byte>(), contentType: null, Url("/"), out _);
        Assert.False(ok);
    }

    // ---------- Shared fixture builders (reused by converter tests) ----------

    /// <summary>Build the smallest zip that looks convincingly like a
    /// .docx from the outside: a [Content_Types].xml whose Override
    /// points the main document part at the wordprocessingml content
    /// type, and a word/document.xml with a single paragraph.</summary>
    internal static byte[] BuildMinimalDocx(string bodyText = "hello")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            AddEntry(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            AddEntry(zip, "word/document.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:p><w:r><w:t>{bodyText}</w:t></w:r></w:p></w:body>
                </w:document>
                """);
        }
        return ms.ToArray();
    }

    /// <summary>Minimal xlsx: workbook, one sheet, one string cell.</summary>
    internal static byte[] BuildMinimalXlsx(string cellText = "hello")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
                </Types>
                """);
            AddEntry(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            AddEntry(zip, "xl/workbook.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            AddEntry(zip, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
                </Relationships>
                """);
            AddEntry(zip, "xl/sharedStrings.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="1" uniqueCount="1">
                  <si><t>{cellText}</t></si>
                </sst>
                """);
            AddEntry(zip, "xl/worksheets/sheet1.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1"><c r="A1" t="s"><v>0</v></c></row>
                  </sheetData>
                </worksheet>
                """);
        }
        return ms.ToArray();
    }

    internal static void AddEntry(ZipArchive zip, string path, string contents)
    {
        var entry = zip.CreateEntry(path);
        using var s = entry.Open();
        using var w = new StreamWriter(s, new UTF8Encoding(false));
        w.Write(contents.TrimStart());
    }
}
