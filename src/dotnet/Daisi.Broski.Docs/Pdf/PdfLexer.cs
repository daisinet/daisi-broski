using System.Globalization;
using System.Text;

namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// Tokenizes PDF content and object syntax per PDF 1.7 spec §7.2.
/// Operates on a raw byte buffer with an absolute-position cursor
/// so the parser can rewind or seek — the xref table points at
/// byte offsets into the file, and we have to land on those
/// offsets directly.
///
/// <para>Stream bodies (the bytes between <c>stream</c> and
/// <c>endstream</c>) are NOT tokenized by this class: the lexer
/// returns the <c>stream</c> keyword, and the caller reads
/// <c>/Length</c> bytes from <see cref="Position"/> to capture
/// the payload before telling the lexer to resume. That's the
/// spec-prescribed flow because stream bodies are opaque to the
/// tokenizer.</para>
/// </summary>
internal sealed class PdfLexer
{
    private readonly byte[] _data;

    /// <summary>Absolute byte offset of the next character to
    /// read. Public so the caller can capture it before reading a
    /// stream body and restore it afterwards.</summary>
    public int Position { get; set; }

    public PdfLexer(byte[] data, int initialPosition = 0)
    {
        _data = data;
        Position = initialPosition;
    }

    /// <summary>Full buffer the lexer is scanning. Exposed so
    /// higher layers can memory-copy stream payloads without
    /// re-reading through Peek.</summary>
    public byte[] Data => _data;

    public int Length => _data.Length;

    /// <summary>Read the next token, skipping whitespace and
    /// comments. Returns null at end-of-buffer.</summary>
    public PdfToken? NextToken()
    {
        SkipWhitespaceAndComments();
        if (Position >= _data.Length) return null;
        byte b = _data[Position];
        return b switch
        {
            (byte)'/' => ReadName(),
            (byte)'(' => ReadLiteralString(),
            (byte)'<' => ReadAngleBracketed(),
            (byte)'>' => ReadCloseDict(),
            (byte)'[' => SingleCharToken(PdfTokenKind.OpenArray),
            (byte)']' => SingleCharToken(PdfTokenKind.CloseArray),
            (byte)'+' or (byte)'-' or (byte)'.' => ReadNumber(),
            _ when IsDigit(b) => ReadNumber(),
            _ => ReadKeyword(),
        };
    }

    /// <summary>Peek the next token without consuming it. Implemented
    /// by saving + restoring <see cref="Position"/>; the lexer is
    /// stateless apart from position so this is safe.</summary>
    public PdfToken? PeekToken()
    {
        int saved = Position;
        var token = NextToken();
        Position = saved;
        return token;
    }

    /// <summary>Advance <see cref="Position"/> past the single line
    /// ending that follows <c>stream</c> (CR-LF or just LF — never
    /// just CR, per the spec). Called by the parser after it
    /// recognizes the <c>stream</c> keyword but before it copies
    /// the payload bytes.</summary>
    public void ConsumeStreamPreamble()
    {
        // Per §7.3.8.1, the line ending MUST be either CR+LF or a
        // lone LF. A lone CR is non-conforming but we tolerate it
        // (seen in the wild, rare).
        if (Position < _data.Length && _data[Position] == (byte)'\r')
        {
            Position++;
            if (Position < _data.Length && _data[Position] == (byte)'\n') Position++;
        }
        else if (Position < _data.Length && _data[Position] == (byte)'\n')
        {
            Position++;
        }
    }

    // -------------------- scanners --------------------

    private PdfToken ReadName()
    {
        int start = Position;
        Position++; // consume '/'
        var sb = new StringBuilder();
        while (Position < _data.Length)
        {
            byte b = _data[Position];
            if (IsWhitespace(b) || IsDelimiter(b)) break;
            if (b == (byte)'#' && Position + 2 < _data.Length)
            {
                int hi = HexValue(_data[Position + 1]);
                int lo = HexValue(_data[Position + 2]);
                if (hi >= 0 && lo >= 0)
                {
                    sb.Append((char)((hi << 4) | lo));
                    Position += 3;
                    continue;
                }
            }
            sb.Append((char)b);
            Position++;
        }
        return new PdfToken(PdfTokenKind.Name, start, sb.ToString(), 0);
    }

