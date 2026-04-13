using System.Globalization;
using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Lexical scanner for ECMAScript source. Produces <see cref="JsToken"/>s
/// via <see cref="Next"/> until <see cref="JsTokenKind.EndOfFile"/>.
///
/// Supported today (ES5 core + ES2015+ keyword recognition):
///
/// - All ES5 keywords and the future-reserved / ES2015+ keywords
///   (<c>let</c>, <c>const</c>, <c>class</c>, <c>extends</c>, ...).
/// - Identifiers over ASCII letters / digits / <c>_</c> / <c>$</c>.
///   Unicode escapes in identifiers are deferred until a real site
///   trips over them.
/// - Number literals:
///   - Decimal: <c>42</c>, <c>3.14</c>, <c>.5</c>, <c>5.</c>
///   - Scientific: <c>1e10</c>, <c>1e+10</c>, <c>1E-10</c>, <c>1.5e2</c>
///   - Hex: <c>0x1F</c>, <c>0X1F</c>
///   - Octal (legacy): not supported (strict mode forbids them anyway)
/// - String literals in single or double quotes with escapes:
///   <c>\n</c>, <c>\r</c>, <c>\t</c>, <c>\b</c>, <c>\f</c>, <c>\v</c>,
///   <c>\0</c>, <c>\'</c>, <c>\"</c>, <c>\\</c>, <c>\xXX</c>, <c>\uXXXX</c>,
///   and line continuations (<c>\</c> at end of line).
/// - Line comments (<c>//</c> to end of line) and block comments
///   (<c>/* ... */</c>). Skipped, not emitted.
/// - All ES5 punctuators, including the long ones
///   (<c>&gt;&gt;&gt;=</c>, <c>===</c>, etc.).
/// - <c>null</c>, <c>true</c>, <c>false</c> as their own token kinds
///   so the parser can handle them without going through the
///   identifier path.
///
/// Deliberately deferred:
///
/// - <b>Regex literals.</b> These can only be distinguished from the
///   division operator by parser context. The lexer will grow a
///   <c>ReLexCurrent(RegexMode)</c> entry point when the parser
///   needs it; for now <c>/</c> always lexes as division.
/// - <b>Template literals</b> (<c>`foo ${bar}`</c>) — ES2015 feature,
///   phase 3b.
/// - <b>BigInt literals</b> (<c>42n</c>) — phase 3c.
/// - <b>Unicode identifiers</b> beyond ASCII — phase 3b if a site
///   needs them.
/// - <b>Automatic semicolon insertion.</b> ASI is a parser concern;
///   the lexer does not inject synthetic semicolons.
/// - <b>Hashbang</b> (<c>#!/usr/bin/env node</c>) — only relevant for
///   CLI scripts, not web JS.
/// </summary>
public sealed class JsLexer
{
    private readonly string _src;
    private int _pos;

    // Template literal state — used to drive the context-
    // sensitive lexing of `${ ... }` interpolations. When
    // a template enters an interpolation (via `${`), we push
    // the brace depth at that moment onto _templateStack.
    // Every `{` inside the interpolation bumps _braceDepth;
    // the matching `}` at the pushed depth emits a
    // TemplateMiddle / TemplateTail instead of a RightBrace
    // punctuator and switches back to template-string scan
    // mode. Nesting templates inside template interpolations
    // just adds more entries to the stack.
    private int _braceDepth;
    private readonly List<int> _templateStack = new();

    /// <summary>
    /// Kind of the most recent non-trivia token we emitted,
    /// or <see cref="JsTokenKind.Unknown"/> at start of
    /// input. Used for the context-sensitive <c>/</c>
    /// disambiguation: at a position where a regex literal
    /// is valid (after <c>(</c>, <c>,</c>, <c>return</c>, ...),
    /// a slash starts a regex; otherwise it's division.
    /// This is the classic JS lexer ambiguity.
    /// </summary>
    private JsTokenKind _prevKind = JsTokenKind.Unknown;

