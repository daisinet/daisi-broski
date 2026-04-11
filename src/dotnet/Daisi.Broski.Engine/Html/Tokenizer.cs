using System.Text;

namespace Daisi.Broski.Engine.Html;

/// <summary>
/// HTML5 tokenizer following the WHATWG HTML standard §13.2.5 state
/// machine. This is a pragmatic phase-1 subset — enough to parse the
/// vast majority of real documents, but not yet fully spec-compliant.
///
/// Supported states:
///   - Data, tag open, end tag open, tag name
///   - Before/after/attribute name, before/in attribute value
///     (double-quoted, single-quoted, unquoted), after attribute value
///   - Self-closing start tag
///   - Markup declaration open → comment start, comment, comment end
///   - DOCTYPE name
///
/// Deliberately deferred (see roadmap phase 1 follow-ups):
///   - Character references (&amp;amp; etc.) — '&amp;' currently flows
///     through as a literal character. The tokenizer docs say the full
///     character-reference state will land before the tree builder.
///   - RAWTEXT / RCDATA / script data states — <c>&lt;script&gt;</c>,
///     <c>&lt;style&gt;</c>, <c>&lt;title&gt;</c>, <c>&lt;textarea&gt;</c>
///     body content is not yet given the special tokenization they need.
///   - CDATA sections.
///   - Foreign-content (SVG/MathML) handling.
///   - DOCTYPE public/system identifiers.
///
/// The API is <c>Next()</c> → a single <see cref="HtmlToken"/>, repeated
/// until <see cref="EndOfFileToken.Instance"/> is returned. Consecutive
/// data-state characters are batched into one <see cref="CharacterToken"/>
/// so that callers do not pay for an allocation per character.
/// </summary>
public sealed class Tokenizer
{
    private readonly string _input;
    private int _pos;

    // Current token being assembled.
    private readonly StringBuilder _dataBuffer = new();
    private readonly StringBuilder _nameBuffer = new();
    private readonly StringBuilder _commentBuffer = new();
    private readonly StringBuilder _attrNameBuffer = new();
    private readonly StringBuilder _attrValueBuffer = new();
    private readonly List<HtmlAttribute> _currentAttrs = [];
    private bool _currentIsEndTag;
    private bool _currentSelfClosing;

    // Pending token(s) to emit. The state machine can emit more than
    // one token in a single step (e.g. flushing buffered character data
    // before emitting a tag that just finished parsing).
    private HtmlToken? _pending;
    private HtmlToken? _pending2;
    private bool _eofEmitted;

    private State _state = State.Data;