    private PdfToken ReadLiteralString()
    {
        int start = Position;
        Position++; // consume '('
        var bytes = new List<byte>();
        int parenDepth = 1;
        while (Position < _data.Length && parenDepth > 0)
        {
            byte b = _data[Position++];
            if (b == (byte)'\\')
            {
                if (Position >= _data.Length) break;
                byte esc = _data[Position++];
                switch (esc)
                {
                    case (byte)'n': bytes.Add((byte)'\n'); break;
                    case (byte)'r': bytes.Add((byte)'\r'); break;
                    case (byte)'t': bytes.Add((byte)'\t'); break;
                    case (byte)'b': bytes.Add((byte)'\b'); break;
                    case (byte)'f': bytes.Add((byte)'\f'); break;
                    case (byte)'(': bytes.Add((byte)'('); break;
                    case (byte)')': bytes.Add((byte)')'); break;
                    case (byte)'\\': bytes.Add((byte)'\\'); break;
                    case (byte)'\n': break; // line continuation
                    case (byte)'\r':
                        if (Position < _data.Length && _data[Position] == (byte)'\n')
                            Position++;
                        break;
                    default:
                        if (IsOctalDigit(esc))
                        {
                            int val = esc - '0';
                            for (int i = 0; i < 2 && Position < _data.Length
                                && IsOctalDigit(_data[Position]); i++)
                            {
                                val = val * 8 + (_data[Position++] - '0');
                            }
                            bytes.Add((byte)(val & 0xFF));
                        }
                        else
                        {
                            // Unknown escape: spec says drop the
                            // backslash, keep the character. This
                            // preserves (almost) all information and
                            // matches Adobe's behavior.
                            bytes.Add(esc);
                        }
                        break;
                }
                continue;
            }
            if (b == (byte)'(') { parenDepth++; bytes.Add(b); continue; }
            if (b == (byte)')')
            {
                parenDepth--;
                if (parenDepth == 0) break;
                bytes.Add(b);
                continue;
            }
            if (b == (byte)'\r')
            {
                // Normalize CR / CRLF line endings inside strings
                // to LF per §7.3.4.2.
                bytes.Add((byte)'\n');
                if (Position < _data.Length && _data[Position] == (byte)'\n')
                    Position++;
                continue;
            }
            bytes.Add(b);
        }
        return new PdfToken(PdfTokenKind.LiteralString, start, "", 0)
        {
            ByteValue = bytes.ToArray(),
        };
    }

    /// <summary>Dispatch on the byte after <c>&lt;</c>: a second
    /// <c>&lt;</c> means an open-dict delimiter, anything else
    /// starts a hex string.</summary>
    private PdfToken ReadAngleBracketed()
    {
        int start = Position;
        Position++; // '<'
        if (Position < _data.Length && _data[Position] == (byte)'<')
        {
            Position++;
            return new PdfToken(PdfTokenKind.OpenDict, start, "", 0);
        }
        return ReadHexString(start);
    }

    private PdfToken ReadHexString(int start)
    {
        var bytes = new List<byte>();
        int pendingHi = -1;
        while (Position < _data.Length)
        {
            byte b = _data[Position];
            if (b == (byte)'>') { Position++; break; }
            Position++;
            if (IsWhitespace(b)) continue;
            int v = HexValue(b);
            if (v < 0) continue; // spec permits garbage between hex chars; be tolerant
            if (pendingHi < 0)
            {
                pendingHi = v;
            }
            else
            {
                bytes.Add((byte)((pendingHi << 4) | v));
                pendingHi = -1;
            }
        }
        if (pendingHi >= 0)
        {
            // Odd number of hex digits: the trailing one is
            // left-aligned (multiplied by 0x10).
            bytes.Add((byte)(pendingHi << 4));
        }
        return new PdfToken(PdfTokenKind.HexString, start, "", 0)
        {
            ByteValue = bytes.ToArray(),
        };
    }

    private PdfToken ReadCloseDict()
    {
        int start = Position;
        Position++;
        if (Position < _data.Length && _data[Position] == (byte)'>')
        {
            Position++;
            return new PdfToken(PdfTokenKind.CloseDict, start, "", 0);
        }
        // Stray '>' without a matching '<'. Rather than throw
        // (content streams and malformed PDFs occasionally include
        // one as a spurious byte), synthesize a close-dict token
        // so the parser can continue and surface a cleaner error
        // if it matters. The caller will either discard it or fail
        // when it doesn't close the currently-open dict.
        return new PdfToken(PdfTokenKind.CloseDict, start, "", 0);
    }

