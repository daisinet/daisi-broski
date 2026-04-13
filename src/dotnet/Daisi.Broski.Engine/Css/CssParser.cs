using Daisi.Broski.Engine.Dom.Selectors;

namespace Daisi.Broski.Engine.Css;

/// <summary>
/// Pragmatic CSS parser — enough surface to consume the
/// <c>&lt;style&gt;</c> blocks real sites ship today, but not a
/// full CSS Syntax Level 3 implementation. Walks the input
/// character-by-character with brace-nesting awareness;
/// reuses the existing <see cref="SelectorParser"/> for the
/// selector half so we share one Selectors-Level-4 subset
/// between query selectors and stylesheet rules.
///
/// <para>
/// Recognized productions (matches what real-world CSS
/// generators emit):
/// </para>
/// <list type="bullet">
/// <item>Style rules: <c>selector-list { decl; decl; ... }</c></item>
/// <item>Declarations: <c>property: value [!important];</c></item>
/// <item>At-rules with a block: <c>@media (max-width: ...)
///   { rules }</c>, <c>@keyframes name { rules }</c>,
///   <c>@font-face { decls }</c>, <c>@supports (...)</c>.
///   Nested rules inside a block at-rule are recursively
///   parsed; declaration-style at-rules
///   (<c>@font-face</c>) get their declarations parsed.</item>
/// <item>Plain at-rules: <c>@import "..."; @charset ...;</c>
///   stored as bare AtRules with no body.</item>
/// <item>C-style comments <c>/* ... */</c> — stripped during
///   tokenization rather than preserved on the AST.</item>
/// </list>
///
/// <para>
/// The parser is forgiving: malformed declarations are
/// dropped, unmatched braces are tolerated, and unknown
/// at-rule shapes downgrade to a bare AtRule with the raw
/// prelude. The goal is to keep parsing real-world CSS
/// (which routinely contains vendor extensions and quirks)
/// rather than refuse it.
/// </para>
/// </summary>
public static class CssParser
{
    /// <summary>Parse <paramref name="source"/> into a
    /// <see cref="Stylesheet"/>. Always returns a stylesheet —
    /// at worst, one with no rules.</summary>
    public static Stylesheet Parse(string source)
    {
        if (string.IsNullOrEmpty(source)) return new Stylesheet();
        var ctx = new ParseContext(source);
        var rules = ParseRules(ctx, expectClose: false);
        return new Stylesheet { Rules = rules };
    }

    /// <summary>Parse a single declaration block (the contents
    /// of an inline <c>style=""</c> attribute) — no surrounding
    /// braces, just <c>prop: val; prop: val</c>.</summary>
    public static IReadOnlyList<Declaration> ParseDeclarationList(string source)
    {
        if (string.IsNullOrEmpty(source)) return Array.Empty<Declaration>();
        var ctx = new ParseContext(source);
        return ParseDeclarations(ctx, expectClose: false);
    }

    private static List<Rule> ParseRules(ParseContext ctx, bool expectClose)
    {
        var rules = new List<Rule>();
        while (!ctx.AtEnd)
        {
            ctx.SkipWhitespaceAndComments();
            if (ctx.AtEnd) break;
            if (expectClose && ctx.Peek() == '}')
            {
                ctx.Advance();
                return rules;
            }
            if (ctx.Peek() == '@')
            {
                var atRule = ParseAtRule(ctx);
                if (atRule is not null) rules.Add(atRule);
                continue;
            }
            var styleRule = ParseStyleRule(ctx);
            if (styleRule is not null) rules.Add(styleRule);
        }
        return rules;
    }

    private static StyleRule? ParseStyleRule(ParseContext ctx)
    {
        // Consume the prelude up to the opening brace, then
        // parse declarations between '{' and '}'. A prelude
        // that hits ';' or EOF without a '{' is dropped — the
        // stylesheet recovers by skipping that token run.
        var prelude = ConsumeUntil(ctx, c => c is '{' or ';');
        if (ctx.AtEnd) return null;
        if (ctx.Peek() == ';') { ctx.Advance(); return null; }
        ctx.Advance(); // consume '{'

        var declarations = ParseDeclarations(ctx, expectClose: true);
        var selectorText = prelude.Trim();
        SelectorList? selectors = null;
        try
        {
            selectors = SelectorParser.Parse(selectorText);
        }
        catch (SelectorParseException)
        {
            // Selector failed to parse — keep the raw text so
            // CSSOM consumers can still see the rule. The
            // matcher will skip it.
        }
        return new StyleRule
        {
            SelectorText = selectorText,
            Selectors = selectors,
            Declarations = declarations,
        };
    }

