using System.IO.Compression;
using System.Text;
using Daisi.Broski.Docs;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs;

/// <summary>
/// Focused tests for the docx → HTML converter. Each test builds a
/// targeted fixture via <see cref="ZipArchive"/> (avoiding the need
/// to check in binary Office files), runs the converter through
/// <see cref="DocDispatcher"/>, and asserts on the synthetic HTML
/// shape.
/// </summary>
public class DocxConverterTests
{
    private const string DocxCt =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private static readonly Uri Url = new("https://example.com/doc.docx");

    // ---------- Paragraphs + runs ----------

    [Fact]
    public void Plain_paragraph_renders_as_p()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:r><w:t>Hello world.</w:t></w:r></w:p>
            """);
        var html = Run(body);
        Assert.Contains("<p>Hello world.</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Bold_run_renders_strong()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Bold</w:t></w:r></w:p>
            """);
        Assert.Contains("<strong>Bold</strong>", Run(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Italic_run_renders_em()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:r><w:rPr><w:i/></w:rPr><w:t>Italic</w:t></w:r></w:p>
            """);
        Assert.Contains("<em>Italic</em>", Run(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Underline_run_renders_u_tag()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:r><w:rPr><w:u w:val="single"/></w:rPr><w:t>Under</w:t></w:r></w:p>
            """);
        Assert.Contains("<u>Under</u>", Run(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Bold_zero_val_disables_bold()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:r><w:rPr><w:b w:val="0"/></w:rPr><w:t>NotBold</w:t></w:r></w:p>
            """);
        var html = Run(body);
        Assert.DoesNotContain("<strong>", html, StringComparison.Ordinal);
        Assert.Contains("NotBold", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_metacharacters_are_escaped()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:r><w:t>&lt;script&gt;&amp;</w:t></w:r></w:p>
            """);
        var html = Run(body);
        Assert.Contains("&lt;script&gt;&amp;", html, StringComparison.Ordinal);
        // Belt-and-braces — the raw tag sequence must not appear.
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Br_element_renders_br_tag()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:r><w:t>A</w:t><w:br/><w:t>B</w:t></w:r></w:p>
            """);
        Assert.Contains("A<br>B", Run(body), StringComparison.Ordinal);
    }

    // ---------- Headings ----------

    [Fact]
    public void Heading_style_with_outlineLvl_produces_h_tag()
    {
        var body = BuildDocx(
            stylesXml: """
            <w:style w:type="paragraph" w:styleId="Heading1">
              <w:name w:val="heading 1"/>
              <w:pPr><w:outlineLvl w:val="0"/></w:pPr>
            </w:style>
            """,
            documentXml: """
            <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr>
              <w:r><w:t>Title</w:t></w:r></w:p>
            """);
        Assert.Contains("<h1>Title</h1>", Run(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_outlineLvl_overrides_to_heading()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:pPr><w:outlineLvl w:val="1"/></w:pPr>
              <w:r><w:t>Subhead</w:t></w:r></w:p>
            """);
        Assert.Contains("<h2>Subhead</h2>", Run(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Style_name_convention_derives_heading_when_no_outlineLvl()
    {
        var body = BuildDocx(
            stylesXml: """
            <w:style w:type="paragraph" w:styleId="H3"><w:name w:val="Heading 3"/></w:style>
            """,
            documentXml: """
            <w:p><w:pPr><w:pStyle w:val="H3"/></w:pPr>
              <w:r><w:t>Third</w:t></w:r></w:p>
            """);
        Assert.Contains("<h3>Third</h3>", Run(body), StringComparison.Ordinal);
    }

    // ---------- Lists ----------

    [Fact]
    public void Bullet_list_renders_ul()
    {
        var body = BuildDocx(
            numberingXml: """
            <w:abstractNum w:abstractNumId="0">
              <w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/></w:lvl>
            </w:abstractNum>
            <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
            """,
            documentXml: """
            <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr>
              <w:r><w:t>First</w:t></w:r></w:p>
            <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr>
              <w:r><w:t>Second</w:t></w:r></w:p>
            """);
        var html = Run(body);
        Assert.Contains("<ul><li>First</li><li>Second</li></ul>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Decimal_list_renders_ol()
    {
        var body = BuildDocx(
            numberingXml: """
            <w:abstractNum w:abstractNumId="5">
              <w:lvl w:ilvl="0"><w:numFmt w:val="decimal"/></w:lvl>
            </w:abstractNum>
            <w:num w:numId="2"><w:abstractNumId w:val="5"/></w:num>
            """,
            documentXml: """
            <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="2"/></w:numPr></w:pPr>
              <w:r><w:t>One</w:t></w:r></w:p>
            <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="2"/></w:numPr></w:pPr>
              <w:r><w:t>Two</w:t></w:r></w:p>
            """);
        Assert.Contains("<ol><li>One</li><li>Two</li></ol>",
            Run(body), StringComparison.Ordinal);
    }

    // ---------- Tables ----------

    [Fact]
    public void Simple_table_renders_tr_td_rows()
    {
        var body = BuildDocx(documentXml: """
            <w:tbl>
              <w:tr><w:tc><w:p><w:r><w:t>H1</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>H2</w:t></w:r></w:p></w:tc></w:tr>
              <w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr>
            </w:tbl>
            """);
        var html = Run(body);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<tr>", html, StringComparison.Ordinal);
        // Cell content wraps each paragraph in <p> — the docx cell
        // body is a list of blocks, rendered through the block
        // emitter. Assert on the text and structural tags, not on
        // a specific wrapper shape.
        Assert.Contains("<td><p>H1</p></td>", html, StringComparison.Ordinal);
        Assert.Contains("<td><p>A</p></td>", html, StringComparison.Ordinal);
    }

    // ---------- Hyperlinks ----------

    [Fact]
    public void External_hyperlink_resolves_through_relationships()
    {
        var body = BuildDocx(
            relationships: """
            <Relationship Id="rLink" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://daisi.ai" TargetMode="External"/>
            """,
            documentXml: """
            <w:p><w:hyperlink r:id="rLink"><w:r><w:t>daisi</w:t></w:r></w:hyperlink></w:p>
            """);
        Assert.Contains("<a href=\"https://daisi.ai\">daisi</a>",
            Run(body), StringComparison.Ordinal);
    }

    // ---------- Metadata ----------

    [Fact]
    public void Core_properties_populate_title_and_author()
    {
        var body = BuildDocx(
            coreXml: """
            <dc:title>Test Report</dc:title>
            <dc:creator>Jane Doe</dc:creator>
            <dcterms:created xsi:type="dcterms:W3CDTF">2026-04-13T12:00:00Z</dcterms:created>
            """,
            documentXml: """
            <w:p><w:r><w:t>Body.</w:t></w:r></w:p>
            """);
        var html = Run(body);
        Assert.Contains("<title>Test Report</title>", html, StringComparison.Ordinal);
        Assert.Contains("Jane Doe", html, StringComparison.Ordinal);
        Assert.Contains("2026-04-13T12:00:00Z", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_title_falls_back_to_filename()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:r><w:t>Body.</w:t></w:r></w:p>
            """);
        var html = Run(body);
        // URL path: /doc.docx → title: "doc"
        Assert.Contains("<title>doc</title>", html, StringComparison.Ordinal);
    }

    // ---------- End-to-end shape ----------

    [Fact]
    public void Output_parses_as_an_article()
    {
        var body = BuildDocx(documentXml: """
            <w:p><w:r><w:t>Hello.</w:t></w:r></w:p>
            """);
        var html = Run(body);
        Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<article>", html, StringComparison.Ordinal);
        Assert.Contains("</article>", html, StringComparison.Ordinal);
    }

    // ---------- Helpers ----------

    private static string Run(byte[] body)
    {
        bool ok = DocDispatcher.TryConvert(body, DocxCt, Url, out var html);
        Assert.True(ok);
        Assert.NotNull(html);
        return html!;
    }

    /// <summary>Build a minimal docx with the caller's custom pieces
    /// injected. Any argument left null uses a sensible default so
    /// tests only need to specify what they're pinning.</summary>
    private static byte[] BuildDocx(
        string? documentXml = null,
        string? stylesXml = null,
        string? numberingXml = null,
        string? coreXml = null,
        string? relationships = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var overrides = new StringBuilder();
            overrides.Append("""<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>""");
            if (stylesXml is not null)
                overrides.Append("""<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>""");
            if (numberingXml is not null)
                overrides.Append("""<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>""");
            if (coreXml is not null)
                overrides.Append("""<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>""");

            AddEntry(zip, "[Content_Types].xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  {overrides}
                </Types>
                """);
            AddEntry(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            AddEntry(zip, "word/_rels/document.xml.rels",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  {relationships ?? ""}
                </Relationships>
                """);
            AddEntry(zip, "word/document.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <w:body>
                    {documentXml ?? ""}
                  </w:body>
                </w:document>
                """);
            if (stylesXml is not null)
            {
                AddEntry(zip, "word/styles.xml",
                    $"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                      {stylesXml}
                    </w:styles>
                    """);
            }
            if (numberingXml is not null)
            {
                AddEntry(zip, "word/numbering.xml",
                    $"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                      {numberingXml}
                    </w:numbering>
                    """);
            }
            if (coreXml is not null)
            {
                AddEntry(zip, "docProps/core.xml",
                    $"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <cp:coreProperties
                      xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                      xmlns:dc="http://purl.org/dc/elements/1.1/"
                      xmlns:dcterms="http://purl.org/dc/terms/"
                      xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                      {coreXml}
                    </cp:coreProperties>
                    """);
            }
        }
        return ms.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string path, string contents)
    {
        var entry = zip.CreateEntry(path);
        using var s = entry.Open();
        using var w = new StreamWriter(s, new UTF8Encoding(false));
        w.Write(contents.TrimStart());
    }
}