    public Tokenizer(string input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <summary>
    /// Advance the state machine until one token is produced. Callers
    /// should invoke this in a loop until <see cref="EndOfFileToken"/>
    /// is returned.
    /// </summary>
    public HtmlToken Next()
    {
        if (_pending is not null)
        {
            var p = _pending;
            _pending = _pending2;
            _pending2 = null;
            return p;
        }

        while (true)
        {
            if (_pos >= _input.Length)
            {
                // Flush any buffered character data before EOF.
                if (_dataBuffer.Length > 0)
                {
                    return FlushCharacterData();
                }

                if (_eofEmitted) return EndOfFileToken.Instance;
                _eofEmitted = true;
                return EndOfFileToken.Instance;
            }

            char c = _input[_pos];

            switch (_state)
            {
                case State.Data:
                    HandleData(c);
                    break;
                case State.TagOpen:
                    HandleTagOpen(c);
                    break;
                case State.EndTagOpen:
                    HandleEndTagOpen(c);
                    break;
                case State.TagName:
                    HandleTagName(c);
                    break;
                case State.SelfClosingStartTag:
                    HandleSelfClosingStartTag(c);
                    break;
                case State.BeforeAttributeName:
                    HandleBeforeAttributeName(c);
                    break;
                case State.AttributeName:
                    HandleAttributeName(c);
                    break;
                case State.AfterAttributeName:
                    HandleAfterAttributeName(c);
                    break;
                case State.BeforeAttributeValue:
                    HandleBeforeAttributeValue(c);
                    break;
                case State.AttributeValueDoubleQuoted:
                    HandleAttributeValueDoubleQuoted(c);
                    break;
                case State.AttributeValueSingleQuoted:
                    HandleAttributeValueSingleQuoted(c);
                    break;
                case State.AttributeValueUnquoted:
                    HandleAttributeValueUnquoted(c);
                    break;
                case State.AfterAttributeValueQuoted:
                    HandleAfterAttributeValueQuoted(c);
                    break;
                case State.MarkupDeclarationOpen:
                    HandleMarkupDeclarationOpen();
                    continue; // HandleMarkupDeclarationOpen advances _pos itself
                case State.CommentStart:
                    HandleCommentStart(c);
                    break;
                case State.Comment:
                    HandleComment(c);
                    break;
                case State.CommentEndDash:
                    HandleCommentEndDash(c);
                    break;
                case State.CommentEnd:
                    HandleCommentEnd(c);
                    break;
                case State.Doctype:
                    HandleDoctype(c);
                    break;
                case State.BeforeDoctypeName:
                    HandleBeforeDoctypeName(c);
                    break;
                case State.DoctypeName:
                    HandleDoctypeName(c);
                    break;
                case State.AfterDoctypeName:
                    HandleAfterDoctypeName(c);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled tokenizer state: {_state}");
            }

            // A handler may have parked a token on _pending. Return it.
            if (_pending is not null)
            {
                var p = _pending;
                _pending = _pending2;
                _pending2 = null;
                return p;
            }
        }
    }

    // -------------------------------------------------------------------
    // State handlers. Each consumes exactly one character unless noted.
    // -------------------------------------------------------------------

    private void HandleData(char c)
    {
        if (c == '<')
        {
            _pos++;
            _state = State.TagOpen;
            return;
        }
        if (c == '&')
        {
            _dataBuffer.Append(ConsumeCharacterReference(inAttributeValue: false));
            return;
        }

        _dataBuffer.Append(c);
        _pos++;
    }

    private void HandleTagOpen(char c)
    {
        if (c == '!')
        {
            _pos++;
            _state = State.MarkupDeclarationOpen;
            return;
        }
        if (c == '/')
        {
            _pos++;
            _state = State.EndTagOpen;
            return;
        }
        if (IsAsciiAlpha(c))
        {
            StartNewTag(isEndTag: false);
            _nameBuffer.Append(char.ToLowerInvariant(c));
            _pos++;
            _state = State.TagName;
            return;
        }

        // Parse error: the '<' was not the start of a tag.
        // Re-emit as literal data and re-process the current char.
        _dataBuffer.Append('<');
        _state = State.Data;
    }

    private void HandleEndTagOpen(char c)
    {
        if (IsAsciiAlpha(c))
        {
            StartNewTag(isEndTag: true);
            _nameBuffer.Append(char.ToLowerInvariant(c));
            _pos++;
            _state = State.TagName;
            return;
        }
        if (c == '>')
        {
            // '</>' — ignore per spec recovery.
            _pos++;
            _state = State.Data;
            return;
        }

        // '</' followed by something else → treat as bogus comment (simplified: ignore content until '>').
        _pos++;
        _state = State.Comment;
        _commentBuffer.Clear();
    }

    private void HandleTagName(char c)
    {
        if (IsWhitespace(c))
        {
            _pos++;
            _state = State.BeforeAttributeName;
            return;
        }
        if (c == '/')
        {
            _pos++;
            _state = State.SelfClosingStartTag;
            return;
        }
        if (c == '>')
        {
            _pos++;
            EmitCurrentTag();
            _state = State.Data;
            return;
        }

        _nameBuffer.Append(char.ToLowerInvariant(c));
        _pos++;
    }

    private void HandleSelfClosingStartTag(char c)
    {
        if (c == '>')
        {
            _currentSelfClosing = true;
            _pos++;
            EmitCurrentTag();
            _state = State.Data;
            return;
        }

        // Treat as before-attribute-name (parse-error recovery).
        _state = State.BeforeAttributeName;
    }

    private void HandleBeforeAttributeName(char c)
    {
        if (IsWhitespace(c))
        {
            _pos++;
            return;
        }
        if (c == '/' || c == '>')
        {
            _state = State.AfterAttributeName;
            return;
        }

        _attrNameBuffer.Clear();
        _attrValueBuffer.Clear();
        _state = State.AttributeName;
    }

    private void HandleAttributeName(char c)
    {
        if (IsWhitespace(c) || c == '/' || c == '>')
        {
            _state = State.AfterAttributeName;
            return;
        }
        if (c == '=')
        {
            _pos++;
            _state = State.BeforeAttributeValue;
            return;
        }

        _attrNameBuffer.Append(char.ToLowerInvariant(c));
        _pos++;
    }

    private void HandleAfterAttributeName(char c)
    {
        if (IsWhitespace(c))
        {
            _pos++;
            return;
        }
        if (c == '/')
        {
            PushAttribute(withEmptyValue: true);
            _pos++;
            _state = State.SelfClosingStartTag;
            return;
        }
        if (c == '=')
        {
            _pos++;
            _state = State.BeforeAttributeValue;
            return;
        }
        if (c == '>')
        {
            PushAttribute(withEmptyValue: true);
            _pos++;
            EmitCurrentTag();
            _state = State.Data;
            return;
        }

        // New attribute begins.
        PushAttribute(withEmptyValue: true);
        _attrNameBuffer.Clear();
        _attrValueBuffer.Clear();
        _state = State.AttributeName;
    }

    private void HandleBeforeAttributeValue(char c)
    {
        if (IsWhitespace(c))
        {
            _pos++;
            return;
        }
        if (c == '"')
        {
            _pos++;
            _state = State.AttributeValueDoubleQuoted;
            return;
        }
        if (c == '\'')
        {
            _pos++;
            _state = State.AttributeValueSingleQuoted;
            return;
        }
        if (c == '>')
        {
            PushAttribute(withEmptyValue: true);
            _pos++;
            EmitCurrentTag();
            _state = State.Data;
            return;
        }

        _state = State.AttributeValueUnquoted;
    }

    private void HandleAttributeValueDoubleQuoted(char c)
    {
        if (c == '"')
        {
            PushAttribute(withEmptyValue: false);
            _pos++;
            _state = State.AfterAttributeValueQuoted;
            return;
        }
        if (c == '&')
        {
            _attrValueBuffer.Append(ConsumeCharacterReference(inAttributeValue: true));
            return;
        }

        _attrValueBuffer.Append(c);
        _pos++;
    }

    private void HandleAttributeValueSingleQuoted(char c)
    {
        if (c == '\'')
        {
            PushAttribute(withEmptyValue: false);
            _pos++;
            _state = State.AfterAttributeValueQuoted;
            return;
        }
        if (c == '&')
        {
            _attrValueBuffer.Append(ConsumeCharacterReference(inAttributeValue: true));
            return;
        }

        _attrValueBuffer.Append(c);
        _pos++;
    }

    private void HandleAttributeValueUnquoted(char c)
    {
        if (IsWhitespace(c))
        {
            PushAttribute(withEmptyValue: false);
            _pos++;
            _state = State.BeforeAttributeName;
            return;
        }
        if (c == '>')
        {
            PushAttribute(withEmptyValue: false);
            _pos++;
            EmitCurrentTag();
            _state = State.Data;
            return;
        }
        if (c == '&')
        {
            _attrValueBuffer.Append(ConsumeCharacterReference(inAttributeValue: true));
            return;
        }

        _attrValueBuffer.Append(c);
        _pos++;
    }

    private void HandleAfterAttributeValueQuoted(char c)
    {
        if (IsWhitespace(c))
        {
            _pos++;
            _state = State.BeforeAttributeName;
            return;
        }
        if (c == '/')
        {
            _pos++;
            _state = State.SelfClosingStartTag;
            return;
        }
        if (c == '>')
        {
            _pos++;
            EmitCurrentTag();
            _state = State.Data;
            return;
        }

        // Parse error: treat as before-attribute-name.
        _state = State.BeforeAttributeName;
    }

    private void HandleMarkupDeclarationOpen()
    {
        // We're at the char after "<!". Peek ahead to pick the flavor.
        if (Peek("--"))
        {
            _pos += 2;
            _commentBuffer.Clear();
            _state = State.CommentStart;
            return;
        }
        if (PeekCaseInsensitive("DOCTYPE"))
        {
            _pos += 7;
            _state = State.Doctype;
            return;
        }

        // Unknown declaration (e.g. CDATA section in phase 2). Treat as bogus comment.
        _commentBuffer.Clear();
        _state = State.Comment;
    }

    private void HandleCommentStart(char c)
    {
        if (c == '-')
        {
            _pos++;
            _state = State.CommentEndDash;
            return;
        }
        if (c == '>')
        {
            // '<!-->' — emit empty comment.
            _pos++;
            _pending = new CommentToken { Data = "" };
            _state = State.Data;
            return;
        }

        _state = State.Comment;
    }

    private void HandleComment(char c)
    {
        if (c == '-')
        {
            _pos++;
            _state = State.CommentEndDash;
            return;
        }

        _commentBuffer.Append(c);
        _pos++;
    }

    private void HandleCommentEndDash(char c)
    {
        if (c == '-')
        {
            _pos++;
            _state = State.CommentEnd;
            return;
        }

        _commentBuffer.Append('-');
        _state = State.Comment;
    }

    private void HandleCommentEnd(char c)
    {
        if (c == '>')
        {
            _pos++;
            EmitBufferedCharsThenPending(new CommentToken { Data = _commentBuffer.ToString() });
            _commentBuffer.Clear();
            _state = State.Data;
            return;
        }
        if (c == '-')
        {
            _commentBuffer.Append('-');
            _pos++;
            return;
        }

        _commentBuffer.Append("--");
        _state = State.Comment;
    }

    private void HandleDoctype(char c)
    {
        if (IsWhitespace(c))
        {
            _pos++;
            _state = State.BeforeDoctypeName;
            return;
        }

        _state = State.BeforeDoctypeName;
    }

    private void HandleBeforeDoctypeName(char c)
    {
        if (IsWhitespace(c))
        {
            _pos++;
            return;
        }
        if (c == '>')
        {
            _pos++;
            EmitBufferedCharsThenPending(new DoctypeToken { Name = null });
            _state = State.Data;
            return;
        }

        _nameBuffer.Clear();
        _nameBuffer.Append(char.ToLowerInvariant(c));
        _pos++;
        _state = State.DoctypeName;
    }

    private void HandleDoctypeName(char c)
    {
        if (IsWhitespace(c))
        {
            _pos++;
            _state = State.AfterDoctypeName;
            return;
        }
        if (c == '>')
        {
            _pos++;
            EmitBufferedCharsThenPending(new DoctypeToken { Name = _nameBuffer.ToString() });
            _nameBuffer.Clear();
            _state = State.Data;
            return;
        }

        _nameBuffer.Append(char.ToLowerInvariant(c));
        _pos++;
    }

    private void HandleAfterDoctypeName(char c)
    {
        if (c == '>')
        {
            _pos++;
            EmitBufferedCharsThenPending(new DoctypeToken { Name = _nameBuffer.ToString() });
            _nameBuffer.Clear();
            _state = State.Data;
            return;
        }

        // Public/system identifiers not parsed in phase 1 — consume to '>'.
        _pos++;
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private void StartNewTag(bool isEndTag)
    {
        _nameBuffer.Clear();
        _attrNameBuffer.Clear();
        _attrValueBuffer.Clear();
        _currentAttrs.Clear();
        _currentIsEndTag = isEndTag;
        _currentSelfClosing = false;
    }

    private void PushAttribute(bool withEmptyValue)
    {
        if (_attrNameBuffer.Length == 0) return;

        var name = _attrNameBuffer.ToString();
        var value = withEmptyValue ? "" : _attrValueBuffer.ToString();

        // Duplicate attribute names: spec says keep the first, ignore later.
        foreach (var existing in _currentAttrs)
        {
            if (existing.Name == name)
            {
                _attrNameBuffer.Clear();
                _attrValueBuffer.Clear();
                return;
            }
        }

        _currentAttrs.Add(new HtmlAttribute(name, value));
        _attrNameBuffer.Clear();
        _attrValueBuffer.Clear();
    }

    private void EmitCurrentTag()
    {
        // Flush any pending attribute that was still being parsed.
        PushAttribute(withEmptyValue: _attrValueBuffer.Length == 0);

        var name = _nameBuffer.ToString();
        HtmlToken tag = _currentIsEndTag
            ? new EndTagToken { Name = name }
            : new StartTagToken
            {
                Name = name,
                Attributes = _currentAttrs.ToArray(),
                SelfClosing = _currentSelfClosing,
            };

        EmitBufferedCharsThenPending(tag);

        _nameBuffer.Clear();
        _currentAttrs.Clear();
        _currentSelfClosing = false;
    }

    /// <summary>
    /// If there is buffered character data, emit it first and park the
    /// real token in _pending so the next Next() call returns it.
    /// </summary>
    private void EmitBufferedCharsThenPending(HtmlToken token)
    {
        if (_dataBuffer.Length > 0)
        {
            _pending = FlushCharacterData();
            _pending2 = token;
        }
        else
        {
            _pending = token;
        }
    }

    private CharacterToken FlushCharacterData()
    {
        var token = new CharacterToken { Data = _dataBuffer.ToString() };
        _dataBuffer.Clear();
        return token;
    }

    /// <summary>
    /// Consume a character reference starting at <c>_pos</c> (which must
    /// point at <c>&amp;</c>). Returns the replacement string and advances
    /// <c>_pos</c> past every character consumed. If the reference is
    /// malformed or unknown, consumes only the <c>&amp;</c> and returns
    /// <c>"&amp;"</c>.
    ///
    /// Supports:
    ///   - Named references (<c>&amp;amp;</c>) — must terminate with <c>;</c>.
    ///     Legacy no-semicolon forms are not supported.
    ///   - Decimal numeric references (<c>&amp;#65;</c>).
    ///   - Hexadecimal numeric references (<c>&amp;#x41;</c> / <c>&amp;#X41;</c>).
    ///
    /// Numeric references apply the WHATWG legacy Windows-1252 fixup for
    /// code points 0x80–0x9F and substitute U+FFFD for surrogates and
    /// out-of-range code points. See <see cref="HtmlEntities.NumericReferenceToString"/>.
    ///
    /// The <paramref name="inAttributeValue"/> flag is accepted for future
    /// legacy-compat handling (the spec has special rules about named
    /// references followed by <c>=</c> or alphanumerics inside attribute
    /// values). Currently unused because we require the terminating
    /// semicolon unconditionally.
    /// </summary>
    private string ConsumeCharacterReference(bool inAttributeValue)
    {
        _ = inAttributeValue; // reserved for future use; see doc comment

        // _pos currently points at '&'. Save for rollback.
        int start = _pos;
        _pos++; // consume '&'

        if (_pos >= _input.Length)
        {
            return "&";
        }

        char next = _input[_pos];

        // Numeric reference: '&#' followed by decimal digits or 'x'/'X' + hex.
        if (next == '#')
        {
            _pos++;
            if (_pos >= _input.Length)
            {
                _pos = start + 1;
                return "&";
            }

            bool hex = false;
            if (_input[_pos] == 'x' || _input[_pos] == 'X')
            {
                hex = true;
                _pos++;
            }

            int numStart = _pos;
            int code = 0;
            bool overflow = false;

            while (_pos < _input.Length)
            {
                char d = _input[_pos];
                int digit = hex ? HexValue(d) : (d >= '0' && d <= '9' ? d - '0' : -1);
                if (digit < 0) break;

                // Clamp at something well above U+10FFFF so we don't overflow
                // int but still report "too big" via the U+FFFD fallback.
                if (!overflow)
                {
                    long probe = (long)code * (hex ? 16 : 10) + digit;
                    if (probe > 0x10FFFF) overflow = true;
                    else code = (int)probe;
                }

                _pos++;
            }

            // Empty digit run is a parse error → roll back.
            if (_pos == numStart)
            {
                _pos = start + 1;
                return "&";
            }

            // Optional terminating semicolon — consume if present.
            if (_pos < _input.Length && _input[_pos] == ';')
            {
                _pos++;
            }

            return HtmlEntities.NumericReferenceToString(overflow ? 0xFFFD : code);
        }

        // Named reference: '&' followed by ASCII alpha, then alphanumerics, then ';'.
        if (IsAsciiAlpha(next))
        {
            int nameStart = _pos;
            while (_pos < _input.Length && IsAsciiAlphaOrDigit(_input[_pos]))
            {
                _pos++;
            }

            // Must terminate with ';' to be recognized.
            if (_pos >= _input.Length || _input[_pos] != ';')
            {
                _pos = start + 1;
                return "&";
            }

            string name = _input[nameStart.._pos];
            _pos++; // consume ';'

            if (HtmlEntities.TryGetNamed(name, out var replacement))
            {
                return replacement;
            }

            // Unknown named reference: pass through '&' literally and replay
            // the name characters in the data stream.
            _pos = start + 1;
            return "&";
        }

        return "&";
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static bool IsAsciiAlphaOrDigit(char c) =>
        IsAsciiAlpha(c) || (c >= '0' && c <= '9');

    private bool Peek(string literal)
    {
        if (_pos + literal.Length > _input.Length) return false;
        for (int i = 0; i < literal.Length; i++)
        {
            if (_input[_pos + i] != literal[i]) return false;
        }
        return true;
    }

    private bool PeekCaseInsensitive(string literal)
    {
        if (_pos + literal.Length > _input.Length) return false;
        for (int i = 0; i < literal.Length; i++)
        {
            if (char.ToUpperInvariant(_input[_pos + i]) != char.ToUpperInvariant(literal[i]))
                return false;
        }
        return true;
    }

    private static bool IsAsciiAlpha(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsWhitespace(char c) =>
        c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

    private enum State
    {
        Data,
        TagOpen,
        EndTagOpen,
        TagName,
        SelfClosingStartTag,
        BeforeAttributeName,
        AttributeName,
        AfterAttributeName,
        BeforeAttributeValue,
        AttributeValueDoubleQuoted,
        AttributeValueSingleQuoted,
        AttributeValueUnquoted,
        AfterAttributeValueQuoted,
        MarkupDeclarationOpen,
        CommentStart,
        Comment,
        CommentEndDash,
        CommentEnd,
        Doctype,
        BeforeDoctypeName,
        DoctypeName,
        AfterDoctypeName,
    }
}