    private static AtRule? ParseAtRule(ParseContext ctx)
    {
        ctx.Advance(); // consume '@'
        // Identifier characters: alphanumerics + '-'.
        var name = ConsumeWhile(ctx,
            c => char.IsLetterOrDigit(c) || c == '-');
        if (string.IsNullOrEmpty(name)) return null;

        // Prelude runs to ';' (declaration-style at-rule, no
        // body) or '{' (block at-rule). Strings + parens get
        // skipped over correctly so e.g.
        // `@media (max-width: 600px) {...}` doesn't terminate
        // at the colon.
        var prelude = ConsumeUntil(ctx, c => c is ';' or '{', balancedScanning: true);
        if (ctx.AtEnd)
        {
            return new AtRule { Name = name, Prelude = prelude.Trim() };
        }
        if (ctx.Peek() == ';')
        {
            ctx.Advance();
            return new AtRule { Name = name, Prelude = prelude.Trim() };
        }
        ctx.Advance(); // consume '{'

        // Block contents: at-rules with rule bodies (@media,
        // @keyframes, @supports) hold nested style rules; at-
        // rules with declaration bodies (@font-face,
        // @page) hold declarations. Distinguish by the first
        // non-whitespace, non-comment token after '{': if it
        // looks like the start of a declaration (identifier
        // followed by ':'), take the declaration path;
        // otherwise treat as nested rules.
        var save = ctx.Save();
        ctx.SkipWhitespaceAndComments();
        bool isDeclarationBlock = LooksLikeDeclaration(ctx);
        ctx.Restore(save);

        if (isDeclarationBlock)
        {
            var declarations = ParseDeclarations(ctx, expectClose: true);
            return new AtRule
            {
                Name = name,
                Prelude = prelude.Trim(),
                Declarations = declarations,
            };
        }
        var rules = ParseRules(ctx, expectClose: true);
        return new AtRule
        {
            Name = name,
            Prelude = prelude.Trim(),
            Rules = rules,
        };
    }

