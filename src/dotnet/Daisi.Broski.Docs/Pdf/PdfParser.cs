namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// Consumes tokens from <see cref="PdfLexer"/> and assembles them
/// into the <see cref="PdfObject"/> tree. Knows just enough about
/// PDF's grammar (PDF 1.7 §7.3) to recognize compound objects —
/// array, dictionary, stream, indirect reference — and the
/// top-level "N M obj … endobj" wrapping used by indirect objects
/// referenced through the xref table.
/// </summary>
internal sealed class PdfParser
{
    private readonly PdfLexer _lexer;

    public PdfParser(PdfLexer lexer) => _lexer = lexer;

    /// <summary>The underlying lexer. Exposed so higher layers can
    /// seek to a byte offset (indirect-object bodies are anchored
    /// by the xref table) without round-tripping through a second
    /// lexer instance.</summary>
    public PdfLexer Lexer => _lexer;

    /// <summary>Read the next token and turn it into an object.
    /// Integers have a special case: two integers followed by the
    /// <c>R</c> keyword form an indirect reference (<c>12 0 R</c>).
    /// The caller doesn't need to know this happened —
    /// <see cref="PdfRef"/> comes out of the same method as
    /// everything else.</summary>
    public PdfObject ReadObject()
    {
        var token = _lexer.NextToken()
            ?? throw new InvalidDataException(
                "Unexpected end of input while reading object.");
        return ReadObjectFromToken(token);
    }

    /// <summary>Read an indirect object's body: the lexer is
    /// positioned just past <c>N M obj</c>, and this method reads
    /// the contained value (plus optional stream payload) up to
    /// <c>endobj</c>. Returns the inner object (often a
    /// <see cref="PdfDictionary"/> or <see cref="PdfStream"/>).
    /// The caller is expected to have validated the N / M header
    /// against the xref entry.</summary>
    public PdfObject ReadIndirectObjectBody()
    {
        var value = ReadObject();
        // If what follows is a `stream` keyword, the object is a
        // PdfStream — fold the payload in.
        var next = _lexer.PeekToken();
        if (next is { Kind: PdfTokenKind.Keyword, Text: "stream" }
            && value is PdfDictionary dict)
        {
            _lexer.NextToken(); // consume "stream"
            _lexer.ConsumeStreamPreamble();
            int payloadStart = _lexer.Position;
            int? directLength = ResolveStreamLength(dict);
            int length = directLength ?? ScanForEndstream(payloadStart);
            if (length < 0 || payloadStart + length > _lexer.Length)
            {
                throw new InvalidDataException(
                    $"Stream /Length {length} overruns the buffer.");
            }
            var bytes = new byte[length];
            Array.Copy(_lexer.Data, payloadStart, bytes, 0, length);
            _lexer.Position = payloadStart + length;
            value = new PdfStream(dict, bytes);
            // Consume the 'endstream' keyword (tolerating
            // whitespace + a possible trailing EOL).
            var end = _lexer.NextToken();
            if (end is not { Kind: PdfTokenKind.Keyword, Text: "endstream" })
            {
                throw new InvalidDataException(
                    $"Expected 'endstream' after stream payload at offset {_lexer.Position}.");
            }
        }
        // Consume 'endobj' if present. It's optional from our
        // point of view — higher layers anchor objects by file
        // offset, not by the closing keyword.
        var endTok = _lexer.PeekToken();
        if (endTok is { Kind: PdfTokenKind.Keyword, Text: "endobj" })
        {
            _lexer.NextToken();
        }
        return value;
    }

