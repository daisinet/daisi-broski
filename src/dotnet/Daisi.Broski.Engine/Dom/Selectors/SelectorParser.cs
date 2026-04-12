namespace Daisi.Broski.Engine.Dom.Selectors;

/// <summary>
/// Parses a CSS selector string into a <see cref="SelectorList"/> AST.
///
/// Supported grammar (a pragmatic subset of CSS Selectors Level 4):
///
/// - Type selectors: <c>div</c>, <c>*</c>
/// - ID selectors: <c>#main</c>
/// - Class selectors: <c>.primary</c>
/// - Attribute selectors:
///     <c>[attr]</c>, <c>[attr="x"]</c>, <c>[attr~="x"]</c>, <c>[attr|="x"]</c>,
///     <c>[attr^="x"]</c>, <c>[attr$="x"]</c>, <c>[attr*="x"]</c>, with
///     optional <c>i</c> flag for case-insensitive value matching.
/// - Combinators: descendant (space), <c>&gt;</c>, <c>+</c>, <c>~</c>
/// - Compound selectors: concatenation, e.g. <c>div.foo#bar[attr]</c>
/// - Selector lists: <c>a, b, c</c>
/// - Pseudo-classes: <c>:first-child</c>, <c>:last-child</c>,
///   <c>:only-child</c>, <c>:first-of-type</c>, <c>:last-of-type</c>,
///   <c>:only-of-type</c>, <c>:nth-child(An+B)</c>,
///   <c>:nth-last-child(An+B)</c>, <c>:nth-of-type(An+B)</c>,
///   <c>:nth-last-of-type(An+B)</c>, <c>:root</c>, <c>:empty</c>,
///   <c>:not(selector-list)</c>
///
/// Not supported in phase 1:
/// - Pseudo-elements (<c>::before</c>, <c>::after</c>, etc.)
/// - <c>:has()</c>, <c>:is()</c>, <c>:where()</c>
/// - Namespace prefixes
/// - Case-insensitive flag edge cases beyond the common <c>i</c> form
///
/// Selectors that use unsupported features throw
/// <see cref="SelectorParseException"/> so the caller can surface a
/// clean diagnostic.
/// </summary>
public static class SelectorParser
{
    public static SelectorList Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new Impl(source);
        return parser.ParseList();
    }

    private sealed class Impl
    {
        private readonly string _src;
        private int _pos;

        public Impl(string src) { _src = src; }

        public SelectorList ParseList()
        {
            var selectors = new List<ComplexSelector>();
            SkipWhitespace();
            selectors.Add(ParseComplex());
            SkipWhitespace();
            while (_pos < _src.Length && _src[_pos] == ',')
            {
                _pos++;
                SkipWhitespace();
                selectors.Add(ParseComplex());
                SkipWhitespace();
            }
            if (_pos < _src.Length)
                throw Fail($"Unexpected character '{_src[_pos]}' at position {_pos}");
            return new SelectorList(selectors);
        }

        private ComplexSelector ParseComplex()
        {
            var compounds = new List<CompoundSelector> { ParseCompound() };
            var combinators = new List<Combinator>();

            while (true)
            {
                int before = _pos;
                SkipWhitespace();

                if (_pos >= _src.Length || _src[_pos] == ',') return Build(compounds, combinators);

                Combinator? explicitCombinator = _src[_pos] switch
                {
                    '>' => Combinator.Child,
                    '+' => Combinator.AdjacentSibling,
                    '~' => Combinator.GeneralSibling,
                    _ => null,
                };

                if (explicitCombinator is { } explicitly)
                {
                    _pos++;
                    SkipWhitespace();
                    combinators.Add(explicitly);
                    compounds.Add(ParseCompound());
                    continue;
                }

                // If we advanced past whitespace and next is the start of a
                // compound (identifier, *, #, ., [, :), it's a descendant.
                if (IsCompoundStart(_src[_pos]))
                {
                    if (before != _pos) // at least one whitespace char
                    {
                        combinators.Add(Combinator.Descendant);
                        compounds.Add(ParseCompound());
                        continue;
                    }
                }

                // No combinator found — end of this complex selector.
                return Build(compounds, combinators);
            }

            static ComplexSelector Build(List<CompoundSelector> compounds, List<Combinator> combinators)
                => new(compounds, combinators);
        }

        private CompoundSelector ParseCompound()
        {
            var simples = new List<SimpleSelector>();

            // Optional type/universal selector at the start.
            if (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == '*')
                {
                    _pos++;
                    simples.Add(new TypeSelector("*"));
                }
                else if (IsIdentStart(c))
                {
                    var name = ReadIdentifier();
                    simples.Add(new TypeSelector(name.ToLowerInvariant()));
                }
            }

            // Zero or more #/./[/:/pseudo-class selectors.
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == '#')
                {
                    _pos++;
                    simples.Add(new IdSelector(ReadIdentifier()));
                }
                else if (c == '.')
                {
                    _pos++;
                    simples.Add(new ClassSelector(ReadIdentifier()));
                }
                else if (c == '[')
                {
                    simples.Add(ParseAttributeSelector());
                }
                else if (c == ':')
                {
                    simples.Add(ParsePseudoClass());
                }
                else
                {
                    break;
                }
            }

            if (simples.Count == 0)
                throw Fail($"Empty compound selector at position {_pos}");

            return new CompoundSelector(simples);
        }

        private AttributeSelector ParseAttributeSelector()
        {
            Expect('[');
            SkipWhitespace();
            var name = ReadIdentifier().ToLowerInvariant();
            SkipWhitespace();

            if (_pos < _src.Length && _src[_pos] == ']')
            {
                _pos++;
                return new AttributeSelector(name, AttributeMatch.Exists, null);
            }

            AttributeMatch match;
            if (Peek("="))
            {
                _pos += 1;
                match = AttributeMatch.Equals;
            }
            else if (Peek("~=")) { _pos += 2; match = AttributeMatch.ContainsWord; }
            else if (Peek("|=")) { _pos += 2; match = AttributeMatch.DashPrefix; }
            else if (Peek("^=")) { _pos += 2; match = AttributeMatch.StartsWith; }
            else if (Peek("$=")) { _pos += 2; match = AttributeMatch.EndsWith; }
            else if (Peek("*=")) { _pos += 2; match = AttributeMatch.Substring; }
            else throw Fail($"Expected attribute match operator at position {_pos}");

            SkipWhitespace();
            string value = ReadAttributeValue();
            SkipWhitespace();

            bool ci = false;
            if (_pos < _src.Length && (_src[_pos] == 'i' || _src[_pos] == 'I'))
            {
                ci = true;
                _pos++;
                SkipWhitespace();
            }

            Expect(']');
            return new AttributeSelector(name, match, value, ci);
        }

        private PseudoClassSelector ParsePseudoClass()
        {
            Expect(':');

            // '::' marks a pseudo-element, which we don't support.
            if (_pos < _src.Length && _src[_pos] == ':')
                throw Fail($"Pseudo-elements not supported at position {_pos}");

            var name = ReadIdentifier().ToLowerInvariant();

            switch (name)
            {
                case "first-child": return new PseudoClassSelector(PseudoClassKind.FirstChild);
                case "last-child": return new PseudoClassSelector(PseudoClassKind.LastChild);
                case "only-child": return new PseudoClassSelector(PseudoClassKind.OnlyChild);
                case "first-of-type": return new PseudoClassSelector(PseudoClassKind.FirstOfType);
                case "last-of-type": return new PseudoClassSelector(PseudoClassKind.LastOfType);
                case "only-of-type": return new PseudoClassSelector(PseudoClassKind.OnlyOfType);
                case "root": return new PseudoClassSelector(PseudoClassKind.Root);
                case "scope": return new PseudoClassSelector(PseudoClassKind.Scope);
                case "empty": return new PseudoClassSelector(PseudoClassKind.Empty);

                case "nth-child":
                case "nth-last-child":
                case "nth-of-type":
                case "nth-last-of-type":
                    {
                        var (a, b) = ReadNthArgument();
                        var kind = name switch
                        {
                            "nth-child" => PseudoClassKind.NthChild,
                            "nth-last-child" => PseudoClassKind.NthLastChild,
                            "nth-of-type" => PseudoClassKind.NthOfType,
                            "nth-last-of-type" => PseudoClassKind.NthLastOfType,
                            _ => throw Fail("unreachable"),
                        };
                        return new PseudoClassSelector(kind, a, b);
                    }

                case "not":
                    {
                        Expect('(');
                        SkipWhitespace();
                        int start = _pos;
                        int depth = 1;
                        while (_pos < _src.Length && depth > 0)
                        {
                            if (_src[_pos] == '(') depth++;
                            else if (_src[_pos] == ')') depth--;
                            if (depth > 0) _pos++;
                        }
                        if (_pos >= _src.Length) throw Fail("Unterminated :not(");
                        var inner = _src[start.._pos];
                        _pos++; // consume ')'
                        var innerList = SelectorParser.Parse(inner);
                        return new PseudoClassSelector(PseudoClassKind.Not, innerList);
                    }

                default:
                    throw Fail($"Unsupported pseudo-class ':{name}'");
            }
        }

        private (int a, int b) ReadNthArgument()
        {
            Expect('(');
            SkipWhitespace();

            int start = _pos;
            while (_pos < _src.Length && _src[_pos] != ')') _pos++;
            if (_pos >= _src.Length) throw Fail("Unterminated :nth-*(");

            var arg = _src[start.._pos].Trim().ToLowerInvariant();
            _pos++; // consume ')'

            return ParseNthExpression(arg);
        }

        private (int a, int b) ParseNthExpression(string s)
        {
            // Keywords
            if (s == "odd") return (2, 1);
            if (s == "even") return (2, 0);

            // Strip whitespace around '+' / '-' — spec permits "2n + 1".
            var normalized = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (c != ' ' && c != '\t') normalized.Append(c);
            }
            string t = normalized.ToString();

            // Forms:
            //   An+B  /  An-B  / An  /  n+B  /  n  /  -n  /  N
            int nIdx = t.IndexOf('n');
            if (nIdx < 0)
            {
                // Pure constant.
                if (!int.TryParse(t, out int b))
                    throw Fail($"Invalid :nth-* expression '{s}'");
                return (0, b);
            }

            string aPart = t[..nIdx];
            string bPart = t[(nIdx + 1)..];

            int a;
            if (aPart.Length == 0) a = 1;
            else if (aPart == "-") a = -1;
            else if (aPart == "+") a = 1;
            else if (!int.TryParse(aPart, out a))
                throw Fail($"Invalid :nth-* coefficient '{aPart}'");

            int b2;
            if (bPart.Length == 0) b2 = 0;
            else if (!int.TryParse(bPart, out b2))
                throw Fail($"Invalid :nth-* offset '{bPart}'");

            return (a, b2);
        }

        // -------- lexer primitives --------

        private void SkipWhitespace()
        {
            while (_pos < _src.Length && IsWhitespace(_src[_pos])) _pos++;
        }

        private static bool IsWhitespace(char c) =>
            c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

        private static bool IsIdentStart(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c == '-' || c >= 0x80;

        private static bool IsIdentPart(char c) =>
            IsIdentStart(c) || (c >= '0' && c <= '9');

        private static bool IsCompoundStart(char c) =>
            IsIdentStart(c) || c == '*' || c == '#' || c == '.' || c == '[' || c == ':';

        private string ReadIdentifier()
        {
            int start = _pos;
            if (_pos < _src.Length && (_src[_pos] == '-' || IsIdentStart(_src[_pos])))
            {
                _pos++;
                while (_pos < _src.Length && IsIdentPart(_src[_pos])) _pos++;
            }
            if (_pos == start) throw Fail($"Expected identifier at position {_pos}");
            return _src[start.._pos];
        }

        private string ReadAttributeValue()
        {
            if (_pos >= _src.Length) throw Fail("Unexpected end of attribute selector");
            char c = _src[_pos];

            if (c == '"' || c == '\'')
            {
                char quote = c;
                _pos++;
                int start = _pos;
                while (_pos < _src.Length && _src[_pos] != quote) _pos++;
                if (_pos >= _src.Length) throw Fail("Unterminated attribute value string");
                var result = _src[start.._pos];
                _pos++; // consume closing quote
                return result;
            }

            // Unquoted identifier-like value.
            return ReadIdentifier();
        }

        private void Expect(char c)
        {
            if (_pos >= _src.Length || _src[_pos] != c)
                throw Fail($"Expected '{c}' at position {_pos}");
            _pos++;
        }

        private bool Peek(string literal)
        {
            if (_pos + literal.Length > _src.Length) return false;
            for (int i = 0; i < literal.Length; i++)
            {
                if (_src[_pos + i] != literal[i]) return false;
            }
            return true;
        }

        private SelectorParseException Fail(string message) =>
            new(message + $" (input: \"{_src}\")");
    }
}

/// <summary>Thrown by <see cref="SelectorParser.Parse"/> when the input is not a
/// valid selector or uses an unsupported feature.</summary>
public sealed class SelectorParseException : Exception
{
    public SelectorParseException(string message) : base(message) { }
}