    private static List<Declaration> ParseDeclarations(ParseContext ctx, bool expectClose)
    {
        var decls = new List<Declaration>();
        while (!ctx.AtEnd)
        {
            ctx.SkipWhitespaceAndComments();
            if (ctx.AtEnd) break;
            if (expectClose && ctx.Peek() == '}')
            {
                ctx.Advance();
                return decls;
            }

            // Property name runs up to ':' (declaration form)
            // or '{' (nested rule, which we tolerate by
            // skipping). Bare ';' between declarations is
            // legal and just resets us to the next property.
            if (ctx.Peek() == ';') { ctx.Advance(); continue; }

            var property = ConsumeUntil(ctx, c => c is ':' or ';' or '}').Trim();
            if (ctx.AtEnd) break;
            if (ctx.Peek() != ':')
            {
                // Malformed — skip to next ';' or end.
                if (ctx.Peek() == '}' && expectClose) continue;
                if (ctx.Peek() == ';') { ctx.Advance(); continue; }
                continue;
            }
            ctx.Advance(); // consume ':'

            var value = ConsumeUntil(ctx, c => c is ';' or '}', balancedScanning: true).Trim();
            if (!ctx.AtEnd && ctx.Peek() == ';') ctx.Advance();

            if (string.IsNullOrEmpty(property)) continue;

            bool important = false;
            const string bang = "!important";
            if (value.EndsWith(bang, StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value.Substring(0, value.Length - bang.Length).Trim();
            }
            decls.Add(new Declaration
            {
                Property = property.ToLowerInvariant(),
                Value = value,
                Important = important,
            });
        }
        return decls;
    }

    /// <summary>True when the next token after the brace looks
    /// like the start of a declaration — an identifier
    /// followed (after optional whitespace) by ':'. Used to
    /// pick between nested-rules and nested-declarations
    /// modes inside a block at-rule.</summary>
    private static bool LooksLikeDeclaration(ParseContext ctx)
    {
        int p = ctx.Position;
        while (p < ctx.Source.Length)
        {
            char c = ctx.Source[p];
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_') { p++; continue; }
            // Identifier ended — next non-whitespace must be ':'
            // for this to be a declaration.
            while (p < ctx.Source.Length &&
                   char.IsWhiteSpace(ctx.Source[p])) p++;
            return p < ctx.Source.Length && ctx.Source[p] == ':';
        }
        return false;
    }

    private static string ConsumeWhile(ParseContext ctx, Func<char, bool> predicate)
    {
        int start = ctx.Position;
        while (!ctx.AtEnd && predicate(ctx.Peek())) ctx.Advance();
        return ctx.Source.Substring(start, ctx.Position - start);
    }

    /// <summary>Read characters into a string up to (but not
    /// including) the first character that satisfies
    /// <paramref name="stopAt"/>. Strings and parens / brackets
    /// are crossed atomically when
    /// <paramref name="balancedScanning"/> is true so colons
    /// or semicolons inside <c>url(...)</c>, <c>var(...)</c>,
    /// <c>"..."</c> don't terminate the run. Comments are
    /// skipped from both the cursor and the returned text.</summary>
    private static string ConsumeUntil(
        ParseContext ctx, Func<char, bool> stopAt, bool balancedScanning = false)
    {
        var sb = new System.Text.StringBuilder();
        while (!ctx.AtEnd)
        {
            char c = ctx.Peek();
            if (c == '/' && ctx.Position + 1 < ctx.Source.Length
                && ctx.Source[ctx.Position + 1] == '*')
            {
                // Comment is dropped from the accumulator but
                // the cursor moves past it. Whitespace around
                // the comment survives because the trim that
                // each caller does collapses adjacent runs
                // anyway.
                ctx.SkipBlockComment();
                continue;
            }
            if (balancedScanning && c is '"' or '\'')
            {
                int s = ctx.Position;
                ctx.SkipString();
                sb.Append(ctx.Source, s, ctx.Position - s);
                continue;
            }
            if (balancedScanning && c == '(')
            {
                int s = ctx.Position;
                ctx.SkipBalanced('(', ')');
                sb.Append(ctx.Source, s, ctx.Position - s);
                continue;
            }
            if (balancedScanning && c == '[')
            {
                int s = ctx.Position;
                ctx.SkipBalanced('[', ']');
                sb.Append(ctx.Source, s, ctx.Position - s);
                continue;
            }
            if (stopAt(c)) break;
            sb.Append(c);
            ctx.Advance();
        }
        return sb.ToString();
    }

    /// <summary>Mutable parse cursor. Inlined here rather than
    /// promoted to a public helper because nothing else in the
    /// engine needs CSS-shaped tokenization.</summary>
    private sealed class ParseContext
    {
        public string Source { get; }
        public int Position { get; private set; }
        public bool AtEnd => Position >= Source.Length;

        public ParseContext(string source) { Source = source; }

        public char Peek() => Source[Position];
        public void Advance() => Position++;

        public int Save() => Position;
        public void Restore(int p) => Position = p;

        public void SkipWhitespaceAndComments()
        {
            while (!AtEnd)
            {
                char c = Peek();
                if (char.IsWhiteSpace(c)) { Advance(); continue; }
                if (c == '/' && Position + 1 < Source.Length
                    && Source[Position + 1] == '*')
                {
                    SkipBlockComment();
                    continue;
                }
                return;
            }
        }

        public void SkipBlockComment()
        {
            Advance(); Advance(); // skip /*
            while (!AtEnd)
            {
                if (Peek() == '*' && Position + 1 < Source.Length
                    && Source[Position + 1] == '/')
                {
                    Advance(); Advance();
                    return;
                }
                Advance();
            }
        }

        public void SkipString()
        {
            char quote = Peek();
            Advance();
            while (!AtEnd)
            {
                char c = Peek();
                if (c == '\\' && Position + 1 < Source.Length)
                {
                    Advance(); Advance();
                    continue;
                }
                Advance();
                if (c == quote) return;
            }
        }

        /// <summary>Skip a balanced delimiter pair, handling
        /// nested strings and nested same-type delimiters.</summary>
        public void SkipBalanced(char open, char close)
        {
            int depth = 0;
            while (!AtEnd)
            {
                char c = Peek();
                if (c is '"' or '\'') { SkipString(); continue; }
                if (c == open) { depth++; Advance(); continue; }
                if (c == close)
                {
                    Advance();
                    if (--depth <= 0) return;
                    continue;
                }
                Advance();
            }
        }
    }
}
