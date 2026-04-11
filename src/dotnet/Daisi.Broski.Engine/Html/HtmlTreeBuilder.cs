using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Html;

/// <summary>
/// HTML5 tree builder — consumes a stream of <see cref="HtmlToken"/>s
/// from <see cref="Tokenizer"/> and produces a <see cref="Document"/>.
///
/// Implements a phase-1 subset of the WHATWG insertion-mode state
/// machine (§13.2.6). The "main spine" of modes is supported:
/// Initial → BeforeHtml → BeforeHead → InHead → AfterHead → InBody →
/// AfterBody → AfterAfterBody. This handles any page whose structure
/// doesn't depend on table, form, template, frameset, or foreign-content
/// modes.
///
/// Deliberate simplifications:
///
/// - No table-specific insertion modes. Table tags (<c>table</c>, <c>tr</c>,
///   <c>td</c>, etc.) are inserted as regular elements inside <c>&lt;body&gt;</c>.
///   This gives a wrong tree structure for malformed tables but lets
///   well-formed tables parse correctly.
/// - No form-element association.
/// - No template elements.
/// - No foreign content (SVG / MathML) — their tags parse as regular
///   HTML elements.
/// - No frameset modes.
/// - Quirks-mode detection is ignored. The document is always assumed
///   to be no-quirks.
/// - The full adoption agency algorithm is not implemented. Misnested
///   end tags are handled with a simplified "pop until matching name"
///   walk which gets the common cases right (see <see cref="HandleEndTagInBody"/>).
///   Patterns like <c>&lt;b&gt;&lt;i&gt;&lt;/b&gt;&lt;/i&gt;</c> produce
///   a different tree than what Chrome does; these are rare in real
///   pages.
/// - Implicit closing of <c>&lt;p&gt;</c> when a new <c>&lt;p&gt;</c>
///   or a block-level element opens — supported, because it's common.
///   Implicit closes for list items and table rows are supported for
///   the same reason.
///
/// The tree builder is NOT thread-safe. One instance per document.
/// </summary>
public sealed class HtmlTreeBuilder
{
    private readonly Document _document = new();
    private readonly Tokenizer _tokenizer;
    private readonly List<Element> _openElements = [];
    private InsertionMode _mode = InsertionMode.Initial;
    private InsertionMode _returnToMode = InsertionMode.Initial;
    private Element? _headElement;

    public HtmlTreeBuilder(string input)
    {
        _tokenizer = new Tokenizer(input);
    }

    /// <summary>
    /// Convenience: parse an HTML string into a <see cref="Document"/>
    /// in one call.
    /// </summary>
    public static Document Parse(string input) => new HtmlTreeBuilder(input).Parse();

    /// <summary>
    /// Drive the tokenizer to EOF, routing each token into the current
    /// insertion mode. Returns the fully-built <see cref="Document"/>.
    /// </summary>
    public Document Parse()
    {
        while (true)
        {
            var token = _tokenizer.Next();
            if (token is EndOfFileToken)
            {
                HandleEof();
                return _document;
            }
            Dispatch(token);
        }
    }

    // -------------------------------------------------------------------
    // Dispatcher
    // -------------------------------------------------------------------

    private void Dispatch(HtmlToken token)
    {
        switch (_mode)
        {
            case InsertionMode.Initial:
                InitialMode(token);
                break;
            case InsertionMode.BeforeHtml:
                BeforeHtmlMode(token);
                break;
            case InsertionMode.BeforeHead:
                BeforeHeadMode(token);
                break;
            case InsertionMode.InHead:
                InHeadMode(token);
                break;
            case InsertionMode.AfterHead:
                AfterHeadMode(token);
                break;
            case InsertionMode.InBody:
                InBodyMode(token);
                break;
            case InsertionMode.AfterBody:
                AfterBodyMode(token);
                break;
            case InsertionMode.AfterAfterBody:
                AfterAfterBodyMode(token);
                break;
            case InsertionMode.Text:
                TextMode(token);
                break;
        }
    }

