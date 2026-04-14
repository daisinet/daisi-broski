using Daisi.Broski.Engine.Css;

namespace Daisi.Broski.Engine.Layout;

/// <summary>
/// Minimum-viable user-agent stylesheet — the small set of
/// "every browser ships these defaults" rules that real
/// pages assume are in place. Without these, a stylesheet
/// that says nothing leaves block elements at zero margin /
/// padding and inline elements indistinguishable from
/// blocks. The full CSS UA stylesheet is ~200 rules across
/// 50+ tags; we ship only the structural ones layout cares
/// about (display, font-size, margin) plus a couple of
/// font defaults so inheritance has something to flow.
///
/// <para>
/// Applied as the first stylesheet in
/// <see cref="LayoutTree.Build"/>; author rules cascade on
/// top per spec.
/// </para>
/// </summary>
internal static class UserAgentStyles
{
    private static Stylesheet? _cached;

    public static Stylesheet Stylesheet => _cached ??= CssParser.Parse(Css);

    private const string Css = @"
        html, body, div, section, article, aside, header, footer, nav, main,
        h1, h2, h3, h4, h5, h6, p, ol, ul, li, dl, dt, dd, figure, figcaption,
        hr, blockquote, pre,
        form, fieldset, legend, address {
            display: block;
        }
        /* Real CSS 2.1 §17 table display values. Matching
           LayoutTree.ParseDisplay — 'table' activates
           TableLayout; the internal roles (row-group / row /
           cell) are discovered by TableLayout walking the
           DOM structure, so they don't need their own
           BoxDisplay enum values. */
        table { display: table; border-spacing: 2px; }
        thead, tbody, tfoot { display: table-row-group; }
        tr { display: table-row; }
        td, th { display: table-cell; padding: 1px; vertical-align: inherit; }
        th { font-weight: bold; text-align: center; }
        caption { display: table-caption; text-align: center; }
        head, script, style, link, meta, title, noscript { display: none; }
        span, a, em, strong, b, i, u, s, small, big, sub, sup, code, kbd,
        samp, var, cite, q, abbr, acronym, label, button, input, select,
        textarea, time, mark, ruby, rt, rp, bdi, bdo, wbr { display: inline; }
        img, video, audio, canvas, picture, svg, iframe, embed, object,
        progress, meter { display: inline-block; }
        li { display: list-item; }

        html { font-size: 16px; line-height: 1.2; color: black; }
        body { margin: 8px; }
        h1 { font-size: 2em;     margin: 0.67em 0; font-weight: bold; }
        h2 { font-size: 1.5em;   margin: 0.83em 0; font-weight: bold; }
        h3 { font-size: 1.17em;  margin: 1em 0;    font-weight: bold; }
        h4 { font-size: 1em;     margin: 1.33em 0; font-weight: bold; }
        h5 { font-size: 0.83em;  margin: 1.67em 0; font-weight: bold; }
        h6 { font-size: 0.67em;  margin: 2.33em 0; font-weight: bold; }
        p, blockquote, figure, pre { margin: 1em 0; }
        ul, ol { margin: 1em 0; padding-left: 40px; }
        hr { margin: 0.5em auto; }

        a { color: #0066cc; text-decoration: underline; }
        b, strong { font-weight: bold; }
        i, em { font-style: italic; }
        small { font-size: smaller; }
        code, kbd, samp, pre { font-family: monospace; }
    ";
}
