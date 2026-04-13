using Daisi.Broski.Engine.Css;

namespace Daisi.Broski.Engine.Fonts;

/// <summary>
/// Pulls <c>@font-face</c> rules out of a parsed stylesheet
/// and normalizes them into a list of (family, weight, style,
/// URL, format) tuples the fetcher can consume. Each
/// <c>src: url(...) format(...), url(...) format(...)</c>
/// list expands into multiple candidates — the fetcher picks
/// the first one with a format we know how to parse (and
/// falls through formats we don't, instead of failing hard).
/// </summary>
internal static class FontFaceParser
{
    /// <summary>Parsed candidate — not yet fetched.</summary>
    public sealed record Candidate(
        string Family, int Weight, string Style, string Src, string Format,
        IReadOnlyList<(int Start, int End)> UnicodeRange);

    public static List<Candidate> Extract(Stylesheet sheet)
    {
        var results = new List<Candidate>();
        foreach (var rule in sheet.Rules)
        {
            if (rule is not AtRule ar) continue;
            if (!string.Equals(ar.Name, "font-face", StringComparison.OrdinalIgnoreCase)) continue;
            ParseFaceBlock(ar, results);
        }
        return results;
    }

    private static void ParseFaceBlock(AtRule rule, List<Candidate> into)
    {
        string family = "";
        string weight = "400";
        string style = "normal";
        string src = "";
        string unicodeRange = "";
        foreach (var d in rule.Declarations)
        {
            switch (d.Property.ToLowerInvariant())
            {
                case "font-family": family = Unquote(d.Value.Trim()); break;
                case "font-weight": weight = d.Value.Trim(); break;
                case "font-style": style = d.Value.Trim(); break;
                case "src": src = d.Value; break;
                case "unicode-range": unicodeRange = d.Value; break;
            }
        }
        if (string.IsNullOrEmpty(family) || string.IsNullOrEmpty(src)) return;
        int weightInt = ParseFirstInt(weight, 400);
        var ranges = ParseUnicodeRange(unicodeRange);
        foreach (var entry in SplitSrc(src))
        {
            into.Add(new Candidate(family, weightInt, style, entry.Url, entry.Format, ranges));
        }
    }

    /// <summary>Parse a CSS <c>unicode-range</c> value into a
    /// list of [start, end] code-point ranges. Honors the
    /// three forms the spec calls out:
    /// <list type="bullet">
    /// <item><c>U+26</c> — single code point.</item>
    /// <item><c>U+0-7F</c> — explicit range.</item>
    /// <item><c>U+4??</c> — wildcard range
    ///   (<c>?</c> expands to <c>0</c>..<c>F</c>).</item>
    /// </list>
    /// Empty / "U+0-10FFFF" / unparseable input returns an
    /// empty list (interpreted as "covers everything").</summary>
    internal static List<(int Start, int End)> ParseUnicodeRange(string value)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (var rawPart in value.Split(','))
        {
            var part = rawPart.Trim();
            if (!part.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) continue;
            part = part.Substring(2);
            // Wildcard form: "4??" → [400, 4FF].
            if (part.Contains('?'))
            {
                var lo = part.Replace('?', '0');
                var hi = part.Replace('?', 'F');
                if (TryHex(lo, out var sLo) && TryHex(hi, out var sHi))
                {
                    result.Add((sLo, sHi));
                }
                continue;
            }
            // Range form: "0-7F".
            int dash = part.IndexOf('-');
            if (dash > 0)
            {
                var lo = part.Substring(0, dash);
                var hi = part.Substring(dash + 1);
                if (TryHex(lo, out var s) && TryHex(hi, out var e))
                {
                    result.Add((s, e));
                }
                continue;
            }
            // Single code point.
            if (TryHex(part, out var cp))
            {
                result.Add((cp, cp));
            }
        }
        return result;
    }

    private static bool TryHex(string s, out int value) =>
        int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    private readonly record struct SrcEntry(string Url, string Format);

    /// <summary>Split a <c>src</c> value into its comma-
    /// separated candidates, extracting the <c>url(...)</c>
    /// target and optional <c>format(...)</c> hint from each
    /// one. Handles quoted / unquoted URLs and
    /// <c>local(Name)</c> entries (which we skip — we can't
    /// access OS-installed fonts from a sandboxed renderer).</summary>
    private static List<SrcEntry> SplitSrc(string src)
    {
        var entries = new List<SrcEntry>();
        var parts = SplitTopLevel(src, ',');
        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (part.StartsWith("local(", StringComparison.OrdinalIgnoreCase)) continue;

            string url = ExtractFuncArg(part, "url");
            if (string.IsNullOrEmpty(url)) continue;
            string format = ExtractFuncArg(part, "format");
            entries.Add(new SrcEntry(Unquote(url), Unquote(format).ToLowerInvariant()));
        }
        return entries;
    }

    /// <summary>Find <c>name(arg)</c> in <paramref name="src"/>
    /// and return the inner arg text (stripped of any
    /// surrounding whitespace). Returns empty when absent.
    /// Not a full CSS function parser — sufficient for the
    /// <c>url(...)</c> / <c>format(...)</c> use cases
    /// <c>@font-face</c> actually produces.</summary>
    private static string ExtractFuncArg(string src, string name)
    {
        int i = src.IndexOf(name + "(", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        int start = i + name.Length + 1;
        int depth = 1;
        int j = start;
        while (j < src.Length && depth > 0)
        {
            if (src[j] == '(') depth++;
            else if (src[j] == ')') depth--;
            if (depth > 0) j++;
        }
        if (j >= src.Length) return "";
        return src.Substring(start, j - start).Trim();
    }

    private static string Unquote(string s)
    {
        if (s.Length < 2) return s;
        char f = s[0], l = s[^1];
        if ((f == '"' && l == '"') || (f == '\'' && l == '\''))
        {
            return s.Substring(1, s.Length - 2);
        }
        return s;
    }

    private static int ParseFirstInt(string s, int fallback)
    {
        int i = 0;
        while (i < s.Length && !char.IsDigit(s[i])) i++;
        int start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (start == i) return fallback;
        return int.Parse(s.AsSpan(start, i - start));
    }

    private static List<string> SplitTopLevel(string s, char sep)
    {
        var parts = new List<string>();
        int depth = 0;
        var sb = new System.Text.StringBuilder();
        bool inString = false;
        char stringQuote = '"';
        foreach (var c in s)
        {
            if (inString)
            {
                sb.Append(c);
                if (c == stringQuote) inString = false;
                continue;
            }
            if (c == '"' || c == '\'')
            {
                inString = true;
                stringQuote = c;
                sb.Append(c);
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c == sep && depth == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return parts;
    }
}