    public JsLexer(string source)
    {
        _src = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Advance past whitespace / comments and return the next token,
    /// or <see cref="JsTokenKind.EndOfFile"/> at end of input. The
    /// lexer does not buffer — call this in a loop.
    /// </summary>
    public JsToken Next()
    {
        var tok = NextInternal();
        if (tok.Kind != JsTokenKind.EndOfFile)
        {
            _prevKind = tok.Kind;
        }
        return tok;
    }

    private JsToken NextInternal()
    {
        SkipTrivia();

        if (_pos >= _src.Length)
        {
            return new JsToken(JsTokenKind.EndOfFile, _pos, 0);
        }

        int start = _pos;
        char c = _src[_pos];

        // Template continuation: if we're inside an
        // interpolation and see the matching `}`, switch back
        // to scanning template string content instead of
        // emitting a plain RightBrace punctuator.
        if (c == '}' &&
            _templateStack.Count > 0 &&
            _templateStack[^1] == _braceDepth)
        {
            _templateStack.RemoveAt(_templateStack.Count - 1);
            _pos++; // consume the closing `}`
            return ScanTemplateSpan(start, isHead: false);
        }

        // Identifiers and keywords.
        if (IsIdentifierStart(c))
        {
            return ScanIdentifierOrKeyword(start);
        }

        // ES2022 private class field / method names: `#name`.
        // The `#` prefix is part of the identifier. We lex it
        // as a regular Identifier token with the `#` included
        // in the StringValue so the parser and compiler can
        // treat it as a property name. Privacy enforcement is
        // deferred — the name is stored and looked up like any
        // other property, just with a `#` prefix that script
        // code can't synthesize (no `obj['#foo']` equivalent).
        if (c == '#' && _pos + 1 < _src.Length && IsIdentifierStart(_src[_pos + 1]))
        {
            _pos++; // skip '#'
            int nameStart = _pos;
            while (_pos < _src.Length && IsIdentifierPart(_src[_pos])) _pos++;
            var name = "#" + _src[nameStart.._pos];
            return new JsToken(JsTokenKind.Identifier, start, _pos - start, stringValue: name);
        }

        // Number literal starting with a digit.
        if (c >= '0' && c <= '9')
        {
            return ScanNumber(start);
        }

        // '.' could be a number (.5), punctuator (.), or spread (...).
        if (c == '.')
        {
            if (_pos + 1 < _src.Length && _src[_pos + 1] >= '0' && _src[_pos + 1] <= '9')
            {
                return ScanNumber(start);
            }
            if (_pos + 2 < _src.Length && _src[_pos + 1] == '.' && _src[_pos + 2] == '.')
            {
                _pos += 3;
                return new JsToken(JsTokenKind.DotDotDot, start, 3);
            }
            _pos++;
            return new JsToken(JsTokenKind.Dot, start, 1);
        }

        // String literals.
        if (c == '"' || c == '\'')
        {
            return ScanString(start, c);
        }

        // Template literal — enter template-string scan mode.
        if (c == '`')
        {
            _pos++; // consume the opening backtick
            return ScanTemplateSpan(start, isHead: true);
        }

        // Track `{` / `}` brace depth for template continuation
        // detection before falling through to the normal
        // punctuator path (which also emits LeftBrace/RightBrace).
        if (c == '{')
        {
            _braceDepth++;
            _pos++;
            return new JsToken(JsTokenKind.LeftBrace, start, 1);
        }
        if (c == '}')
        {
            if (_braceDepth > 0) _braceDepth--;
            _pos++;
            return new JsToken(JsTokenKind.RightBrace, start, 1);
        }

        // Punctuators.
        return ScanPunctuator(start, c);
    }

    /// <summary>
    /// Enumerate every token including the final EOF. Convenience for
    /// tests and simple callers that don't need the per-call Next() loop.
    /// </summary>
    public IEnumerable<JsToken> Tokens()
    {
        while (true)
        {
            var t = Next();
            yield return t;
            if (t.Kind == JsTokenKind.EndOfFile) yield break;
        }
    }

    // -------------------------------------------------------------------
    // Trivia: whitespace + comments
    // -------------------------------------------------------------------

    private void SkipTrivia()
    {
        while (_pos < _src.Length)
        {
            char c = _src[_pos];
            if (IsWhitespace(c))
            {
                _pos++;
                continue;
            }
            if (c == '/' && _pos + 1 < _src.Length)
            {
                char next = _src[_pos + 1];
                if (next == '/')
                {
                    SkipLineComment();
                    continue;
                }
                if (next == '*')
                {
                    SkipBlockComment();
                    continue;
                }
            }
            return;
        }
    }

    private void SkipLineComment()
    {
        // Past the '//' — skip until end of line.
        _pos += 2;
        while (_pos < _src.Length)
        {
            char c = _src[_pos];
            if (c == '\n' || c == '\r') return;
            _pos++;
        }
    }

    private void SkipBlockComment()
    {
        // Past the '/*' — skip until '*/'.
        _pos += 2;
        while (_pos < _src.Length)
        {
            if (_src[_pos] == '*' && _pos + 1 < _src.Length && _src[_pos + 1] == '/')
            {
                _pos += 2;
                return;
            }
            _pos++;
        }
        // Unterminated block comment — silently stop at EOF. The parser
        // will notice the missing close via token positions if needed.
    }

    // -------------------------------------------------------------------
    // Identifiers and keywords
    // -------------------------------------------------------------------

    private JsToken ScanIdentifierOrKeyword(int start)
    {
        _pos++; // consume the start char we already validated
        while (_pos < _src.Length && IsIdentifierPart(_src[_pos]))
        {
            _pos++;
        }

        var text = _src[start.._pos];
        var kind = LookupKeyword(text);
        return new JsToken(kind, start, _pos - start, kind == JsTokenKind.Identifier ? text : null);
    }

    /// <summary>
    /// Map a candidate identifier to a keyword token kind, or
    /// <see cref="JsTokenKind.Identifier"/> if it isn't a keyword.
    /// Ordered by rough frequency for a faint hot-path micro-win;
    /// the compiler is free to compile this to a jump table.
    /// </summary>
    private static JsTokenKind LookupKeyword(string text) => text switch
    {
        // Literal-like keywords.
        "null" => JsTokenKind.NullLiteral,
        "true" => JsTokenKind.TrueLiteral,
        "false" => JsTokenKind.FalseLiteral,

        // ES5 keywords.
        "var" => JsTokenKind.KeywordVar,
        "function" => JsTokenKind.KeywordFunction,
        "return" => JsTokenKind.KeywordReturn,
        "if" => JsTokenKind.KeywordIf,
        "else" => JsTokenKind.KeywordElse,
        "for" => JsTokenKind.KeywordFor,
        "while" => JsTokenKind.KeywordWhile,
        "do" => JsTokenKind.KeywordDo,
        "break" => JsTokenKind.KeywordBreak,
        "continue" => JsTokenKind.KeywordContinue,
        "this" => JsTokenKind.KeywordThis,
        "new" => JsTokenKind.KeywordNew,
        "typeof" => JsTokenKind.KeywordTypeof,
        "instanceof" => JsTokenKind.KeywordInstanceof,
        "in" => JsTokenKind.KeywordIn,
        "delete" => JsTokenKind.KeywordDelete,
        "void" => JsTokenKind.KeywordVoid,
        "throw" => JsTokenKind.KeywordThrow,
        "try" => JsTokenKind.KeywordTry,
        "catch" => JsTokenKind.KeywordCatch,
        "finally" => JsTokenKind.KeywordFinally,
        "switch" => JsTokenKind.KeywordSwitch,
        "case" => JsTokenKind.KeywordCase,
        "default" => JsTokenKind.KeywordDefault,
        "debugger" => JsTokenKind.KeywordDebugger,
        "with" => JsTokenKind.KeywordWith,

        // ES2015+ / future-reserved keywords.
        "let" => JsTokenKind.KeywordLet,
        "const" => JsTokenKind.KeywordConst,
        "class" => JsTokenKind.KeywordClass,
        "extends" => JsTokenKind.KeywordExtends,
        "super" => JsTokenKind.KeywordSuper,
        "import" => JsTokenKind.KeywordImport,
        "export" => JsTokenKind.KeywordExport,
        "enum" => JsTokenKind.KeywordEnum,
        "yield" => JsTokenKind.KeywordYield,
        "static" => JsTokenKind.KeywordStatic,

        _ => JsTokenKind.Identifier,
    };

    // -------------------------------------------------------------------
    // Numbers
    // -------------------------------------------------------------------

    // -------------------------------------------------------------------
    // Regex literals — context-sensitive.
    // -------------------------------------------------------------------

    /// <summary>
    /// Classic JS "can a regex start here?" check, driven
    /// by the most recent token kind. A regex is legal
    /// after:
    /// <list type="bullet">
    /// <item>Start of input (<see cref="JsTokenKind.Unknown"/>)</item>
    /// <item>Opening delimiters: <c>(</c>, <c>[</c>, <c>{</c>,
    ///   <c>,</c>, <c>;</c>, <c>:</c>, <c>?</c></item>
    /// <item>Operators that can't precede an rvalue division:
    ///   assignment, arithmetic, bitwise, logical, equality,
    ///   relational, <c>typeof</c> / <c>delete</c> / <c>void</c>
    ///   / <c>return</c> / <c>throw</c> / <c>new</c>
    ///   / <c>in</c> / <c>instanceof</c> / <c>yield</c>
    ///   / <c>await</c> / <c>case</c></item>
    /// </list>
    /// Everything else (identifiers, numbers, <c>)</c>,
    /// <c>]</c>, postfix <c>++</c>, etc.) means division.
    /// </summary>
    private static bool CanStartRegex(JsTokenKind prev) => prev switch
    {
        // Start of input.
        JsTokenKind.Unknown => true,
        // Structural openers + punctuation that can't be followed
        // by a division continuation.
        JsTokenKind.LeftParen or JsTokenKind.LeftBracket or
        JsTokenKind.LeftBrace or JsTokenKind.RightBrace or
        JsTokenKind.Comma or JsTokenKind.Semicolon or
        JsTokenKind.Colon or JsTokenKind.QuestionMark or
        JsTokenKind.Arrow or JsTokenKind.DotDotDot => true,
        // All assignment operators.
        JsTokenKind.Assign or JsTokenKind.PlusAssign or
        JsTokenKind.MinusAssign or JsTokenKind.StarAssign or
        JsTokenKind.SlashAssign or JsTokenKind.PercentAssign or
        JsTokenKind.LeftShiftAssign or JsTokenKind.RightShiftAssign or
        JsTokenKind.UnsignedRightShiftAssign or
        JsTokenKind.AmpersandAssign or JsTokenKind.PipeAssign or
        JsTokenKind.CaretAssign or JsTokenKind.AmpersandAmpersandAssign or
        JsTokenKind.PipePipeAssign or JsTokenKind.QuestionQuestionAssign => true,
        // Arithmetic, bitwise, logical, relational, equality.
        JsTokenKind.Plus or JsTokenKind.Minus or JsTokenKind.Star or
        JsTokenKind.Slash or JsTokenKind.Percent or
        JsTokenKind.LeftShift or JsTokenKind.RightShift or
        JsTokenKind.UnsignedRightShift or
        JsTokenKind.Ampersand or JsTokenKind.Pipe or JsTokenKind.Caret or
        JsTokenKind.Tilde or JsTokenKind.Exclamation or
        JsTokenKind.AmpersandAmpersand or JsTokenKind.PipePipe or
        JsTokenKind.QuestionQuestion or
        JsTokenKind.LessThan or JsTokenKind.GreaterThan or
        JsTokenKind.LessThanEqual or JsTokenKind.GreaterThanEqual or
        JsTokenKind.Equal or JsTokenKind.NotEqual or
        JsTokenKind.StrictEqual or JsTokenKind.StrictNotEqual => true,
        // Keywords that can't be followed by a division.
        JsTokenKind.KeywordReturn or JsTokenKind.KeywordTypeof or
        JsTokenKind.KeywordDelete or JsTokenKind.KeywordVoid or
        JsTokenKind.KeywordNew or JsTokenKind.KeywordIn or
        JsTokenKind.KeywordInstanceof or JsTokenKind.KeywordThrow or
        JsTokenKind.KeywordCase or JsTokenKind.KeywordYield or
        JsTokenKind.KeywordDo or JsTokenKind.KeywordElse => true,
        _ => false,
    };

    /// <summary>
    /// Scan <c>/pattern/flags</c> starting at the <c>/</c>
    /// already at <see cref="_pos"/>. The pattern is
    /// terminated by an unescaped <c>/</c> not inside a
    /// character class. <c>\</c> escapes the next character,
    /// <c>[</c> enters a character class until the matching
    /// <c>]</c>, and inside a class <c>/</c> is literal.
    /// The trailing flag run is any contiguous sequence of
    /// identifier characters.
    /// </summary>
    private JsToken ScanRegExpLiteral(int start)
    {
        _pos++; // consume the opening '/'
        int bodyStart = _pos;
        bool inClass = false;
        while (_pos < _src.Length)
        {
            char ch = _src[_pos];
            if (ch == '\n' || ch == '\r')
            {
                // Unterminated — spec forbids line terminators
                // inside a regex body. Fall back to an Unknown
                // token so the parser reports a sensible error.
                return new JsToken(JsTokenKind.Unknown, start, _pos - start);
            }
            if (ch == '\\' && _pos + 1 < _src.Length)
            {
                // Skip the escaped character verbatim.
                _pos += 2;
                continue;
            }
            if (ch == '[') inClass = true;
            else if (ch == ']') inClass = false;
            else if (ch == '/' && !inClass)
            {
                break;
            }
            _pos++;
        }
        if (_pos >= _src.Length || _src[_pos] != '/')
        {
            return new JsToken(JsTokenKind.Unknown, start, _pos - start);
        }
        int bodyEnd = _pos;
        _pos++; // consume the closing '/'

        int flagStart = _pos;
        while (_pos < _src.Length && IsIdentifierPart(_src[_pos]))
        {
            _pos++;
        }

        var body = _src[bodyStart..bodyEnd];
        var flags = _src[flagStart.._pos];
        // Store as "body|flags" so the parser can split. The
        // pipe can't appear in a flag run (only letters are
        // valid flags) so it's safe as a separator.
        var payload = body + "|" + flags;
        return new JsToken(JsTokenKind.RegExpLiteral, start, _pos - start,
            stringValue: payload);
    }

    private JsToken ScanNumber(int start)
    {
        // Hex (0x or 0X).
        if (_src[_pos] == '0' && _pos + 1 < _src.Length &&
            (_src[_pos + 1] == 'x' || _src[_pos + 1] == 'X'))
        {
            _pos += 2;
            int digitStart = _pos;
            while (_pos < _src.Length && IsHexDigit(_src[_pos])) _pos++;
            if (_pos == digitStart)
            {
                // "0x" with no digits — treat as an error by emitting
                // an Unknown token covering what we consumed. The parser
                // will stop on it.
                return new JsToken(JsTokenKind.Unknown, start, _pos - start);
            }

            var hexText = _src[digitStart.._pos];
            // BigInt hex suffix: `0x1fn`. Emit as a BigIntLiteral
            // with the digit text stashed in StringValue so the
            // parser can build a BigInteger via TryParse.
            if (_pos < _src.Length && _src[_pos] == 'n')
            {
                _pos++;
                return new JsToken(JsTokenKind.BigIntLiteral, start, _pos - start,
                    stringValue: "0x" + hexText);
            }
            double hexValue = (double)long.Parse(hexText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new JsToken(JsTokenKind.NumberLiteral, start, _pos - start, numberValue: hexValue);
        }

        // Decimal integer part. '.5' and '0.5' both land here because
        // the caller routed leading-dot numbers to ScanNumber.
        int intStart = _pos;
        while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '9') _pos++;
        int intEnd = _pos;
        bool hasFractionOrExp = false;

        // Fractional part.
        if (_pos < _src.Length && _src[_pos] == '.')
        {
            hasFractionOrExp = true;
            _pos++;
            while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '9') _pos++;
        }

        // Exponent.
        if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
        {
            hasFractionOrExp = true;
            _pos++;
            if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
            int expStart = _pos;
            while (_pos < _src.Length && _src[_pos] >= '0' && _src[_pos] <= '9') _pos++;
            if (_pos == expStart)
            {
                return new JsToken(JsTokenKind.Unknown, start, _pos - start);
            }
        }

        // BigInt decimal suffix: `42n`. Only allowed on integer
        // literals — `3.14n` is a syntax error per spec.
        if (!hasFractionOrExp && _pos < _src.Length && _src[_pos] == 'n')
        {
            var digitText = _src[intStart..intEnd];
            _pos++;
            return new JsToken(JsTokenKind.BigIntLiteral, start, _pos - start,
                stringValue: digitText);
        }

        var text = _src[start.._pos];
        if (!double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return new JsToken(JsTokenKind.Unknown, start, _pos - start);
        }
        return new JsToken(JsTokenKind.NumberLiteral, start, _pos - start, numberValue: value);
    }