    private void HandleEof()
    {
        // Flush any pending mode-specific end-of-input cleanup here.
        // Currently a no-op — the open elements stack can be left
        // as-is; DOM consumers don't care about unclosed elements.
    }

    // -------------------------------------------------------------------
    // Insertion modes
    // -------------------------------------------------------------------

    private void InitialMode(HtmlToken token)
    {
        if (token is DoctypeToken dt)
        {
            var doctype = _document.CreateDocumentType(dt.Name ?? "html");
            _document.AppendChild(doctype);
            _mode = InsertionMode.BeforeHtml;
            return;
        }
        if (IsIgnorableWhitespace(token)) return;

        // Anything else: transition to BeforeHtml and reprocess.
        _mode = InsertionMode.BeforeHtml;
        BeforeHtmlMode(token);
    }

    private void BeforeHtmlMode(HtmlToken token)
    {
        if (token is StartTagToken st && st.Name == "html")
        {
            var html = _document.CreateElement("html");
            CopyAttributes(st, html);
            _document.AppendChild(html);
            _openElements.Add(html);
            _mode = InsertionMode.BeforeHead;
            return;
        }
        if (token is CommentToken c)
        {
            _document.AppendChild(_document.CreateComment(c.Data));
            return;
        }
        if (IsIgnorableWhitespace(token)) return;

        // Synthesize <html> and reprocess.
        var implicitHtml = _document.CreateElement("html");
        _document.AppendChild(implicitHtml);
        _openElements.Add(implicitHtml);
        _mode = InsertionMode.BeforeHead;
        BeforeHeadMode(token);
    }

    private void BeforeHeadMode(HtmlToken token)
    {
        if (token is StartTagToken st && st.Name == "head")
        {
            var head = _document.CreateElement("head");
            CopyAttributes(st, head);
            CurrentOpenElement.AppendChild(head);
            _openElements.Add(head);
            _headElement = head;
            _mode = InsertionMode.InHead;
            return;
        }
        if (token is CommentToken c)
        {
            CurrentOpenElement.AppendChild(_document.CreateComment(c.Data));
            return;
        }
        if (IsIgnorableWhitespace(token)) return;

        // Synthesize <head> and reprocess.
        var implicitHead = _document.CreateElement("head");
        CurrentOpenElement.AppendChild(implicitHead);
        _openElements.Add(implicitHead);
        _headElement = implicitHead;
        _mode = InsertionMode.InHead;
        InHeadMode(token);
    }

    private void InHeadMode(HtmlToken token)
    {
        if (token is CharacterToken ct && IsPureWhitespace(ct.Data))
        {
            InsertText(ct.Data);
            return;
        }
        if (token is CommentToken c)
        {
            CurrentOpenElement.AppendChild(_document.CreateComment(c.Data));
            return;
        }
        if (token is StartTagToken st)
        {
            switch (st.Name)
            {
                case "meta" or "link" or "base" or "basefont" or "bgsound":
                    // Void element — insert and don't push.
                    InsertVoidElement(st);
                    return;
                case "title":
                    // RCDATA — switch to the Text insertion mode so the
                    // upcoming character tokens land inside the <title>
                    // instead of being treated as "non-whitespace in head"
                    // and flushing us out of head.
                    InsertTextModeElement(st);
                    return;
                case "style" or "script" or "noscript" or "noframes":
                    InsertTextModeElement(st);
                    return;
                case "head":
                    // Spurious nested <head>. Ignore.
                    return;
                default:
                    // Anything else — close <head>, switch to AfterHead, reprocess.
                    PopElement("head");
                    _mode = InsertionMode.AfterHead;
                    AfterHeadMode(token);
                    return;
            }
        }
        if (token is EndTagToken et)
        {
            if (et.Name == "head")
            {
                PopElement("head");
                _mode = InsertionMode.AfterHead;
                return;
            }
            if (et.Name is "body" or "html" or "br")
            {
                PopElement("head");
                _mode = InsertionMode.AfterHead;
                AfterHeadMode(token);
                return;
            }
            // Other end tags in head are parse errors; ignore for phase 1.
            return;
        }
        if (token is CharacterToken)
        {
            // Non-whitespace characters in head → close head, transition, reprocess.
            PopElement("head");
            _mode = InsertionMode.AfterHead;
            AfterHeadMode(token);
            return;
        }
    }

