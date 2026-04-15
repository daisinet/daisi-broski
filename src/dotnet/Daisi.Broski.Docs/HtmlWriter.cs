using System.Text;

namespace Daisi.Broski.Docs;

/// <summary>
/// Escape helpers shared by every doc → HTML converter. Converters
/// hand user-controlled text (docx run text, xlsx cell values, PDF
/// string literals) through here before writing into the synthetic
/// HTML that gets re-parsed by <c>HtmlTreeBuilder</c>. Any gap here
/// is a cross-site-scripting vector the moment the extractor sees
/// the synthesized article.
/// </summary>
public static class HtmlWriter
{
    /// <summary>Escape text for use inside an HTML element's child
    /// content. Handles <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, and
    /// both quote characters (we quote both because the same helper
    /// is sometimes spliced into attribute contexts by mistake; being
    /// strict here removes an entire failure mode).</summary>
    public static string EscapeText(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new StringBuilder(input.Length + 16);
        AppendEscaped(sb, input);
        return sb.ToString();
    }

    /// <summary>Escape a value intended for a double-quoted HTML
    /// attribute. Callers are expected to emit
    /// <c>name="{EscapeAttr(value)}"</c>; single-quoted contexts are
    /// not supported.</summary>
    public static string EscapeAttr(string? input) => EscapeText(input);

    /// <summary>Append <paramref name="input"/> to
    /// <paramref name="sb"/> with HTML-meaningful characters escaped.
    /// Exposed so converters can stream runs of text into the shared
    /// builder without allocating an intermediate string.</summary>
    public static void AppendEscaped(StringBuilder sb, string? input)
    {
        if (string.IsNullOrEmpty(input)) return;
        foreach (char c in input)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