    // -------------------------------------------------------------------
    // Strings
    // -------------------------------------------------------------------

    private JsToken ScanString(int start, char quote)
    {
        _pos++; // consume opening quote
        var sb = new StringBuilder();

        while (_pos < _src.Length)
        {
            char c = _src[_pos];

            if (c == quote)
            {
                _pos++;
                return new JsToken(
                    JsTokenKind.StringLiteral, start, _pos - start, stringValue: sb.ToString());
            }

            if (c == '\n' || c == '\r')
            {
                // Unterminated string — emit as Unknown at the point
                // where the line break happened.
                return new JsToken(JsTokenKind.Unknown, start, _pos - start);
            }

            if (c == '\\')
            {
                _pos++;
                if (_pos >= _src.Length)
                {
                    return new JsToken(JsTokenKind.Unknown, start, _pos - start);
                }
                HandleStringEscape(sb);
                continue;
            }

            sb.Append(c);
            _pos++;
        }

        // EOF before closing quote.
        return new JsToken(JsTokenKind.Unknown, start, _pos - start);
    }

    private void HandleStringEscape(StringBuilder sb)
    {
        char esc = _src[_pos];
        switch (esc)
        {
            case 'n': sb.Append('\n'); _pos++; return;
            case 't': sb.Append('\t'); _pos++; return;
            case 'r': sb.Append('\r'); _pos++; return;
            case 'b': sb.Append('\b'); _pos++; return;
            case 'f': sb.Append('\f'); _pos++; return;
            case 'v': sb.Append('\v'); _pos++; return;
            case '0':
                // Only '\0' (not followed by another digit) is the null char.
                // Legacy octal escapes ('\012') are not supported.
                sb.Append('\0'); _pos++; return;
            case '\'': sb.Append('\''); _pos++; return;
            case '"': sb.Append('"'); _pos++; return;
            case '\\': sb.Append('\\'); _pos++; return;
            case '/': sb.Append('/'); _pos++; return;

            case '\n':
                // Line continuation — produces no output.
                _pos++;
                return;
            case '\r':
                _pos++;
                if (_pos < _src.Length && _src[_pos] == '\n') _pos++;
                return;

            case 'x':
                _pos++;
                if (_pos + 2 <= _src.Length &&
                    IsHexDigit(_src[_pos]) && IsHexDigit(_src[_pos + 1]))
                {
                    int code = (HexDigitValue(_src[_pos]) << 4) | HexDigitValue(_src[_pos + 1]);
                    sb.Append((char)code);
                    _pos += 2;
                }
                else
                {
                    // Malformed \x — emit the literal characters.
                    sb.Append('x');
                }
                return;

            case 'u':
                _pos++;
                if (_pos + 4 <= _src.Length &&
                    IsHexDigit(_src[_pos]) && IsHexDigit(_src[_pos + 1]) &&
                    IsHexDigit(_src[_pos + 2]) && IsHexDigit(_src[_pos + 3]))
                {
                    int code =
                        (HexDigitValue(_src[_pos]) << 12) |
                        (HexDigitValue(_src[_pos + 1]) << 8) |
                        (HexDigitValue(_src[_pos + 2]) << 4) |
                        HexDigitValue(_src[_pos + 3]);
                    sb.Append((char)code);
                    _pos += 4;
                }
                else
                {
                    sb.Append('u');
                }
                return;

            default:
                // Unrecognized escape: per spec, a non-escape char after
                // '\' represents itself. This is looser than strict mode
                // ES5 in some cases, but matches what every browser does.
                sb.Append(esc);
                _pos++;
                return;
        }
    }

