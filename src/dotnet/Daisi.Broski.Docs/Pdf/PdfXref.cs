using System.Globalization;
using System.Text;

namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// The PDF cross-reference table — PDF 1.7 spec §7.5.4. Maps
/// <c>(object#, generation)</c> to the byte offset of the object's
/// <c>N M obj</c> header. Built by finding <c>%%EOF</c> at the
/// tail of the file, walking backward to <c>startxref</c> to find
/// the offset of the first xref block, and following the
/// <c>/Prev</c> chain in each block's trailer dictionary.
///
/// <para>Only the traditional xref syntax (<c>xref 0 3\n…</c>) is
/// handled here. PDF 1.5+ cross-reference streams — where the xref
/// itself is a compressed stream — are a milestone-3 add. Such
/// files will fail here with a clear "unsupported" signal rather
/// than silent garbage.</para>
/// </summary>
internal sealed class PdfXref
{
    /// <summary>One entry per referenced object number. Can be
    /// either a regular in-use entry (offset + generation), a
    /// free-list placeholder (ignored for reads), or — for PDF
    /// 1.5+ cross-reference streams — a compressed entry pointing
    /// at an object stream by number + index.</summary>
    private readonly Dictionary<int, Entry> _entries = new();

    public PdfDictionary? Trailer { get; private set; }

    public IReadOnlyDictionary<int, Entry> Entries => _entries;

    /// <summary>Try to look up an object's byte offset. Returns
    /// true and sets <paramref name="offset"/> for uncompressed
    /// in-use entries; returns false for missing, free-list, or
    /// compressed entries (compressed entries need
    /// <see cref="TryGetCompressed"/> + object-stream
    /// expansion).</summary>
    public bool TryGetOffset(int objectNumber, out long offset)
    {
        if (_entries.TryGetValue(objectNumber, out var e)
            && e.Kind == EntryKind.Uncompressed)
        {
            offset = e.Offset;
            return true;
        }
        offset = 0;
        return false;
    }

    /// <summary>Try to look up a compressed-entry pointer. Returns
    /// true and sets <paramref name="objStream"/> / <paramref name="index"/>
    /// when the object lives inside an ObjStm; false otherwise.</summary>
    public bool TryGetCompressed(
        int objectNumber, out int objStream, out int index)
    {
        if (_entries.TryGetValue(objectNumber, out var e)
            && e.Kind == EntryKind.Compressed)
        {
            objStream = (int)e.Offset;
            index = e.Generation;
            return true;
        }
        objStream = 0;
        index = 0;
        return false;
    }

    /// <summary>Parse the xref table(s) + trailer out of a PDF
    /// byte buffer. Starts by locating <c>startxref</c> near the
    /// end of the file, then chases the <c>/Prev</c> chain back to
    /// the oldest revision — per the spec, later revisions
    /// override earlier ones for the same object number.</summary>
    public static PdfXref Read(byte[] data)
    {
        long startXref = FindStartXref(data);
        var xref = new PdfXref();
        var visited = new HashSet<long>();
        long current = startXref;
        while (current > 0 && visited.Add(current))
        {
            var lexer = new PdfLexer(data, (int)current);
            var head = lexer.NextToken();
            if (head is { Kind: PdfTokenKind.Keyword, Text: "xref" })
            {
                ReadXrefTable(lexer, xref);
                var trailerTok = lexer.NextToken();
                if (trailerTok is not { Kind: PdfTokenKind.Keyword, Text: "trailer" })
                {
                    throw new InvalidDataException(
                        $"Expected 'trailer' after xref block at offset {current}.");
                }
                var parser = new PdfParser(lexer);
                var trailer = parser.ReadObject() as PdfDictionary
                    ?? throw new InvalidDataException(
                        $"Trailer at offset {current} was not a dictionary.");
                // First trailer encountered is authoritative for
                // document-level entries (/Root, /Info, /ID).
                xref.Trailer ??= trailer;
                // Follow /Prev to the next-older revision.
                current = trailer.Entries.TryGetValue("Prev", out var prev)
                    && prev is PdfInt p ? p.Value : 0;
            }
            else
            {
                // PDF 1.5+ cross-reference stream — the xref
                // itself is a compressed stream. Re-read the
                // indirect object at this offset and expand it.
                long? prev = ReadXrefStream(data, (int)current, xref);
                current = prev ?? 0;
                continue;
            }
        }
        return xref;
    }

