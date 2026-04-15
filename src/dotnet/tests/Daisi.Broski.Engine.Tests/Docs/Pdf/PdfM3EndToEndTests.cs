using System.IO.Compression;
using System.Text;
using Daisi.Broski.Docs;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Milestone-3 end-to-end tests: hand-builds a PDF using the
/// PDF 1.5+ structures (cross-reference stream, object stream,
/// Type 0 font with ToUnicode CMap) and asserts the converter
/// extracts text correctly. Each test writes the file bytes
/// inline rather than extending the simpler MinimalPdf builder
/// — the shapes differ enough that shared plumbing would be more
/// confusing than three focused builders.
/// </summary>
public class PdfM3EndToEndTests
{
    private const string PdfCt = "application/pdf";
    private static readonly Uri Url = new("https://example.com/modern.pdf");

    [Fact]
    public void Pdf_with_cross_reference_stream_extracts_text()
    {
        var bytes = BuildPdfWithXrefStream(
            contentStream: "BT /F1 12 Tf (Xref-stream text extraction works) Tj ET");
        var html = Run(bytes);
        Assert.Contains("Xref-stream text extraction works", html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_with_object_stream_extracts_text()
    {
        // The catalog, pages, font, and info objects all live
        // inside a single compressed ObjStm. The page's content
        // stream remains uncompressed and lives outside the
        // ObjStm (only non-stream objects can go inside).
        var bytes = BuildPdfWithObjectStream(
            contentStream: "BT /F1 12 Tf (Object-stream payload is resolvable) Tj ET");
        var html = Run(bytes);
        Assert.Contains("Object-stream payload is resolvable", html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_with_type0_font_and_tounicode_cmap_extracts_text()
    {
        // 2-byte CID font: codes 0x0001..0x0005 map to 'H', 'e',
        // 'l', 'l', 'o'. Content stream shows the hex-string
        // "<00010002000300040005>" which the extractor decodes via
        // the ToUnicode CMap to "Hello".
        var bytes = BuildPdfWithType0Font();
        var html = Run(bytes);
        Assert.Contains("Hello", html, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Fixture builders — inline so each test's structure is readable
    // in one place. They DO use the real PdfFilters.FlateDecode
    // format (zlib via System.IO.Compression.ZLibStream).
    // ------------------------------------------------------------------

    private static byte[] BuildPdfWithXrefStream(string contentStream)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WRaw(byte[] b) => ms.Write(b);

        W("%PDF-1.5\n%\xE2\xE3\xCF\xD3\n");
        long catalogOff = ms.Length;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        long pagesOff = ms.Length;
        W("2 0 obj\n<< /Type /Pages /Kids [4 0 R] /Count 1 >>\nendobj\n");
        long fontOff = ms.Length;
        W("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        long pageOff = ms.Length;
        W("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
          "/Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        long contentsOff = ms.Length;
        var cs = Encoding.ASCII.GetBytes(contentStream);
        W($"5 0 obj\n<< /Length {cs.Length} >>\nstream\n");
        WRaw(cs);
        W("\nendstream\nendobj\n");

        // -- xref stream (object 6). Builds a payload with 6
        //    entries (objects 0..5). Column widths W=[1 2 1]:
        //    one byte for type, two for offset, one for gen.
        long xrefObjOff = ms.Length;
        using var payloadMs = new MemoryStream();
        void AddEntry(int type, int field2, int field3)
        {
            payloadMs.WriteByte((byte)type);
            payloadMs.WriteByte((byte)(field2 >> 8));
            payloadMs.WriteByte((byte)field2);
            payloadMs.WriteByte((byte)field3);
        }
        AddEntry(0, 0, 0xFF);                   // obj 0 — head of free list
        AddEntry(1, (int)catalogOff, 0);        // obj 1 — catalog
        AddEntry(1, (int)pagesOff, 0);          // obj 2 — pages
        AddEntry(1, (int)fontOff, 0);           // obj 3 — font
        AddEntry(1, (int)pageOff, 0);           // obj 4 — page
        AddEntry(1, (int)contentsOff, 0);       // obj 5 — contents
        AddEntry(1, (int)xrefObjOff, 0);        // obj 6 — self
        byte[] payload = payloadMs.ToArray();
        byte[] compressed = FlateCompress(payload);

        W("6 0 obj\n");
        W("<< /Type /XRef /Size 7 /W [1 2 1] /Root 1 0 R ");
        W($"/Length {compressed.Length} /Filter /FlateDecode >>\n");
        W("stream\n");
        WRaw(compressed);
        W("\nendstream\nendobj\n");

        W($"startxref\n{xrefObjOff}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithObjectStream(string contentStream)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WRaw(byte[] b) => ms.Write(b);

        W("%PDF-1.5\n%\xE2\xE3\xCF\xD3\n");

        // -- Object stream (object 7) packs the catalog (1), pages
        //    (2), font (3), and page (4) dictionaries as the 4
        //    sub-objects at index 0..3.
        var objStmInner = new StringBuilder();
        var offsets = new List<int>();
        void AddSubObject(string objSyntax)
        {
            offsets.Add(objStmInner.Length);
            objStmInner.Append(objSyntax);
        }
        AddSubObject("<< /Type /Catalog /Pages 2 0 R >> ");
        AddSubObject("<< /Type /Pages /Kids [4 0 R] /Count 1 >> ");
        AddSubObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> ");
        AddSubObject("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                     "/Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >> ");
        // Header: "objnum offset" pairs, then the concatenated inner objects.
        var header = new StringBuilder();
        int[] objNums = { 1, 2, 3, 4 };
        for (int i = 0; i < objNums.Length; i++)
        {
            header.Append(objNums[i]).Append(' ')
                  .Append(offsets[i]).Append(' ');
        }
        int firstOffset = header.Length;
        string combinedText = header.ToString() + objStmInner.ToString();
        byte[] combined = Encoding.ASCII.GetBytes(combinedText);
        byte[] compressedObjStm = FlateCompress(combined);

        long contentsOff = ms.Length;
        var cs = Encoding.ASCII.GetBytes(contentStream);
        W($"5 0 obj\n<< /Length {cs.Length} >>\nstream\n");
        WRaw(cs);
        W("\nendstream\nendobj\n");

        long objStmOff = ms.Length;
        W("7 0 obj\n");
        W($"<< /Type /ObjStm /N {objNums.Length} /First {firstOffset} ");
        W($"/Length {compressedObjStm.Length} /Filter /FlateDecode >>\n");
        W("stream\n");
        WRaw(compressedObjStm);
        W("\nendstream\nendobj\n");

        // -- xref stream (object 8). Compressed entries use type 2:
        //    field2 = containing obj stream number, field3 = index
        //    within that stream.
        long xrefObjOff = ms.Length;
        using var payloadMs = new MemoryStream();
        void AddEntry(int type, int f2, int f3)
        {
            payloadMs.WriteByte((byte)type);
            payloadMs.WriteByte((byte)(f2 >> 16));
            payloadMs.WriteByte((byte)(f2 >> 8));
            payloadMs.WriteByte((byte)f2);
            payloadMs.WriteByte((byte)f3);
        }
        AddEntry(0, 0, 0);                        // obj 0
        AddEntry(2, 7, 0);                        // obj 1 → obj stream 7 idx 0
        AddEntry(2, 7, 1);                        // obj 2 → obj stream 7 idx 1
        AddEntry(2, 7, 2);                        // obj 3 → obj stream 7 idx 2
        AddEntry(2, 7, 3);                        // obj 4 → obj stream 7 idx 3
        AddEntry(1, (int)contentsOff, 0);         // obj 5 → contents
        AddEntry(0, 0, 0);                        // obj 6 — unused slot
        AddEntry(1, (int)objStmOff, 0);           // obj 7 → object stream itself
        AddEntry(1, (int)xrefObjOff, 0);          // obj 8 → self
        byte[] xrefPayload = payloadMs.ToArray();
        byte[] xrefCompressed = FlateCompress(xrefPayload);

        W("8 0 obj\n");
        W("<< /Type /XRef /Size 9 /W [1 3 1] /Root 1 0 R ");
        W($"/Length {xrefCompressed.Length} /Filter /FlateDecode >>\n");
        W("stream\n");
        WRaw(xrefCompressed);
        W("\nendstream\nendobj\n");

        W($"startxref\n{xrefObjOff}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithType0Font()
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WRaw(byte[] b) => ms.Write(b);

        W("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        long catalogOff = ms.Length;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        long pagesOff = ms.Length;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // -- Page (object 3) references F1 which maps to object 4
        //    (the Type 0 font).
        long pageOff = ms.Length;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
          "/Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R >>\nendobj\n");

        // -- Type 0 font (object 4) with ToUnicode CMap (object 5).
        long fontOff = ms.Length;
        W("4 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Arial " +
          "/Encoding /Identity-H /ToUnicode 5 0 R >>\nendobj\n");

        long cmapOff = ms.Length;
        string cmapSource = """
            /CIDInit /ProcSet findresource begin 12 dict begin begincmap
            /CMapName /Custom def
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange
            5 beginbfchar
            <0001> <0048>
            <0002> <0065>
            <0003> <006C>
            <0004> <006C>
            <0005> <006F>
            endbfchar
            endcmap CMapName currentdict /CMap defineresource pop end end
            """;
        var cmapBytes = Encoding.ASCII.GetBytes(cmapSource);
        W($"5 0 obj\n<< /Length {cmapBytes.Length} >>\nstream\n");
        WRaw(cmapBytes);
        W("\nendstream\nendobj\n");

        // -- Content stream (object 6): show hex string via the
        //    Type 0 font. Hex string "<00010002000300040005>" is
        //    five 2-byte codes.
        long contentsOff = ms.Length;
        var cs = Encoding.ASCII.GetBytes(
            "BT /F1 12 Tf <00010002000300040005> Tj ET");
        W($"6 0 obj\n<< /Length {cs.Length} >>\nstream\n");
        WRaw(cs);
        W("\nendstream\nendobj\n");

        // Traditional xref.
        long xrefOff = ms.Length;
        W("xref\n0 7\n");
        W("0000000000 65535 f \n");
        W($"{catalogOff:D10} 00000 n \n");
        W($"{pagesOff:D10} 00000 n \n");
        W($"{pageOff:D10} 00000 n \n");
        W($"{fontOff:D10} 00000 n \n");
        W($"{cmapOff:D10} 00000 n \n");
        W($"{contentsOff:D10} 00000 n \n");
        W("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOff}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string Run(byte[] body)
    {
        bool ok = DocDispatcher.TryConvert(body, PdfCt, Url, out var html);
        Assert.True(ok);
        Assert.NotNull(html);
        return html!;
    }

    private static byte[] FlateCompress(byte[] input)
    {
        using var dst = new MemoryStream();
        using (var zs = new ZLibStream(dst, CompressionLevel.Optimal, leaveOpen: true))
        {
            zs.Write(input, 0, input.Length);
        }
        return dst.ToArray();
    }
}