    // -------------------------------------------------------------------
    // Template literals
    // -------------------------------------------------------------------

    /// <summary>
    /// Scan a template-literal span, starting from the current
    /// position (which is just past either the opening backtick
    /// or a <c>}</c> closing an interpolation). Reads until the
    /// next <c>${</c> or closing backtick, decoding escape
    /// sequences along the way.
    ///
    /// On <c>${</c>: emits <see cref="JsTokenKind.TemplateHead"/>
    /// if this is the first span of the template (when
    /// <paramref name="isHead"/>) or
    /// <see cref="JsTokenKind.TemplateMiddle"/> otherwise, and
    /// pushes the current <see cref="_braceDepth"/> onto
    /// <see cref="_templateStack"/> so the matching <c>}</c>
    /// can be recognized.
    ///
    /// On closing backtick: emits
    /// <see cref="JsTokenKind.NoSubstitutionTemplate"/> if this
    /// is the first span (no interpolations), or
    /// <see cref="JsTokenKind.TemplateTail"/> otherwise.
    /// </summary>
    private JsToken ScanTemplateSpan(int start, bool isHead)
    {
        var sb = new StringBuilder();
        while (_pos < _src.Length)
        {
            char c = _src[_pos];

            if (c == '`')
            {
                _pos++; // consume closing backtick
                var kind = isHead
                    ? JsTokenKind.NoSubstitutionTemplate
                    : JsTokenKind.TemplateTail;
                return new JsToken(kind, start, _pos - start, stringValue: sb.ToString());
            }

            if (c == '$' && _pos + 1 < _src.Length && _src[_pos + 1] == '{')
            {
                _pos += 2; // consume `${`
                _templateStack.Add(_braceDepth);
                var kind = isHead
                    ? JsTokenKind.TemplateHead
                    : JsTokenKind.TemplateMiddle;
                return new JsToken(kind, start, _pos - start, stringValue: sb.ToString());
            }

            if (c == '\\')
            {
                _pos++;
                if (_pos >= _src.Length)
                {
                    return new JsToken(JsTokenKind.Unknown, start, _pos - start);
                }
                HandleTemplateEscape(sb);
                continue;
            }

            // CRLF and bare CR normalize to LF per ECMA §11.8.6.
            if (c == '\r')
            {
                sb.Append('\n');
                _pos++;
                if (_pos < _src.Length && _src[_pos] == '\n') _pos++;
                continue;
            }

            sb.Append(c);
            _pos++;
        }

        // EOF inside an unterminated template.
        return new JsToken(JsTokenKind.Unknown, start, _pos - start);
    }