    private PdfToken ReadNumber()
    {
        int start = Position;
        bool sign = _data[Position] is (byte)'-' or (byte)'+';
        if (sign) Position++;
        bool dot = false;
        int digitCount = 0;
        while (Position < _data.Length)
        {
            byte b = _data[Position];
            if (IsDigit(b)) { Position++; digitCount++; continue; }
            if (b == (byte)'.' && !dot) { dot = true; Position++; continue; }
            break;
        }
        int len = Position - start;
        var s = Encoding.ASCII.GetString(_data, start, len);
        // Degenerate cases: no digits accumulated (input was just
        // "+", "-", ".", "+.", etc). Don't try to parse as a
        // number — emit as a keyword so the fuzz harness / real
        // caller can treat it as an unrecognized token rather
        // than throwing FormatException.
        if (digitCount == 0)
        {
            return new PdfToken(PdfTokenKind.Keyword, start, s, 0);
        }
        if (dot)
        {
            if (!double.TryParse(s, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var real))
            {
                return new PdfToken(PdfTokenKind.Keyword, start, s, 0);
            }
            return new PdfToken(PdfTokenKind.Real, start, s, real);
        }
        if (!long.TryParse(s, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var intVal))
        {
            // Integer overflow or similar — also emit as keyword.
            return new PdfToken(PdfTokenKind.Keyword, start, s, 0);
        }
        return new PdfToken(PdfTokenKind.Integer, start, s, intVal)
        {
            IntValue = intVal,
        };
    }

    private PdfToken ReadKeyword()
    {
        int start = Position;
        var sb = new StringBuilder();
        while (Position < _data.Length)
        {
            byte b = _data[Position];
            if (IsWhitespace(b) || IsDelimiter(b)) break;
            sb.Append((char)b);
            Position++;
        }
        // Guarantee progress. If the byte at Position is a delimiter
        // we don't explicitly handle in NextToken's dispatch (stray
        // ')', '{', '}', or a byte we've failed to classify), we'd
        // otherwise loop forever returning zero-length keywords.
        // Advance past the offending byte and emit an empty keyword
        // so the caller can treat it as "unknown token" rather than
        // stall.
        if (Position == start && Position < _data.Length)
        {
            Position++;
        }
        var s = sb.ToString();
        return s switch
        {
            "true" => new PdfToken(PdfTokenKind.BooleanTrue, start, s, 0),
            "false" => new PdfToken(PdfTokenKind.BooleanFalse, start, s, 0),
            "null" => new PdfToken(PdfTokenKind.Null, start, s, 0),
            _ => new PdfToken(PdfTokenKind.Keyword, start, s, 0),
        };
    }

    private PdfToken SingleCharToken(PdfTokenKind kind)
    {
        int pos = Position;
        Position++;
        return new PdfToken(kind, pos, "", 0);
    }

    private void SkipWhitespaceAndComments()
    {
        while (Position < _data.Length)
        {
            byte b = _data[Position];
            if (IsWhitespace(b)) { Position++; continue; }
            if (b == (byte)'%')
            {
                // Comment to end-of-line.
                while (Position < _data.Length
                       && _data[Position] != (byte)'\r'
                       && _data[Position] != (byte)'\n')
                {
                    Position++;
                }
                continue;
            }
            break;
        }
    }

    // -------------------- character classes --------------------

    internal static bool IsWhitespace(byte b) =>
        b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    internal static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
          or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}'
          or (byte)'/' or (byte)'%';

    internal static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';
    internal static bool IsOctalDigit(byte b) => b >= (byte)'0' && b <= (byte)'7';

    internal static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        _ => -1,
    };
}

internal enum PdfTokenKind
{
    Integer,
    Real,
    Name,
    LiteralString,
    HexString,
    OpenArray,
    CloseArray,
    OpenDict,
    CloseDict,
    BooleanTrue,
    BooleanFalse,
    Null,
    Keyword,
}

internal sealed class PdfToken
{
    public PdfTokenKind Kind { get; }
    public int Offset { get; }
    public string Text { get; }
    public double NumberValue { get; }
    public long IntValue { get; init; }
    public byte[]? ByteValue { get; init; }

    public PdfToken(PdfTokenKind kind, int offset, string text, double numberValue)
    {
        Kind = kind;
        Offset = offset;
        Text = text;
        NumberValue = numberValue;
    }
}
