using System.Text;
using Daisi.Broski.Docs.Pdf;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Docs.Pdf;

/// <summary>
/// Covers the traditional xref table path: single-subsection and
/// multi-subsection xrefs, <c>/Prev</c>-chained revisions, the
/// startxref locator, and the trailer dictionary exposure.
/// </summary>
public class PdfXrefTests
{
    [Fact]
    public void Reads_single_subsection()
    {
        // Build a minimal valid PDF shape: header, two dummy
        // objects, xref, trailer, startxref, EOF. Object offsets
        // in the xref are absolute byte offsets into the file.
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        int obj1Offset = pdf.Length;
        pdf.Append("1 0 obj\n<</Type /Catalog /Pages 2 0 R>>\nendobj\n");
        int obj2Offset = pdf.Length;
        pdf.Append("2 0 obj\n<</Type /Pages /Count 0>>\nendobj\n");
        int xrefOffset = pdf.Length;
        pdf.Append("xref\n");
        pdf.Append("0 3\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.Append($"{obj1Offset:D10} 00000 n \n");
        pdf.Append($"{obj2Offset:D10} 00000 n \n");
        pdf.Append("trailer\n<</Size 3 /Root 1 0 R>>\n");
        pdf.Append("startxref\n");
        pdf.Append(xrefOffset);
        pdf.Append("\n%%EOF\n");

        var xref = PdfXref.Read(Encoding.ASCII.GetBytes(pdf.ToString()));
        Assert.True(xref.TryGetOffset(1, out long off1));
        Assert.Equal(obj1Offset, off1);
        Assert.True(xref.TryGetOffset(2, out long off2));
        Assert.Equal(obj2Offset, off2);
        Assert.NotNull(xref.Trailer);
        var root = Assert.IsType<PdfRef>(xref.Trailer!.Get("Root"));
        Assert.Equal(1, root.ObjectNumber);
    }

    [Fact]
    public void Free_entry_not_exposed_as_offset()
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        int objOffset = pdf.Length;
        pdf.Append("1 0 obj\n<</A 1>>\nendobj\n");
        int xrefOffset = pdf.Length;
        pdf.Append("xref\n");
        pdf.Append("0 2\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.Append($"{objOffset:D10} 00000 n \n");
        pdf.Append("trailer\n<</Size 2 /Root 1 0 R>>\n");
        pdf.Append("startxref\n");
        pdf.Append(xrefOffset);
        pdf.Append("\n%%EOF\n");

        var xref = PdfXref.Read(Encoding.ASCII.GetBytes(pdf.ToString()));
        Assert.False(xref.TryGetOffset(0, out _));
        Assert.True(xref.TryGetOffset(1, out _));
    }

    [Fact]
    public void Prev_chain_gives_later_revision_priority()
    {
        // Two xref blocks: first (older) maps obj 1 → offset A.
        // Second (newer, pointed at by startxref) maps obj 1 →
        // offset B and carries /Prev → older. Later xref wins.
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        int olderObjOffset = pdf.Length;
        pdf.Append("1 0 obj\n<</V 1>>\nendobj\n");
        int olderXrefOffset = pdf.Length;
        pdf.Append("xref\n0 2\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.Append($"{olderObjOffset:D10} 00000 n \n");
        pdf.Append("trailer\n<</Size 2 /Root 1 0 R>>\n");
        pdf.Append("startxref\n");
        pdf.Append(olderXrefOffset);
        pdf.Append("\n%%EOF\n");
        int newerObjOffset = pdf.Length;
        pdf.Append("1 1 obj\n<</V 2>>\nendobj\n");
        int newerXrefOffset = pdf.Length;
        pdf.Append("xref\n0 2\n");
        pdf.Append("0000000000 65535 f \n");
        pdf.Append($"{newerObjOffset:D10} 00001 n \n");
        pdf.Append($"trailer\n<</Size 2 /Root 1 0 R /Prev {olderXrefOffset}>>\n");
        pdf.Append("startxref\n");
        pdf.Append(newerXrefOffset);
        pdf.Append("\n%%EOF\n");

        var xref = PdfXref.Read(Encoding.ASCII.GetBytes(pdf.ToString()));
        Assert.True(xref.TryGetOffset(1, out long off));
        Assert.Equal(newerObjOffset, off);
    }

    [Fact]
    public void Missing_startxref_throws()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4\nhello world\n%%EOF\n");
        Assert.Throws<InvalidDataException>(() => PdfXref.Read(bytes));
    }
}