    /// <summary>
    /// Escape-sequence handler for template literal spans.
    /// Very close to <see cref="HandleStringEscape"/> but also
    /// accepts <c>\`</c> (literal backtick) and treats <c>\$</c>
    /// as a literal dollar sign (so an escaped
    /// <c>\${</c> produces a literal <c>${</c> in the result).
    /// </summary>
    private void HandleTemplateEscape(StringBuilder sb)
    {
        char esc = _src[_pos];
        switch (esc)
        {
            case 'n': sb.Append('\n'); _pos++; return;
            case 't': sb.Append('\t'); _pos++; return;
            case 'r': sb.Append('\r'); _pos++; return;
            case 'b': sb.Append('\b'); _pos++; return;
            case 'f': sb.Append('\f'); _pos++; return;
            case 'v': sb.Append('\v'); _pos++; return;
            case '0': sb.Append('\0'); _pos++; return;
            case '\'': sb.Append('\''); _pos++; return;
            case '"': sb.Append('"'); _pos++; return;
            case '\\': sb.Append('\\'); _pos++; return;
            case '/': sb.Append('/'); _pos++; return;
            case '`': sb.Append('`'); _pos++; return;
            case '$': sb.Append('$'); _pos++; return;

            case '\n':
                // Line continuation — produces no output.
                _pos++;
                return;
            case '\r':
                _pos++;
                if (_pos < _src.Length && _src[_pos] == '\n') _pos++;
                return;

            case 'x':
                _pos++;
                if (_pos + 2 <= _src.Length &&
                    IsHexDigit(_src[_pos]) && IsHexDigit(_src[_pos + 1]))
                {
                    int code = (HexDigitValue(_src[_pos]) << 4) |
                               HexDigitValue(_src[_pos + 1]);
                    sb.Append((char)code);
                    _pos += 2;
                }
                else
                {
                    sb.Append('x');
                }
                return;

            case 'u':
                _pos++;
                if (_pos + 4 <= _src.Length &&
                    IsHexDigit(_src[_pos]) && IsHexDigit(_src[_pos + 1]) &&
                    IsHexDigit(_src[_pos + 2]) && IsHexDigit(_src[_pos + 3]))
                {
                    int code =
                        (HexDigitValue(_src[_pos]) << 12) |
                        (HexDigitValue(_src[_pos + 1]) << 8) |
                        (HexDigitValue(_src[_pos + 2]) << 4) |
                        HexDigitValue(_src[_pos + 3]);
                    sb.Append((char)code);
                    _pos += 4;
                }
                else
                {
                    sb.Append('u');
                }
                return;

            default:
                sb.Append(esc);
                _pos++;
                return;
        }
    }

