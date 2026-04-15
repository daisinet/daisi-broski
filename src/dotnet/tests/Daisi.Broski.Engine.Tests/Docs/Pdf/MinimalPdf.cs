using System.IO.Compression;
using System.Text;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Builds a byte-accurate minimal PDF 1.4 file for tests. Layout:
/// %PDF-1.4 header → catalog → pages tree → per page (page dict +
/// its content stream) → optional /Info → xref table → trailer →
/// startxref → %%EOF. xref offsets are computed from the actual
/// byte positions of each object, so the resulting file passes
/// our own xref reader.
///
/// <para>This is a test-only fixture builder; we deliberately
/// don't ship it as a general PDF writer. Only the shapes we
/// need for converter round-trips are supported.</para>
/// </summary>
internal sealed class MinimalPdf
{
    private readonly List<(string content, bool flateCompressed)> _pages = new();
    private string? _infoTitle;

    internal MinimalPdf AddPage(string contentStream, bool flateCompressed = false)
    {
        _pages.Add((contentStream, flateCompressed));
        return this;
    }

    internal MinimalPdf WithInfoTitle(string title)
    {
        _infoTitle = title;
        return this;
    }

    internal byte[] Build()
    {
        // Object numbering:
        //   1 = Catalog
        //   2 = Pages (tree root)
        //   3 = Font F1 (Helvetica with WinAnsi)
        //   4 = Info (optional, skipped when no metadata)
        //   5+2i = page i dictionary
        //   6+2i = page i content stream
        using var ms = new MemoryStream();
        var offsets = new List<long>();

        void WriteAscii(string s)
            => ms.Write(Encoding.ASCII.GetBytes(s));

        void WriteRaw(byte[] b) => ms.Write(b);

        // Header. The binary comment after %PDF- hints that the
        // file contains 8-bit content; our parser ignores it but
        // real readers sometimes use it to pick a transport mode.
        WriteAscii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        // -- Catalog (object 1)
        offsets.Add(ms.Length);
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // -- Pages (object 2)
        offsets.Add(ms.Length);
        var kids = new StringBuilder();
        for (int i = 0; i < _pages.Count; i++)
        {
            int pageObj = 5 + 2 * i;
            if (kids.Length > 0) kids.Append(' ');
            kids.Append(pageObj).Append(" 0 R");
        }
        WriteAscii($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>\nendobj\n");

        // -- Font (object 3)
        offsets.Add(ms.Length);
        WriteAscii("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");

        // -- Info (object 4) — always allocate the slot so the
        //    numbering stays stable; use an empty dict when there's
        //    nothing to say.
        offsets.Add(ms.Length);
        if (_infoTitle is not null)
        {
            WriteAscii($"4 0 obj\n<< /Title ({Escape(_infoTitle)}) >>\nendobj\n");
        }
        else
        {
            WriteAscii("4 0 obj\n<< >>\nendobj\n");
        }

        // -- Per page: dict + content stream
        for (int i = 0; i < _pages.Count; i++)
        {
            int pageObj = 5 + 2 * i;
            int contentObj = 6 + 2 * i;
            offsets.Add(ms.Length);
            WriteAscii($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R " +
                       $"/MediaBox [0 0 612 792] " +
                       $"/Resources << /Font << /F1 3 0 R >> >> " +
                       $"/Contents {contentObj} 0 R >>\nendobj\n");

            offsets.Add(ms.Length);
            var (content, compressed) = _pages[i];
            byte[] payload = Encoding.ASCII.GetBytes(content);
            if (compressed)
            {
                payload = FlateCompress(payload);
                WriteAscii($"{contentObj} 0 obj\n<< /Length {payload.Length} /Filter /FlateDecode >>\nstream\n");
            }
            else
            {
                WriteAscii($"{contentObj} 0 obj\n<< /Length {payload.Length} >>\nstream\n");
            }
            WriteRaw(payload);
            WriteAscii("\nendstream\nendobj\n");
        }

        // -- xref
        long xrefOffset = ms.Length;
        // The xref table always includes the implicit "free" object 0.
        int objCount = 1 + offsets.Count;
        WriteAscii("xref\n");
        WriteAscii($"0 {objCount}\n");
        WriteAscii("0000000000 65535 f \n");
        foreach (var off in offsets)
        {
            WriteAscii($"{off:D10} 00000 n \n");
        }

        // -- trailer
        var trailer = new StringBuilder();
        trailer.Append($"trailer\n<< /Size {objCount} /Root 1 0 R");
        if (_infoTitle is not null) trailer.Append(" /Info 4 0 R");
        trailer.Append(" >>\n");
        WriteAscii(trailer.ToString());

        WriteAscii($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Escape parens / backslashes inside a PDF literal
    /// string so the fixture stays well-formed no matter what
    /// characters the caller passes.</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
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
