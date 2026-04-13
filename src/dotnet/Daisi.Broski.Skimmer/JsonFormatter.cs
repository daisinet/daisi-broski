using System.Globalization;
using System.Text;

namespace Daisi.Broski.Skimmer;

/// <summary>
/// Hand-rolled JSON serializer for <see cref="ArticleContent"/>. We
/// don't pull <c>System.Text.Json</c> here because the rest of the repo
/// is "zero third-party dependencies" and the article shape is tiny —
/// half a dozen scalar fields plus two arrays of two-string records.
///
/// <para>
/// The output is pretty-printed with 2-space indentation and uses
/// JSON's spec-strict escape set (<c>\\</c>, <c>\"</c>, <c>\b</c>,
/// <c>\f</c>, <c>\n</c>, <c>\r</c>, <c>\t</c>, plus <c>\uXXXX</c> for
/// every control character). Field ordering is stable so diffs across
/// runs are meaningful.
/// </para>
/// </summary>
public static class JsonFormatter
{
    public static string Format(ArticleContent article)
    {
        ArgumentNullException.ThrowIfNull(article);

        var sb = new StringBuilder();
        sb.Append("{\n");
        WriteString(sb, "  ", "url", article.Url.AbsoluteUri); sb.Append(",\n");
        WriteString(sb, "  ", "title", article.Title); sb.Append(",\n");
        WriteString(sb, "  ", "byline", article.Byline); sb.Append(",\n");
        WriteString(sb, "  ", "publishedAt", article.PublishedAt); sb.Append(",\n");
        WriteString(sb, "  ", "lang", article.Lang); sb.Append(",\n");
        WriteString(sb, "  ", "siteName", article.SiteName); sb.Append(",\n");
        WriteString(sb, "  ", "description", article.Description); sb.Append(",\n");
        WriteString(sb, "  ", "heroImage", article.HeroImage); sb.Append(",\n");
        WriteNumber(sb, "  ", "wordCount", article.WordCount); sb.Append(",\n");
        WriteString(sb, "  ", "plainText", article.PlainText); sb.Append(",\n");

        // images: [{ "src": "...", "alt": "..." }, ...]
        sb.Append("  \"images\": [");
        if (article.Images.Count == 0)
        {
            sb.Append("],\n");
        }
        else
        {
            sb.Append('\n');
            for (int i = 0; i < article.Images.Count; i++)
            {
                var img = article.Images[i];
                sb.Append("    { ");
                sb.Append("\"src\": "); EncodeString(sb, img.Src);
                sb.Append(", \"alt\": "); EncodeString(sb, img.Alt);
                sb.Append(" }");
                if (i < article.Images.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ],\n");
        }

        // links: [{ "href": "...", "text": "..." }, ...]
        sb.Append("  \"links\": [");
        if (article.Links.Count == 0)
        {
            sb.Append("],\n");
        }
        else
        {
            sb.Append('\n');
            for (int i = 0; i < article.Links.Count; i++)
            {
                var link = article.Links[i];
                sb.Append("    { ");
                sb.Append("\"href\": "); EncodeString(sb, link.Href);
                sb.Append(", \"text\": "); EncodeString(sb, link.Text);
                sb.Append(" }");
                if (i < article.Links.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ],\n");
        }

        // navLinks: [{ "href": "...", "text": "..." }, ...] —
        // collected from the page's <nav> elements (and
        // [role=navigation] containers) before the noise strip
        // removed the header / footer nav from the article body.
        sb.Append("  \"navLinks\": [");
        if (article.NavLinks.Count == 0)
        {
            sb.Append("]\n");
        }
        else
        {
            sb.Append('\n');
            for (int i = 0; i < article.NavLinks.Count; i++)
            {
                var link = article.NavLinks[i];
                sb.Append("    { ");
                sb.Append("\"href\": "); EncodeString(sb, link.Href);
                sb.Append(", \"text\": "); EncodeString(sb, link.Text);
                sb.Append(" }");
                if (i < article.NavLinks.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ]\n");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void WriteString(StringBuilder sb, string indent, string key, string? value)
    {
        sb.Append(indent).Append('"').Append(key).Append("\": ");
        if (value is null) sb.Append("null");
        else EncodeString(sb, value);
    }

    private static void WriteNumber(StringBuilder sb, string indent, string key, int value)
    {
        sb.Append(indent).Append('"').Append(key).Append("\": ");
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Encode <paramref name="s"/> as a JSON string literal.
    /// Handles the spec-required escape set; non-ASCII printable chars
    /// pass through as UTF-16 code units (consumers that need
    /// 7-bit-clean output can post-process).</summary>
    private static void EncodeString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