    // -------------------------------------------------------------------
    // Punctuators
    // -------------------------------------------------------------------

    private JsToken ScanPunctuator(int start, char c)
    {
        // Greedy matching: try the longest punctuator first at each
        // dispatch. Single-char fallback at the bottom of each branch.
        switch (c)
        {
            case '(': _pos++; return new JsToken(JsTokenKind.LeftParen, start, 1);
            case ')': _pos++; return new JsToken(JsTokenKind.RightParen, start, 1);
            case '{': _pos++; return new JsToken(JsTokenKind.LeftBrace, start, 1);
            case '}': _pos++; return new JsToken(JsTokenKind.RightBrace, start, 1);
            case '[': _pos++; return new JsToken(JsTokenKind.LeftBracket, start, 1);
            case ']': _pos++; return new JsToken(JsTokenKind.RightBracket, start, 1);
            case ';': _pos++; return new JsToken(JsTokenKind.Semicolon, start, 1);
            case ',': _pos++; return new JsToken(JsTokenKind.Comma, start, 1);
            case ':': _pos++; return new JsToken(JsTokenKind.Colon, start, 1);
            case '?':
                // ES2020: `??` (nullish coalesce), `??=`
                // (nullish assign), `?.` (optional chain).
                // Note: `?.` is NOT emitted when followed by
                // a digit — `a ? .5 : b` must parse as
                // ternary + number literal, not optional
                // chain.
                if (Peek(1) == '?')
                {
                    if (Peek(2) == '=')
                    {
                        _pos += 3;
                        return new JsToken(JsTokenKind.QuestionQuestionAssign, start, 3);
                    }
                    _pos += 2;
                    return new JsToken(JsTokenKind.QuestionQuestion, start, 2);
                }
                if (Peek(1) == '.' && !(Peek(2) >= '0' && Peek(2) <= '9'))
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.QuestionDot, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.QuestionMark, start, 1);
            case '~': _pos++; return new JsToken(JsTokenKind.Tilde, start, 1);

            case '=':
                if (Peek(1) == '=')
                {
                    if (Peek(2) == '=')
                    {
                        _pos += 3;
                        return new JsToken(JsTokenKind.StrictEqual, start, 3);
                    }
                    _pos += 2;
                    return new JsToken(JsTokenKind.Equal, start, 2);
                }
                if (Peek(1) == '>')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.Arrow, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Assign, start, 1);