    private static void ReadXrefTable(PdfLexer lexer, PdfXref xref)
    {
        while (true)
        {
            var tok = lexer.PeekToken();
            if (tok is { Kind: PdfTokenKind.Keyword, Text: "trailer" }) return;
            if (tok is not { Kind: PdfTokenKind.Integer })
            {
                throw new InvalidDataException(
                    $"Expected subsection header or 'trailer' in xref, got {tok?.Kind.ToString() ?? "EOF"} at offset {tok?.Offset ?? -1}.");
            }
            lexer.NextToken(); // first object number
            int first = (int)tok!.IntValue;
            var countTok = lexer.NextToken()
                ?? throw new InvalidDataException("Truncated xref subsection header.");
            if (countTok.Kind != PdfTokenKind.Integer)
            {
                throw new InvalidDataException(
                    "Malformed xref subsection header — count not an integer.");
            }
            int count = (int)countTok.IntValue;
            for (int i = 0; i < count; i++)
            {
                ReadXrefEntry(lexer, xref, first + i);
            }
        }
    }

    /// <summary>Each entry is 20 bytes of the form
    /// <c>"oooooooooo ggggg c \n"</c> — offset, generation, flag
    /// ('n' in-use or 'f' free), EOL. We read whitespace-delimited
    /// tokens via the lexer rather than counting bytes because
    /// real PDFs sometimes deviate from the fixed-width rule
    /// (extra spaces / LF-only EOL).</summary>
    private static void ReadXrefEntry(PdfLexer lexer, PdfXref xref, int objectNumber)
    {
        var offsetTok = lexer.NextToken()
            ?? throw new InvalidDataException("Truncated xref entry.");
        var genTok = lexer.NextToken()
            ?? throw new InvalidDataException("Truncated xref entry.");
        var flagTok = lexer.NextToken()
            ?? throw new InvalidDataException("Truncated xref entry.");
        if (offsetTok.Kind != PdfTokenKind.Integer
            || genTok.Kind != PdfTokenKind.Integer
            || flagTok.Kind != PdfTokenKind.Keyword)
        {
            throw new InvalidDataException(
                $"Malformed xref entry for object {objectNumber} at offset {offsetTok.Offset}.");
        }
        bool inUse = flagTok.Text == "n";
        // Free-list entries (flag 'f') carry the next-free-object
        // pointer rather than a real offset — the caller never
        // queries them, so don't record anything. Only add in-use
        // entries, and only when a later revision hasn't already
        // claimed the slot.
        if (inUse && !xref._entries.ContainsKey(objectNumber))
        {
            xref._entries[objectNumber] = new Entry(
                offsetTok.IntValue, (int)genTok.IntValue,
                EntryKind.Uncompressed);
        }
    }