    private void AfterHeadMode(HtmlToken token)
    {
        if (token is CharacterToken ct && IsPureWhitespace(ct.Data))
        {
            InsertText(ct.Data);
            return;
        }
        if (token is CommentToken c)
        {
            CurrentOpenElement.AppendChild(_document.CreateComment(c.Data));
            return;
        }
        if (token is StartTagToken st)
        {
            if (st.Name == "body")
            {
                var body = _document.CreateElement("body");
                CopyAttributes(st, body);
                CurrentOpenElement.AppendChild(body);
                _openElements.Add(body);
                _mode = InsertionMode.InBody;
                return;
            }
            if (st.Name == "html")
            {
                // Spurious — ignore.
                return;
            }

            // Anything else: synthesize <body> and reprocess in body.
            var implicitBody = _document.CreateElement("body");
            CurrentOpenElement.AppendChild(implicitBody);
            _openElements.Add(implicitBody);
            _mode = InsertionMode.InBody;
            InBodyMode(token);
            return;
        }
        if (token is EndTagToken)
        {
            // Synthesize <body> and reprocess.
            var implicitBody = _document.CreateElement("body");
            CurrentOpenElement.AppendChild(implicitBody);
            _openElements.Add(implicitBody);
            _mode = InsertionMode.InBody;
            InBodyMode(token);
            return;
        }
        if (token is CharacterToken)
        {
            // Non-whitespace → synthesize <body> and reprocess.
            var implicitBody = _document.CreateElement("body");
            CurrentOpenElement.AppendChild(implicitBody);
            _openElements.Add(implicitBody);
            _mode = InsertionMode.InBody;
            InBodyMode(token);
            return;
        }
    }

    private void InBodyMode(HtmlToken token)
    {
        switch (token)
        {
            case CharacterToken ct:
                InsertText(ct.Data);
                return;

            case CommentToken c:
                CurrentOpenElement.AppendChild(_document.CreateComment(c.Data));
                return;

            case StartTagToken st:
                HandleStartTagInBody(st);
                return;

            case EndTagToken et:
                HandleEndTagInBody(et);
                return;
        }
    }

    private void AfterBodyMode(HtmlToken token)
    {
        if (token is CharacterToken ct && IsPureWhitespace(ct.Data))
        {
            // Whitespace is processed as if still in body.
            InsertText(ct.Data);
            return;
        }
        if (token is CommentToken c)
        {
            // Comments after </body> attach to the <html> element.
            var html = _openElements[0];
            html.AppendChild(_document.CreateComment(c.Data));
            return;
        }
        if (token is EndTagToken et && et.Name == "html")
        {
            _mode = InsertionMode.AfterAfterBody;
            return;
        }

        // Anything else: reprocess in body.
        _mode = InsertionMode.InBody;
        InBodyMode(token);
    }

    private void AfterAfterBodyMode(HtmlToken token)
    {
        if (token is CommentToken c)
        {
            _document.AppendChild(_document.CreateComment(c.Data));
            return;
        }
        if (token is CharacterToken ct && IsPureWhitespace(ct.Data))
        {
            InsertText(ct.Data);
            return;
        }

        // Anything else: reprocess in body.
        _mode = InsertionMode.InBody;
        InBodyMode(token);
    }