    private PdfObject ReadObjectFromToken(PdfToken token)
    {
        switch (token.Kind)
        {
            case PdfTokenKind.BooleanTrue:
                return PdfBool.True;
            case PdfTokenKind.BooleanFalse:
                return PdfBool.False;
            case PdfTokenKind.Null:
                return PdfNull.Instance;
            case PdfTokenKind.Integer:
                return TryReadIndirectRef(token);
            case PdfTokenKind.Real:
                return new PdfReal(token.NumberValue);
            case PdfTokenKind.Name:
                return new PdfName(token.Text);
            case PdfTokenKind.LiteralString:
                return new PdfString(token.ByteValue!, hex: false);
            case PdfTokenKind.HexString:
                return new PdfString(token.ByteValue!, hex: true);
            case PdfTokenKind.OpenArray:
                return ReadArray();
            case PdfTokenKind.OpenDict:
                return ReadDictionary();
            case PdfTokenKind.Keyword:
                // Operators in content streams come through here.
                return new PdfOperator(token.Text);
            default:
                throw new InvalidDataException(
                    $"Unexpected token {token.Kind} ('{token.Text}') at offset {token.Offset}.");
        }
    }

    /// <summary>If the integer we just consumed is the first part
    /// of <c>N M R</c>, build a <see cref="PdfRef"/>. Otherwise
    /// return a plain <see cref="PdfInt"/>.</summary>
    private PdfObject TryReadIndirectRef(PdfToken intToken)
    {
        int saved = _lexer.Position;
        var second = _lexer.NextToken();
        if (second is { Kind: PdfTokenKind.Integer })
        {
            var third = _lexer.NextToken();
            if (third is { Kind: PdfTokenKind.Keyword, Text: "R" })
            {
                return new PdfRef((int)intToken.IntValue, (int)second.IntValue);
            }
        }
        _lexer.Position = saved;
        return new PdfInt(intToken.IntValue);
    }

    private PdfArray ReadArray()
    {
        var arr = new PdfArray();
        while (true)
        {
            var token = _lexer.NextToken()
                ?? throw new InvalidDataException("Array terminated by EOF.");
            if (token.Kind == PdfTokenKind.CloseArray) return arr;
            arr.Items.Add(ReadObjectFromToken(token));
        }
    }

    private PdfDictionary ReadDictionary()
    {
        var dict = new PdfDictionary();
        while (true)
        {
            var keyToken = _lexer.NextToken()
                ?? throw new InvalidDataException("Dictionary terminated by EOF.");
            if (keyToken.Kind == PdfTokenKind.CloseDict) return dict;
            if (keyToken.Kind != PdfTokenKind.Name)
            {
                throw new InvalidDataException(
                    $"Expected name key in dictionary, got {keyToken.Kind} at offset {keyToken.Offset}.");
            }
            var value = ReadObject();
            dict.Entries[keyToken.Text] = value;
        }
    }

    /// <summary>Pull a stream's byte length from its dictionary.
    /// Returns null when <c>/Length</c> is missing or indirect —
    /// the parser's caller falls back to <see cref="ScanForEndstream"/>
    /// to find the payload boundary by structure.</summary>
    private static int? ResolveStreamLength(PdfDictionary dict)
    {
        if (!dict.Entries.TryGetValue("Length", out var lenObj)) return null;
        return lenObj switch
        {
            PdfInt i => (int)i.Value,
            _ => null,
        };
    }

    /// <summary>Scan forward from <paramref name="start"/> for the
    /// keyword <c>endstream</c> preceded by EOL whitespace, and
    /// return the byte length of the payload that ends just before
    /// it. Used when <c>/Length</c> is an indirect reference (the
    /// parser doesn't have an xref) or absent — the spec requires
    /// the keyword to be on its own line, so we accept either a
    /// preceding LF or CR-LF as the boundary.</summary>
    private int ScanForEndstream(int start)
    {
        var data = _lexer.Data;
        var needle = "endstream"u8;
        int limit = data.Length - needle.Length;
        for (int i = start; i <= limit; i++)
        {
            if (data[i] != (byte)'e') continue;
            bool match = true;
            for (int k = 1; k < needle.Length; k++)
            {
                if (data[i + k] != needle[k]) { match = false; break; }
            }
            if (!match) continue;
            // Walk back over the EOL that the spec mandates
            // immediately before endstream. CR, LF, or CR-LF.
            int end = i;
            if (end > start && data[end - 1] == (byte)'\n') end--;
            if (end > start && data[end - 1] == (byte)'\r') end--;
            return end - start;
        }
        throw new InvalidDataException(
            $"Could not locate 'endstream' for stream starting at offset {start}.");
    }
}
