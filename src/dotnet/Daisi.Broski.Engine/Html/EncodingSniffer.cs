using System.Text;

namespace Daisi.Broski.Engine.Html;

/// <summary>
/// Detects the character encoding of an HTML byte stream, loosely following
/// the WHATWG HTML encoding-sniffing algorithm (§12.2.3).
///
/// Order of precedence:
///   1. Byte-order mark (BOM) — highest confidence, overrides everything.
///   2. Content-Type header <c>charset=</c> parameter.
///   3. <c>&lt;meta&gt;</c> prescan of the first 1024 bytes.
///   4. UTF-8 fallback.
///
/// This is a pragmatic subset of the full spec. Notably:
///
/// - The meta prescan does a lightweight search for a <c>charset=</c>
///   marker inside the prescan window rather than running the full WHATWG
///   prescan state machine. In particular, a <c>charset=</c> embedded in
///   a comment *could* mislead us. Real sites don't do that.
/// - Only encodings natively supported by .NET 10's base <see cref="Encoding"/>
///   class are recognized. Legacy pages in <c>windows-1252</c>,
///   <c>shift_jis</c>, or <c>gbk</c> would need a <c>CodePages</c> encoding
///   provider registered at startup; this file does not register one.
///   Unrecognized encoding names fall back to UTF-8.
/// </summary>
public static class EncodingSniffer
{
    private static readonly byte[] BomUtf8 = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] BomUtf16Be = [0xFE, 0xFF];
    private static readonly byte[] BomUtf16Le = [0xFF, 0xFE];

    /// <summary>
    /// Determine the most likely encoding of the given HTML byte stream.
    /// Returns the fallback (UTF-8) if nothing authoritative is found.
    /// </summary>
    /// <param name="bytes">Prefix of the HTML response body. Only the first
    /// 1024 bytes are consulted for the meta prescan; the caller can pass
    /// more without penalty.</param>
    /// <param name="contentType">Optional <c>Content-Type</c> response header
    /// value. If it contains a valid <c>charset=</c> parameter, that wins
    /// over any later prescan.</param>
    public static Encoding Sniff(ReadOnlySpan<byte> bytes, string? contentType = null)
    {
        // 1. BOM (authoritative — spec says BOM always wins).
        if (bytes.Length >= 3 &&
            bytes[0] == BomUtf8[0] && bytes[1] == BomUtf8[1] && bytes[2] == BomUtf8[2])
        {
            return Encoding.UTF8;
        }
        if (bytes.Length >= 2 && bytes[0] == BomUtf16Be[0] && bytes[1] == BomUtf16Be[1])
        {
            return Encoding.BigEndianUnicode;
        }
        if (bytes.Length >= 2 && bytes[0] == BomUtf16Le[0] && bytes[1] == BomUtf16Le[1])
        {
            return Encoding.Unicode;
        }

        // 2. Content-Type charset parameter.
        if (!string.IsNullOrEmpty(contentType) &&
            TryParseCharsetFromContentType(contentType, out var ctEncoding))
        {
            return ctEncoding;
        }

        // 3. Meta prescan over the first 1024 bytes.
        int prescanLen = Math.Min(bytes.Length, 1024);
        if (prescanLen > 0 && TryPrescanMeta(bytes[..prescanLen], out var metaEncoding))
        {
            return metaEncoding;
        }

        // 4. Fallback.
        return Encoding.UTF8;
    }

    private static bool TryParseCharsetFromContentType(string contentType, out Encoding encoding)
    {
        encoding = Encoding.UTF8;

        int idx = contentType.IndexOf("charset", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        int cursor = idx + "charset".Length;

        // Allow whitespace before '='
        while (cursor < contentType.Length && (contentType[cursor] == ' ' || contentType[cursor] == '\t'))
            cursor++;

        if (cursor >= contentType.Length || contentType[cursor] != '=')
            return false;

        cursor++; // skip '='
        while (cursor < contentType.Length && (contentType[cursor] == ' ' || contentType[cursor] == '\t'))
            cursor++;

        if (cursor >= contentType.Length) return false;

        // Optional quoted value.
        char quote = '\0';
        if (contentType[cursor] == '"' || contentType[cursor] == '\'')
        {
            quote = contentType[cursor];
            cursor++;
        }

        int start = cursor;
        while (cursor < contentType.Length)
        {
            char c = contentType[cursor];
            if (quote != '\0')
            {
                if (c == quote) break;
            }
            else if (c == ';' || c == ' ' || c == '\t')
            {
                break;
            }
            cursor++;
        }

        var name = contentType[start..cursor];
        return TryGetEncoding(name, out encoding);
    }

    private static bool TryPrescanMeta(ReadOnlySpan<byte> bytes, out Encoding encoding)
    {
        encoding = Encoding.UTF8;

        // Decode the prescan window as ASCII. The bytes we care about
        // (charset=, tag characters) are all ASCII regardless of the
        // actual underlying encoding, so this is safe.
        var text = Encoding.ASCII.GetString(bytes);

        int idx = 0;
        while (idx < text.Length)
        {
            int found = text.IndexOf("charset", idx, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;

            int cursor = found + "charset".Length;
            while (cursor < text.Length && (text[cursor] == ' ' || text[cursor] == '\t'))
                cursor++;

            if (cursor < text.Length && text[cursor] == '=')
            {
                cursor++;
                while (cursor < text.Length && (text[cursor] == ' ' || text[cursor] == '\t'))
                    cursor++;

                char quote = '\0';
                if (cursor < text.Length && (text[cursor] == '"' || text[cursor] == '\''))
                {
                    quote = text[cursor];
                    cursor++;
                }

                int start = cursor;
                while (cursor < text.Length)
                {
                    char c = text[cursor];
                    if (quote != '\0')
                    {
                        if (c == quote) break;
                    }
                    else if (c is '"' or '\'' or ' ' or '\t' or ';' or '>' or '/' or '\r' or '\n')
                    {
                        break;
                    }
                    cursor++;
                }

                var name = text[start..cursor];
                if (TryGetEncoding(name, out encoding))
                    return true;
            }

            idx = found + 1;
        }

        return false;
    }

    private static bool TryGetEncoding(string name, out Encoding encoding)
    {
        encoding = Encoding.UTF8;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var normalized = name.Trim().ToLowerInvariant();

        // Fast path for the names that appear on practically every site.
        switch (normalized)
        {
            case "utf-8" or "utf8":
                encoding = Encoding.UTF8;
                return true;
            case "us-ascii" or "ascii":
                encoding = Encoding.ASCII;
                return true;
            case "utf-16" or "utf-16le":
                encoding = Encoding.Unicode;
                return true;
            case "utf-16be":
                encoding = Encoding.BigEndianUnicode;
                return true;
        }

        // Fall back to the system lookup for anything else. This will
        // throw for names not registered with the current runtime
        // (anything outside the default set on .NET Core+), which we
        // catch and report as "unknown."
        try
        {
            encoding = Encoding.GetEncoding(normalized);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
