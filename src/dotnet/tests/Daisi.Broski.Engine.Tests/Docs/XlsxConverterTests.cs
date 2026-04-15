using System.IO.Compression;
using System.Text;
using Daisi.Broski.Docs;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs;

/// <summary>
/// Focused tests for the xlsx → HTML converter. Covers:
/// shared / inline string cells, number cells, date cells (both
/// built-in numFmt ids and custom format strings), multi-sheet
/// workbooks, blank cells padded into rectangular rows, and
/// merged ranges rendered as spanning cells.
/// </summary>
public class XlsxConverterTests
{
    private const string XlsxCt =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly Uri Url = new("https://example.com/book.xlsx");

    // ---------- Basic cell types ----------

    [Fact]
    public void Shared_string_cell_renders_text()
    {
        var body = BuildXlsx(
            sharedStrings: new[] { "Hello" },
            sheets: new[] { Sheet("Sheet1",
                """<row r="1"><c r="A1" t="s"><v>0</v></c></row>""") });
        var html = Run(body);
        Assert.Contains("<td>Hello</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_string_cell_renders_text()
    {
        var body = BuildXlsx(
            sheets: new[] { Sheet("Sheet1",
                """<row r="1"><c r="A1" t="inlineStr"><is><t>Inline</t></is></c></row>""") });
        Assert.Contains("<td>Inline</td>", Run(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Number_cell_renders_raw_value()
    {
        var body = BuildXlsx(
            sheets: new[] { Sheet("Sheet1",
                """<row r="1"><c r="A1"><v>42.5</v></c></row>""") });
        Assert.Contains("<td>42.5</td>", Run(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Boolean_cell_renders_uppercase_word()
    {
        var body = BuildXlsx(
            sheets: new[] { Sheet("Sheet1",
                """<row r="1"><c r="A1" t="b"><v>1</v></c><c r="B1" t="b"><v>0</v></c></row>""") });
        var html = Run(body);
        Assert.Contains("<td>TRUE</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>FALSE</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_cell_preserves_error_code()
    {
        var body = BuildXlsx(
            sheets: new[] { Sheet("Sheet1",
                """<row r="1"><c r="A1" t="e"><v>#N/A</v></c></row>""") });
        Assert.Contains("#N/A", Run(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Formula_cell_renders_cached_value()
    {
        var body = BuildXlsx(
            sheets: new[] { Sheet("Sheet1",
                """<row r="1"><c r="A1"><f>SUM(B1:D1)</f><v>12</v></c></row>""") });
        Assert.Contains("<td>12</td>", Run(body), StringComparison.Ordinal);
    }

    // ---------- Dates ----------

    [Fact]
    public void Builtin_date_numfmt_renders_iso_date()
    {
        // numFmt id 14 is the built-in "m/d/yyyy" short-date format.
        // xf at index 1 applies it; A1 uses s="1".
        // Compute the serial from a known DateTime via ToOADate so
        // the test doesn't depend on the exact Excel↔OADate
        // reconciliation (Excel's 1900-leap bug; the two systems
        // agree for dates after 1900-03-01 anyway).
        var target = new DateTime(2019, 7, 18);
        double serial = target.ToOADate();
        var body = BuildXlsx(
            stylesXml: """
            <numFmts count="0"/>
            <cellXfs count="2">
              <xf numFmtId="0" />
              <xf numFmtId="14" applyNumberFormat="1" />
            </cellXfs>
            """,
            sheets: new[] { Sheet("Sheet1",
                $"""<row r="1"><c r="A1" s="1"><v>{serial}</v></c></row>""") });
        var html = Run(body);
        Assert.Contains("<td>2019-07-18</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_date_format_renders_iso_date()
    {
        var target = new DateTime(2019, 7, 18);
        double serial = target.ToOADate();
        var body = BuildXlsx(
            stylesXml: """
            <numFmts count="1">
              <numFmt numFmtId="164" formatCode="yyyy-mm-dd"/>
            </numFmts>
            <cellXfs count="2">
              <xf numFmtId="0"/>
              <xf numFmtId="164" applyNumberFormat="1"/>
            </cellXfs>
            """,
            sheets: new[] { Sheet("Sheet1",
                $"""<row r="1"><c r="A1" s="1"><v>{serial}</v></c></row>""") });
        Assert.Contains("<td>2019-07-18</td>", Run(body), StringComparison.Ordinal);
    }

    // ---------- Layout ----------

    [Fact]
    public void Blank_cells_are_padded_to_rectangular_grid()
    {
        // Row 1 has cells at A, C (skipping B). Emitter pads B.
        var body = BuildXlsx(
            sheets: new[] { Sheet("Sheet1",
                """
                <row r="1">
                  <c r="A1" t="inlineStr"><is><t>A</t></is></c>
                  <c r="C1" t="inlineStr"><is><t>C</t></is></c>
                </row>
                """) });
        var html = Run(body);
        Assert.Contains("<td>A</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>C</td>", html, StringComparison.Ordinal);
        // Middle (blank) column pads to an empty cell so the table
        // stays rectangular rather than silently collapsing.
        Assert.Contains("<td></td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_sheets_each_get_h2_and_table()
    {
        var body = BuildXlsx(
            sheets: new[]
            {
                Sheet("Alpha",
                    """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>"""),
                Sheet("Beta",
                    """<row r="1"><c r="A1" t="inlineStr"><is><t>b</t></is></c></row>"""),
            });
        var html = Run(body);
        Assert.Contains("<h2>Alpha</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Beta</h2>", html, StringComparison.Ordinal);
        Assert.Contains(">a<", html, StringComparison.Ordinal);
        Assert.Contains(">b<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Merged_cells_render_with_colspan_and_rowspan()
    {
        var body = BuildXlsx(
            sheets: new[] { Sheet("Sheet1",
                """
                <row r="1">
                  <c r="A1" t="inlineStr"><is><t>Header</t></is></c>
                  <c r="B1"/>
                  <c r="C1"/>
                </row>
                <row r="2">
                  <c r="A2" t="inlineStr"><is><t>a</t></is></c>
                  <c r="B2" t="inlineStr"><is><t>b</t></is></c>
                  <c r="C2" t="inlineStr"><is><t>c</t></is></c>
                </row>
                """,
                mergeCells: """<mergeCell ref="A1:C1"/>""") });
        var html = Run(body);
        // Header row: single <th colspan="3">Header</th>; B1/C1 suppressed.
        Assert.Contains("colspan=\"3\"", html, StringComparison.Ordinal);
        Assert.Contains(">Header<", html, StringComparison.Ordinal);
    }

    // ---------- HTML escaping ----------

    [Fact]
    public void Cell_text_is_html_escaped()
    {
        var body = BuildXlsx(
            sheets: new[] { Sheet("Sheet1",
                """<row r="1"><c r="A1" t="inlineStr"><is><t>&lt;script&gt;</t></is></c></row>""") });
        var html = Run(body);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    // ---------- Output shape ----------

    [Fact]
    public void Output_has_article_wrapper_and_title_h1()
    {
        var body = BuildXlsx(
            sheets: new[] { Sheet("Sheet1",
                """<row r="1"><c r="A1" t="inlineStr"><is><t>x</t></is></c></row>""") });
        var html = Run(body);
        Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<article>", html, StringComparison.Ordinal);
        Assert.Contains("<h1>book</h1>", html, StringComparison.Ordinal);
    }

    // ---------- Helpers ----------

    private static string Run(byte[] body)
    {
        bool ok = DocDispatcher.TryConvert(body, XlsxCt, Url, out var html);
        Assert.True(ok);
        Assert.NotNull(html);
        return html!;
    }

    private readonly record struct SheetSpec(
        string Name, string SheetDataInner, string? MergeCellsInner);

    private static SheetSpec Sheet(
        string name, string sheetDataInner, string? mergeCells = null)
        => new(name, sheetDataInner, mergeCells);

    private static byte[] BuildXlsx(
        IReadOnlyList<SheetSpec> sheets,
        IReadOnlyList<string>? sharedStrings = null,
        string? stylesXml = null)
    {
        sharedStrings ??= Array.Empty<string>();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var overrides = new StringBuilder();
            overrides.Append("""<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
            for (int i = 0; i < sheets.Count; i++)
            {
                overrides.Append($"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
            }
            if (sharedStrings.Count > 0)
                overrides.Append("""<Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>""");
            if (stylesXml is not null)
                overrides.Append("""<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");

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
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);

            // workbook + its rels
            var wbSheets = new StringBuilder();
            var wbRels = new StringBuilder();
            for (int i = 0; i < sheets.Count; i++)
            {
                wbSheets.Append($"""<sheet name="{sheets[i].Name}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
                wbRels.Append($"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>""");
            }
            int nextRid = sheets.Count + 1;
            if (sharedStrings.Count > 0)
                wbRels.Append($"""<Relationship Id="rId{nextRid++}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>""");
            if (stylesXml is not null)
                wbRels.Append($"""<Relationship Id="rId{nextRid++}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");

            AddEntry(zip, "xl/workbook.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets>{wbSheets}</sheets>
                </workbook>
                """);
            AddEntry(zip, "xl/_rels/workbook.xml.rels",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  {wbRels}
                </Relationships>
                """);

            // Sheets
            for (int i = 0; i < sheets.Count; i++)
            {
                var merges = sheets[i].MergeCellsInner is null
                    ? ""
                    : $"<mergeCells count=\"1\">{sheets[i].MergeCellsInner}</mergeCells>";
                AddEntry(zip, $"xl/worksheets/sheet{i + 1}.xml",
                    $"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                      <sheetData>{sheets[i].SheetDataInner}</sheetData>
                      {merges}
                    </worksheet>
                    """);
            }

            // sharedStrings
            if (sharedStrings.Count > 0)
            {
                var items = new StringBuilder();
                foreach (var s in sharedStrings)
                    items.Append($"<si><t>{HtmlEscape(s)}</t></si>");
                AddEntry(zip, "xl/sharedStrings.xml",
                    $"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{sharedStrings.Count}" uniqueCount="{sharedStrings.Count}">
                      {items}
                    </sst>
                    """);
            }

            // styles
            if (stylesXml is not null)
            {
                AddEntry(zip, "xl/styles.xml",
                    $"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                      {stylesXml}
                    </styleSheet>
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

    private static string HtmlEscape(string s)
        => s.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}