            case '!':
                if (Peek(1) == '=')
                {
                    if (Peek(2) == '=')
                    {
                        _pos += 3;
                        return new JsToken(JsTokenKind.StrictNotEqual, start, 3);
                    }
                    _pos += 2;
                    return new JsToken(JsTokenKind.NotEqual, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Exclamation, start, 1);

            case '<':
                if (Peek(1) == '<')
                {
                    if (Peek(2) == '=')
                    {
                        _pos += 3;
                        return new JsToken(JsTokenKind.LeftShiftAssign, start, 3);
                    }
                    _pos += 2;
                    return new JsToken(JsTokenKind.LeftShift, start, 2);
                }
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.LessThanEqual, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.LessThan, start, 1);

            case '>':
                if (Peek(1) == '>')
                {
                    if (Peek(2) == '>')
                    {
                        if (Peek(3) == '=')
                        {
                            _pos += 4;
                            return new JsToken(JsTokenKind.UnsignedRightShiftAssign, start, 4);
                        }
                        _pos += 3;
                        return new JsToken(JsTokenKind.UnsignedRightShift, start, 3);
                    }
                    if (Peek(2) == '=')
                    {
                        _pos += 3;
                        return new JsToken(JsTokenKind.RightShiftAssign, start, 3);
                    }
                    _pos += 2;
                    return new JsToken(JsTokenKind.RightShift, start, 2);
                }
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.GreaterThanEqual, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.GreaterThan, start, 1);

            case '+':
                if (Peek(1) == '+')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.PlusPlus, start, 2);
                }
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.PlusAssign, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Plus, start, 1);

            case '-':
                if (Peek(1) == '-')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.MinusMinus, start, 2);
                }
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.MinusAssign, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Minus, start, 1);

            case '*':
                if (Peek(1) == '*')
                {
                    if (Peek(2) == '=')
                    {
                        _pos += 3;
                        // **= (exponent-assign) — not yet wired as
                        // a compound operator; lex it so the parser
                        // can reject it with a clear error instead
                        // of a confusing "unexpected Star".
                        return new JsToken(JsTokenKind.StarStar, start, 2);
                        // TODO: StarStarAssign token + wiring
                    }
                    _pos += 2;
                    return new JsToken(JsTokenKind.StarStar, start, 2);
                }
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.StarAssign, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Star, start, 1);

            case '/':
                // Context-sensitive disambiguation: at a position
                // where a regex literal is valid, scan /pattern/flags.
                // Otherwise treat as division (or /= compound assign).
                if (CanStartRegex(_prevKind))
                {
                    return ScanRegExpLiteral(start);
                }
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.SlashAssign, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Slash, start, 1);

            case '%':
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.PercentAssign, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Percent, start, 1);

            case '&':
                if (Peek(1) == '&')
                {
                    if (Peek(2) == '=')
                    {
                        _pos += 3;
                        return new JsToken(JsTokenKind.AmpersandAmpersandAssign, start, 3);
                    }
                    _pos += 2;
                    return new JsToken(JsTokenKind.AmpersandAmpersand, start, 2);
                }
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.AmpersandAssign, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Ampersand, start, 1);

            case '|':
                if (Peek(1) == '|')
                {
                    if (Peek(2) == '=')
                    {
                        _pos += 3;
                        return new JsToken(JsTokenKind.PipePipeAssign, start, 3);
                    }
                    _pos += 2;
                    return new JsToken(JsTokenKind.PipePipe, start, 2);
                }
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.PipeAssign, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Pipe, start, 1);

            case '^':
                if (Peek(1) == '=')
                {
                    _pos += 2;
                    return new JsToken(JsTokenKind.CaretAssign, start, 2);
                }
                _pos++;
                return new JsToken(JsTokenKind.Caret, start, 1);
        }

        // Anything else: emit as Unknown so the parser sees it.
        _pos++;
        return new JsToken(JsTokenKind.Unknown, start, 1);
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private char Peek(int offset)
    {
        int p = _pos + offset;
        return p < _src.Length ? _src[p] : '\0';
    }

    private static bool IsWhitespace(char c) =>
        c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f' ||
        // Unicode no-break space. Beyond this we don't bother — if a site
        // uses a rare Unicode space character, it'll fail to tokenize and
        // we'll add support.
        c == '\u00A0';

    private static bool IsIdentifierStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '$' ||
        // ES2015+ allows any Unicode letter (UnicodeIDStart) as an
        // identifier start. We approximate via .NET's
        // <see cref="char.IsLetter"/> which covers all letter
        // categories — the spec also includes Number-letter
        // (Nl, e.g. Roman numerals) which IsLetter does not, but
        // those are vanishingly rare in real code. Common cases
        // we care about: minified bundlers that emit non-ASCII
        // property keys (ã, ñ, ko/ja chars) inside transliteration
        // tables; without this, our lexer rejected those keys.
        (c >= 0x80 && char.IsLetter(c));

    private static bool IsIdentifierPart(char c) =>
        IsIdentifierStart(c) || (c >= '0' && c <= '9') ||
        // Unicode digits (Nd) and combining marks (Mn/Mc) are
        // allowed in identifier continuation per UnicodeIDContinue.
        (c >= 0x80 && (char.IsDigit(c) || char.IsLetter(c)));

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexDigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