    /// <summary>
    /// Text insertion mode — active while the tree builder is inside
    /// a <c>&lt;script&gt;</c>, <c>&lt;style&gt;</c>, <c>&lt;title&gt;</c>,
    /// <c>&lt;textarea&gt;</c>, or similar raw-text-like element. The
    /// tokenizer has already switched into its RAWTEXT/RCDATA/ScriptData
    /// state so the character tokens arriving here are element body text,
    /// not markup.
    ///
    /// When the matching end tag arrives, we pop the element and return
    /// to <see cref="_returnToMode"/> — the mode we were in when the
    /// text-mode element opened.
    /// </summary>
    private void TextMode(HtmlToken token)
    {
        switch (token)
        {
            case CharacterToken ct:
                InsertText(ct.Data);
                return;

            case EndTagToken:
                // The tokenizer only emits end tags that match the raw-text
                // element we opened, so we don't need to compare names —
                // just pop and return.
                if (_openElements.Count > 0)
                {
                    _openElements.RemoveAt(_openElements.Count - 1);
                }
                _mode = _returnToMode;
                return;

            case CommentToken c:
                CurrentOpenElement.AppendChild(_document.CreateComment(c.Data));
                return;
        }
    }

    // -------------------------------------------------------------------
    // InBody helpers — where the real tag-level logic lives
    // -------------------------------------------------------------------

    private void HandleStartTagInBody(StartTagToken st)
    {
        var name = st.Name;

        // Void elements: insert and don't push.
        if (IsVoidElement(name))
        {
            InsertVoidElement(st);
            return;
        }

        // Raw-text-like elements: insert, switch to Text mode so the
        // body text lands inside the element. The tokenizer has already
        // entered the corresponding raw-text state when it emitted the
        // start tag.
        if (name is "script" or "style" or "noscript" or "noframes"
                or "title" or "textarea" or "xmp" or "iframe" or "noembed")
        {
            InsertTextModeElement(st);
            return;
        }

        // Implicit <p> close on certain block-level start tags.
        if (CausesImplicitPClose(name) && HasElementInScope("p"))
        {
            PopElement("p");
        }

        // Implicit close of same-named list-item / table-row elements.
        if (name is "li" && HasElementInScope("li"))
            PopElement("li");
        else if ((name is "dd" or "dt") && (HasElementInScope("dd") || HasElementInScope("dt")))
            PopUntilOneOf("dd", "dt");
        else if (name is "tr" && HasElementInScope("tr"))
            PopElement("tr");
        else if ((name is "td" or "th") && (HasElementInScope("td") || HasElementInScope("th")))
            PopUntilOneOf("td", "th");
        else if (name is "option" && HasElementInScope("option"))
            PopElement("option");

        InsertElement(st);
    }

    private void HandleEndTagInBody(EndTagToken et)
    {
        var name = et.Name;

        // Simple case: top of stack matches — pop it.
        if (_openElements.Count > 0 && _openElements[^1].TagName == name)
        {
            _openElements.RemoveAt(_openElements.Count - 1);

            // </body> and </html> transition modes.
            if (name == "body") _mode = InsertionMode.AfterBody;
            else if (name == "html") _mode = InsertionMode.AfterAfterBody;
            return;
        }

        // Walk the stack looking for a matching element to pop to.
        for (int i = _openElements.Count - 1; i >= 0; i--)
        {
            if (_openElements[i].TagName == name)
            {
                // Pop everything above and including the match.
                _openElements.RemoveRange(i, _openElements.Count - i);
                return;
            }
        }

        // No match found — parse error, ignore the end tag.
    }

    // -------------------------------------------------------------------
    // Insertion helpers
    // -------------------------------------------------------------------

    private Element CurrentOpenElement =>
        _openElements.Count > 0
            ? _openElements[^1]
            : throw new InvalidOperationException("No open elements (tree builder misuse)");

    private void InsertElement(StartTagToken st)
    {
        var el = _document.CreateElement(st.Name);
        CopyAttributes(st, el);
        CurrentOpenElement.AppendChild(el);
        _openElements.Add(el);
    }

