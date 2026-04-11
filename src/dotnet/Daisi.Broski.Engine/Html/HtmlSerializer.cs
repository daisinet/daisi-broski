using System.Text;
using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Html;

/// <summary>
/// Serializes a <see cref="Node"/> subtree back to its
/// canonical HTML string form. The inverse of the phase-1
/// tokenizer + tree builder. Used by slice 3c-8 to
/// implement <c>element.innerHTML</c> / <c>outerHTML</c>
/// reads from script — the script-side equivalent of
/// "give me the HTML this element would have been
/// serialized from."
///
/// <para>
/// The output is a spec-pragmatic subset: escapes the
/// five critical characters (<c>&amp;</c>, <c>&lt;</c>,
/// <c>&gt;</c>, <c>&quot;</c>, and <c>'</c>) in the
/// places the spec requires, honors void elements
/// (<c>&lt;br&gt;</c> not <c>&lt;br&gt;&lt;/br&gt;</c>),
/// and preserves attribute insertion order (driven by the
/// <see cref="Element.Attributes"/> ordered list that
/// slice 1 established).
/// </para>
///
/// <para>
/// Deferred: the full WHATWG "HTML fragment serialization
/// algorithm" rules around template contents, foreign
/// (SVG / MathML) elements, and the CDATA-like
/// &lt;noscript&gt; / &lt;textarea&gt; special cases.
/// We cover the common HTML5 subset that scripts actually
/// read / write.
/// </para>
/// </summary>
public static class HtmlSerializer
{
    /// <summary>
    /// Serialize every child of <paramref name="node"/>
    /// concatenated together. This is the
    /// <c>innerHTML</c> shape: the children's HTML, not
    /// the node itself.
    /// </summary>
    public static string SerializeChildren(Node node)
    {
        var sb = new StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            SerializeNode(child, sb);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serialize <paramref name="node"/> including its
    /// own opening / closing tag. Matches the
    /// <c>outerHTML</c> shape.
    /// </summary>
    public static string SerializeNode(Node node)
    {
        var sb = new StringBuilder();
        SerializeNode(node, sb);
        return sb.ToString();
    }

    private static void SerializeNode(Node node, StringBuilder sb)
    {
        switch (node)
        {
            case Element el:
                SerializeElement(el, sb);
                break;
            case Text t:
                // Text nodes inside <script> and <style> are
                // raw — their content is not HTML-escaped so
                // JS source containing `a < b` survives a
                // round trip. Everything else gets escaped.
                if (node.ParentNode is Element parentEl &&
                    (parentEl.TagName is "script" or "style" or "noscript"))
                {
                    sb.Append(t.Data);
                }
                else
                {
                    AppendEscapedText(t.Data, sb);
                }
                break;
            case Comment c:
                sb.Append("<!--").Append(c.Data).Append("-->");
                break;
            case DocumentType dt:
                sb.Append("<!DOCTYPE ").Append(dt.Name).Append('>');
                break;
            // Document / DocumentFragment: just recurse.
            default:
                foreach (var child in node.ChildNodes)
                {
                    SerializeNode(child, sb);
                }
                break;
        }
    }

    private static void SerializeElement(Element el, StringBuilder sb)
    {
        sb.Append('<').Append(el.TagName);
        foreach (var attr in el.Attributes)
        {
            sb.Append(' ').Append(attr.Key).Append("=\"");
            AppendEscapedAttributeValue(attr.Value, sb);
            sb.Append('"');
        }
        sb.Append('>');
        if (IsVoidElement(el.TagName))
        {
            return; // no closing tag, no children
        }
        foreach (var child in el.ChildNodes)
        {
            SerializeNode(child, sb);
        }
        sb.Append("</").Append(el.TagName).Append('>');
    }

    /// <summary>
    /// WHATWG-conformant HTML text-content escape: ampersand,
    /// less-than, greater-than, and the two no-break space
    /// characters. Quote characters do NOT need escaping in
    /// text content — only in attribute values.
    /// </summary>
    private static void AppendEscapedText(string s, StringBuilder sb)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\u00A0': sb.Append("&nbsp;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    /// <summary>
    /// WHATWG-conformant attribute-value escape: ampersand,
    /// double-quote (we emit double-quoted attributes), and
    /// NBSP. The spec does not require escaping of
    /// <c>&lt;</c> / <c>&gt;</c> inside a double-quoted
    /// attribute value — those are unambiguous inside the
    /// quotes.
    /// </summary>
    private static void AppendEscapedAttributeValue(string s, StringBuilder sb)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\u00A0': sb.Append("&nbsp;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    /// <summary>
    /// Void-element set matching the HTML5 spec. Must stay
    /// in sync with the insertion-time check in
    /// <see cref="HtmlTreeBuilder"/>; duplicated here
    /// because the serializer lives in the same namespace
    /// but doesn't need the rest of the tree builder's
    /// state.
    /// </summary>
    private static bool IsVoidElement(string name) => name switch
    {
        "area" or "base" or "br" or "col" or "embed" or "hr"
            or "img" or "input" or "link" or "meta" or "param"
            or "source" or "track" or "wbr" => true,
        _ => false,
    };
}
