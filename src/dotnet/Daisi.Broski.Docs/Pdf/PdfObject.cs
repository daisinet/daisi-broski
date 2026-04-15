namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// Base class for every PDF object (PDF 1.7 spec §7.3). The spec
/// identifies eight types: boolean, integer, real, string, name,
/// array, dictionary, stream, plus the null object and the
/// indirect-reference meta-type. We model them as sealed
/// subclasses rather than a discriminated union so the parser's
/// pattern matches stay readable and the tree is easy to walk.
/// </summary>
internal abstract class PdfObject;

internal sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();
    private PdfNull() { }
    public override string ToString() => "null";
}

internal sealed class PdfBool : PdfObject
{
    public static readonly PdfBool True = new(true);
    public static readonly PdfBool False = new(false);
    public bool Value { get; }
    private PdfBool(bool value) => Value = value;
    public static PdfBool Of(bool v) => v ? True : False;
    public override string ToString() => Value ? "true" : "false";
}

/// <summary>Integer (64-bit so we can express xref offsets in
/// multi-GB PDFs — the spec caps integers at ±2^31-1 but
/// real-world files occasionally exceed that).</summary>
internal sealed class PdfInt : PdfObject
{
    public long Value { get; }
    public PdfInt(long value) => Value = value;
    public override string ToString() => Value.ToString(
        System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class PdfReal : PdfObject
{
    public double Value { get; }
    public PdfReal(double value) => Value = value;
    public override string ToString() => Value.ToString(
        System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>PDF name object (<c>/Foo</c>). The leading slash is
/// syntactic; we store the logical name without it. Characters
/// were originally limited to ASCII printable; PDF 1.2+ allows
/// any byte via <c>#XX</c> hex escapes. We decode on parse — the
/// stored value is the logical string the author intended.</summary>
internal sealed class PdfName : PdfObject
{
    public string Value { get; }
    public PdfName(string value) => Value = value;
    public override bool Equals(object? obj) => obj is PdfName n && n.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => "/" + Value;
}

/// <summary>Literal-string <c>(abc)</c> or hex-string <c>&lt;48656C6C6F&gt;</c>.
/// Stored as raw bytes because the spec is byte-oriented;
/// interpretation (PDFDocEncoding, UTF-16BE with BOM, or a font
/// encoding) is the caller's problem.</summary>
internal sealed class PdfString : PdfObject
{
    public byte[] Bytes { get; }
    public bool Hex { get; }
    public PdfString(byte[] bytes, bool hex)
    {
        Bytes = bytes;
        Hex = hex;
    }

    /// <summary>Decode the string for use as metadata (title,
    /// author, /Info entries). PDF text strings start with the
    /// UTF-16 BE BOM (<c>0xFEFF</c>); otherwise they're
    /// PDFDocEncoding (Latin-1 with a handful of substitutions
    /// we approximate as Latin-1).</summary>
    public string AsText()
    {
        if (Bytes.Length >= 2 && Bytes[0] == 0xFE && Bytes[1] == 0xFF)
        {
            return System.Text.Encoding.BigEndianUnicode
                .GetString(Bytes, 2, Bytes.Length - 2);
        }
        if (Bytes.Length >= 3 && Bytes[0] == 0xEF && Bytes[1] == 0xBB && Bytes[2] == 0xBF)
        {
            return System.Text.Encoding.UTF8
                .GetString(Bytes, 3, Bytes.Length - 3);
        }
        return System.Text.Encoding.Latin1.GetString(Bytes);
    }

    public override string ToString() => AsText();
}

/// <summary>Array of heterogeneous objects (<c>[1 2 /Foo]</c>).</summary>
internal sealed class PdfArray : PdfObject
{
    public List<PdfObject> Items { get; } = new();
    public int Count => Items.Count;
    public PdfObject this[int index] => Items[index];

    public override string ToString() => "[" +
        string.Join(" ", Items) + "]";
}

/// <summary>Dictionary: an unordered mapping of
/// <c>PdfName → PdfObject</c>. Keys are compared by their logical
/// value (after <c>#XX</c> decoding).</summary>
internal sealed class PdfDictionary : PdfObject
{
    public Dictionary<string, PdfObject> Entries { get; } = new(StringComparer.Ordinal);
    public PdfObject? Get(string key)
        => Entries.TryGetValue(key, out var v) ? v : null;
    public bool TryGetValue(string key, out PdfObject? value)
    {
        if (Entries.TryGetValue(key, out var v)) { value = v; return true; }
        value = null;
        return false;
    }

    public override string ToString() => "<<" +
        string.Join(" ", Entries.Select(kv => "/" + kv.Key + " " + kv.Value))
        + ">>";
}

/// <summary>Stream object: a dictionary plus a raw byte payload.
/// The dictionary's <c>/Length</c> names the byte count,
/// <c>/Filter</c> names zero or more decoder filters to apply in
/// order, and <c>/DecodeParms</c> carries filter-specific tuning
/// (the Flate/LZW predictor, mainly). Payload is the raw bytes
/// between <c>stream</c> and <c>endstream</c>, undecoded.</summary>
internal sealed class PdfStream : PdfObject
{
    public PdfDictionary Dictionary { get; }
    public byte[] RawBytes { get; }

    public PdfStream(PdfDictionary dict, byte[] rawBytes)
    {
        Dictionary = dict;
        RawBytes = rawBytes;
    }

    public override string ToString() => $"<stream {RawBytes.Length} bytes>";
}

/// <summary>Indirect reference — <c>12 0 R</c> in the file, points
/// at the object with number 12 and generation 0. Resolution
/// (looking the object up via the xref table) happens at a higher
/// layer.</summary>
internal sealed class PdfRef : PdfObject
{
    public int ObjectNumber { get; }
    public int Generation { get; }
    public PdfRef(int objectNumber, int generation)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
    }
    public override string ToString() => $"{ObjectNumber} {Generation} R";
}

/// <summary>A raw PDF operator keyword inside a content stream —
/// <c>Tj</c>, <c>BT</c>, <c>cm</c>, etc. Only produced by the
/// lexer when it recognizes an unreserved word that isn't one of
/// the object keywords (<c>true</c>, <c>false</c>, <c>null</c>,
/// <c>obj</c>, <c>endobj</c>, <c>stream</c>, <c>endstream</c>,
/// <c>R</c>, <c>xref</c>, <c>trailer</c>, <c>startxref</c>).</summary>
internal sealed class PdfOperator : PdfObject
{
    public string Name { get; }
    public PdfOperator(string name) => Name = name;
    public override string ToString() => Name;
}