    /// <summary>Scan backward from the end of the file for
    /// <c>startxref</c>. Parses the integer that follows and
    /// returns it as the byte offset of the root xref block. Per
    /// spec the last 1024 bytes must contain <c>%%EOF</c>; in
    /// practice we're generous and look further.</summary>
    private static long FindStartXref(byte[] data)
    {
        var marker = Encoding.ASCII.GetBytes("startxref");
        int searchEnd = Math.Max(0, data.Length - 4096);
        int pos = -1;
        for (int i = data.Length - marker.Length; i >= searchEnd; i--)
        {
            if (MatchesAt(data, i, marker)) { pos = i; break; }
        }
        if (pos < 0)
        {
            throw new InvalidDataException("'startxref' not found near end of file.");
        }
        int after = pos + marker.Length;
        var lexer = new PdfLexer(data, after);
        var tok = lexer.NextToken()
            ?? throw new InvalidDataException("Truncated 'startxref' block.");
        if (tok.Kind != PdfTokenKind.Integer)
        {
            throw new InvalidDataException(
                $"Expected integer after 'startxref', got {tok.Kind}.");
        }
        return tok.IntValue;
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] needle)
    {
        if (offset + needle.Length > data.Length) return false;
        for (int i = 0; i < needle.Length; i++)
        {
            if (data[offset + i] != needle[i]) return false;
        }
        return true;
    }

    /// <summary>Read a PDF 1.5+ cross-reference stream anchored at
    /// <paramref name="offset"/>. The stream is an indirect object
    /// whose dictionary carries <c>/Type /XRef</c>, <c>/W</c>
    /// (entry column widths), optional <c>/Index</c> (subsection
    /// list, defaulting to the full [0, Size] range), and the
    /// usual trailer entries (<c>/Root</c>, <c>/Info</c>,
    /// <c>/Prev</c>). The decoded payload is a concatenation of
    /// fixed-width entries whose type byte distinguishes free
    /// (0), uncompressed (1), and compressed (2) slots.</summary>
    private static long? ReadXrefStream(byte[] data, int offset, PdfXref xref)
    {
        var lexer = new PdfLexer(data, offset);
        // Consume "N M obj".
        lexer.NextToken(); lexer.NextToken(); lexer.NextToken();
        var parser = new PdfParser(lexer);
        var obj = parser.ReadIndirectObjectBody();
        if (obj is not PdfStream stream)
            throw new InvalidDataException(
                $"Object at offset {offset} was not a stream as expected for an xref stream.");

        xref.Trailer ??= stream.Dictionary;

        // Decode the stream payload through the filter chain.
        var chain = PdfFilters.FilterChain(stream.Dictionary);
        var parms = PdfFilters.ParmsChain(stream.Dictionary);
        var payload = chain.Count == 0 ? stream.RawBytes
            : PdfFilters.Decode(stream.RawBytes, chain, parms);

        // /W is a 3-element array of column widths.
        if (stream.Dictionary.Get("W") is not PdfArray w || w.Count < 3)
            throw new InvalidDataException("xref stream missing /W array.");
        int w1 = (int)((PdfInt)w.Items[0]).Value;
        int w2 = (int)((PdfInt)w.Items[1]).Value;
        int w3 = (int)((PdfInt)w.Items[2]).Value;
        int rowBytes = w1 + w2 + w3;
        if (rowBytes == 0)
            throw new InvalidDataException("xref stream /W declares zero-width rows.");

        // /Index defaults to [0 Size] — a single subsection
        // covering every object from 0 to Size-1.
        IReadOnlyList<(int first, int count)> subsections =
            BuildSubsections(stream.Dictionary);

        int cursor = 0;
        foreach (var (first, count) in subsections)
        {
            for (int i = 0; i < count; i++)
            {
                if (cursor + rowBytes > payload.Length) return null;
                long type = w1 == 0 ? 1 : ReadBE(payload, cursor, w1);
                long f2 = ReadBE(payload, cursor + w1, w2);
                long f3 = ReadBE(payload, cursor + w1 + w2, w3);
                cursor += rowBytes;
                int objNum = first + i;
                if (xref._entries.ContainsKey(objNum)) continue;
                switch (type)
                {
                    case 0:
                        // free entry — omit
                        break;
                    case 1:
                        xref._entries[objNum] = new Entry(
                            f2, (int)f3, EntryKind.Uncompressed);
                        break;
                    case 2:
                        // Compressed: f2 = containing obj stream #,
                        // f3 = index within that stream.
                        xref._entries[objNum] = new Entry(
                            f2, (int)f3, EntryKind.Compressed);
                        break;
                    default:
                        // Unknown entry type — skip silently.
                        break;
                }
            }
        }

        // Return /Prev to continue the revision chain.
        if (stream.Dictionary.Get("Prev") is PdfInt p) return p.Value;
        return null;
    }

    private static IReadOnlyList<(int first, int count)>
        BuildSubsections(PdfDictionary dict)
    {
        if (dict.Get("Index") is PdfArray idx && idx.Count % 2 == 0)
        {
            var result = new List<(int, int)>(idx.Count / 2);
            for (int i = 0; i < idx.Count; i += 2)
            {
                int first = idx.Items[i] is PdfInt a ? (int)a.Value : 0;
                int count = idx.Items[i + 1] is PdfInt b ? (int)b.Value : 0;
                result.Add((first, count));
            }
            return result;
        }
        int size = dict.Get("Size") is PdfInt s ? (int)s.Value : 0;
        return new[] { (0, size) };
    }

    private static long ReadBE(byte[] buf, int offset, int width)
    {
        long v = 0;
        for (int i = 0; i < width; i++)
            v = (v << 8) | buf[offset + i];
        return v;
    }

    internal enum EntryKind { Uncompressed, Compressed }

    internal readonly record struct Entry(
        long Offset, int Generation, EntryKind Kind)
    {
        internal bool InUse => Kind == EntryKind.Uncompressed;
    }
}