    /// <summary>
    /// Insert an element and switch to <see cref="InsertionMode.Text"/>
    /// so the next batch of character data flows into the element
    /// instead of being processed as markup by the current mode. Used
    /// for <c>&lt;script&gt;</c>, <c>&lt;style&gt;</c>, <c>&lt;title&gt;</c>,
    /// <c>&lt;textarea&gt;</c>, and other raw-text-like elements.
    /// </summary>
    private void InsertTextModeElement(StartTagToken st)
    {
        InsertElement(st);
        _returnToMode = _mode;
        _mode = InsertionMode.Text;
    }

    private void InsertVoidElement(StartTagToken st)
    {
        var el = _document.CreateElement(st.Name);
        CopyAttributes(st, el);
        CurrentOpenElement.AppendChild(el);
        // Don't push — void elements have no content.
    }

    /// <summary>
    /// Insert character data into the current open element. If the
    /// last child is already a <see cref="Text"/> node, append to it
    /// instead of creating a new one — matches browser behavior and
    /// keeps the DOM tree compact.
    /// </summary>
    private void InsertText(string data)
    {
        if (data.Length == 0) return;

        var parent = CurrentOpenElement;
        if (parent.LastChild is Text t)
        {
            t.AppendData(data);
        }
        else
        {
            parent.AppendChild(_document.CreateTextNode(data));
        }
    }

    private static void CopyAttributes(StartTagToken st, Element el)
    {
        foreach (var a in st.Attributes)
        {
            el.SetAttribute(a.Name, a.Value);
        }
    }

    // -------------------------------------------------------------------
    // Stack operations
    // -------------------------------------------------------------------

    private bool HasElementInScope(string tagName)
    {
        // Simplified "in scope" — walks the stack from the top until it
        // finds the named element or hits a document/html boundary.
        // The full spec distinguishes several scope flavors (button,
        // list-item, table, select) which we do not implement yet.
        for (int i = _openElements.Count - 1; i >= 0; i--)
        {
            if (_openElements[i].TagName == tagName) return true;
        }
        return false;
    }

    private void PopElement(string tagName)
    {
        for (int i = _openElements.Count - 1; i >= 0; i--)
        {
            if (_openElements[i].TagName == tagName)
            {
                _openElements.RemoveRange(i, _openElements.Count - i);
                return;
            }
        }
    }

    private void PopUntilOneOf(params string[] tagNames)
    {
        for (int i = _openElements.Count - 1; i >= 0; i--)
        {
            var name = _openElements[i].TagName;
            foreach (var target in tagNames)
            {
                if (name == target)
                {
                    _openElements.RemoveRange(i, _openElements.Count - i);
                    return;
                }
            }
        }
    }

    // -------------------------------------------------------------------
    // Element taxonomy
    // -------------------------------------------------------------------

    private static bool IsVoidElement(string name) => name switch
    {
        "area" or "base" or "br" or "col" or "embed" or "hr"
            or "img" or "input" or "link" or "meta" or "param"
            or "source" or "track" or "wbr" => true,
        _ => false,
    };

    private static bool CausesImplicitPClose(string name) => name switch
    {
        "address" or "article" or "aside" or "blockquote" or "details"
            or "div" or "dl" or "fieldset" or "figcaption" or "figure"
            or "footer" or "form" or "h1" or "h2" or "h3" or "h4"
            or "h5" or "h6" or "header" or "hgroup" or "hr" or "main"
            or "menu" or "nav" or "ol" or "p" or "pre" or "section"
            or "table" or "ul" => true,
        _ => false,
    };

    private static bool IsPureWhitespace(string s)
    {
        foreach (var c in s)
        {
            if (c is not (' ' or '\t' or '\n' or '\r' or '\f')) return false;
        }
        return true;
    }

    private static bool IsIgnorableWhitespace(HtmlToken t) =>
        t is CharacterToken ct && IsPureWhitespace(ct.Data);

    // -------------------------------------------------------------------
    // Mode enum
    // -------------------------------------------------------------------

    private enum InsertionMode
    {
        Initial,
        BeforeHtml,
        BeforeHead,
        InHead,
        AfterHead,
        InBody,
        Text,
        AfterBody,
        AfterAfterBody,
    }
}
