namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Recursive-descent parser for ECMAScript 5 source. Produces a
/// <see cref="Program"/> <see cref="JsNode"/> tree from a
/// <see cref="JsLexer"/> token stream.
///
/// Supported today (ES5 core):
///
/// - Every ES5 statement form: <c>var</c>, <c>function</c>, <c>if</c>/<c>else</c>,
///   <c>while</c>/<c>do..while</c>, C-style <c>for</c> and <c>for..in</c>,
///   <c>break</c>/<c>continue</c>/<c>return</c>/<c>throw</c>, labeled
///   statements, <c>try</c>/<c>catch</c>/<c>finally</c>, <c>switch</c>
///   with fall-through and default, <c>with</c>, <c>debugger</c>, and
///   empty / block / expression statements.
/// - Every ES5 expression form: literals (number, string, boolean, null),
///   identifiers, <c>this</c>, array and object literals (including
///   <c>get</c>/<c>set</c> accessors and trailing commas),
///   function expressions (named and anonymous), unary prefix and
///   postfix increment/decrement, full operator precedence table with
///   right-associative assignment and ternary, member access, computed
///   member access, function calls, <c>new</c> with or without arguments,
///   and the comma operator.
/// - <b>Automatic semicolon insertion</b> (ASI). The parser tracks
///   whether each token is preceded by a line terminator and uses that
///   to decide (a) when a missing <c>;</c> can be elided and (b) when
///   restricted productions (<c>return</c>/<c>throw</c>/<c>break</c>/
///   <c>continue</c>, postfix <c>++</c>/<c>--</c>) terminate early.
/// - <b>Operator precedence.</b> The binary-operator table mirrors the
///   ES5 spec and uses precedence climbing rather than separate methods
///   per level. Right-associative operators (ternary, assignment)
///   recurse at the same precedence level; everything else is
///   left-associative.
/// - <b>The <c>in</c>-operator ambiguity.</b> <c>in</c> is a normal
///   binary operator everywhere except in the initializer clause of
///   a <c>for</c> loop, where it would collide with <c>for..in</c>.
///   Tracked via an <c>allowIn</c> flag threaded through the expression
///   hierarchy, matching the spec's NoIn productions.
///
/// Deliberately deferred:
///
/// - <b>Regex literals.</b> Distinguishing <c>/</c> as division from
///   <c>/</c> as the start of a regex requires parser context. The
///   lexer currently always lexes <c>/</c> as division; regex support
///   will grow a <c>ReLex</c> entry point when the spec-conformance
///   work in phase 3c needs it.
/// - <b>ES2015 forms.</b> The lexer recognizes <c>let</c>, <c>const</c>,
///   <c>class</c>, <c>extends</c>, <c>super</c>, <c>import</c>,
///   <c>export</c>, <c>yield</c>, arrow functions, and template
///   literals; the parser accepts <c>let</c>/<c>const</c> declarations
///   (treated as <see cref="VariableDeclarationKind.Let"/>/<see cref="VariableDeclarationKind.Const"/>
///   for future block-scoping) and rejects every other ES2015 form
///   with a descriptive error.
/// - <b>Strict mode.</b> The parser recognizes <c>"use strict"</c>
///   directives at the top of a program or function body but does
///   not currently enforce the additional restrictions (reserved
///   word usage, octal literals, duplicate parameter names). Those
///   are a semantic pass, not a syntactic one.
/// - <b>Full ASI corner cases.</b> The common cases — <c>return foo</c>
///   on one line, newline-terminated statements, postfix <c>x++</c>
///   — work. The parser does not currently re-try failed statements
///   with an inserted semicolon; if a production is ambiguous and
///   the "try without ASI first" rule changes the parse, we'll tackle
///   it then.
/// </summary>
public sealed class JsParser
{
    // Pre-lexed token stream. We buffer all tokens up front rather than
    // streaming from the lexer so that lookahead, ASI, and the in-operator
    // disambiguation all become cheap index arithmetic.
    private readonly List<JsToken> _tokens;
    // Parallel array: for each token, was it preceded by a line terminator?
    // Needed for ASI and the restricted-production rule.
    private readonly bool[] _hasLineBefore;
    private readonly string _source;
    private int _pos;
    /// <summary>
    /// True while parsing the body of a generator function.
    /// Yield expressions consult this flag to determine if
    /// they are syntactically legal. Saved/restored across
    /// nested function bodies so a plain function inside a
    /// generator does not accidentally enable yield.
    /// </summary>
    private bool _inGeneratorBody;

    /// <summary>
    /// True while parsing the body of an async function.
    /// <c>await</c> expressions consult this flag to
    /// determine if they are syntactically legal. Saved /
    /// restored across nested function bodies.
    /// </summary>
    private bool _inAsyncBody;

    public JsParser(string source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));

        var lexer = new JsLexer(_source);
        _tokens = new List<JsToken>();
        var lineFlags = new List<bool>();

        int prevEnd = 0;
        while (true)
        {
            var token = lexer.Next();
            lineFlags.Add(HasLineTerminatorIn(_source, prevEnd, token.Start));
            _tokens.Add(token);
            if (token.Kind == JsTokenKind.EndOfFile) break;
            prevEnd = token.End;
        }

        _hasLineBefore = lineFlags.ToArray();
    }

    // -------------------------------------------------------------------
    // Public entry
    // -------------------------------------------------------------------

    /// <summary>
    /// Parse the entire source text as a top-level program. Throws
    /// <see cref="JsParseException"/> on the first syntax error.
    /// </summary>
    public Program ParseProgram()
    {
        int start = Current.Start;
        var body = new List<Statement>();

        while (Current.Kind != JsTokenKind.EndOfFile)
        {
            body.Add(ParseSourceElement());
        }

        int end = Current.Start;
        return new Program(start, end, body);
    }

    // -------------------------------------------------------------------
    // Token helpers
    // -------------------------------------------------------------------

    private JsToken Current => _tokens[_pos];

    private bool HasLineBeforeCurrent => _hasLineBefore[_pos];

    private JsToken Peek(int offset = 1)
    {
        int p = _pos + offset;
        if (p >= _tokens.Count) return _tokens[_tokens.Count - 1]; // EOF
        return _tokens[p];
    }

    private JsToken Consume()
    {
        var t = _tokens[_pos];
        if (t.Kind != JsTokenKind.EndOfFile) _pos++;
        return t;
    }

    private bool Match(JsTokenKind kind)
    {
        if (Current.Kind == kind)
        {
            Consume();
            return true;
        }
        return false;
    }

    private JsToken Expect(JsTokenKind kind, string context)
    {
        if (Current.Kind != kind)
        {
            throw new JsParseException(
                $"Expected {kind} in {context}, got {Current.Kind}",
                Current.Start);
        }
        return Consume();
    }

    /// <summary>
    /// Automatic semicolon insertion. Accepts an explicit <c>;</c>,
    /// a close-brace that implicitly closes the enclosing block,
    /// end of input, or a token on a new line (ASI rule 1).
    /// </summary>
    private void ConsumeSemicolon()
    {
        if (Current.Kind == JsTokenKind.Semicolon)
        {
            Consume();
            return;
        }
        if (Current.Kind == JsTokenKind.RightBrace ||
            Current.Kind == JsTokenKind.EndOfFile ||
            HasLineBeforeCurrent)
        {
            return;
        }
        throw new JsParseException(
            $"Expected ';' but got {Current.Kind}",
            Current.Start);
    }

    private static bool HasLineTerminatorIn(string source, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            char c = source[i];
            if (c == '\n' || c == '\r' || c == '\u2028' || c == '\u2029') return true;
        }
        return false;
    }

    // -------------------------------------------------------------------
    // Statements
    // -------------------------------------------------------------------

    /// <summary>
    /// A "source element" is either a function declaration or a
    /// statement. Top-level bodies and function bodies both accept
    /// source elements, so hoisting is implicit in the AST shape.
    /// </summary>
    private Statement ParseSourceElement()
    {
        if (Current.Kind == JsTokenKind.KeywordFunction)
        {
            return ParseFunctionDeclaration();
        }
        // `async function foo() {}` — declaration form.
        if (CurrentIsAsyncKeyword() && Peek(1).Kind == JsTokenKind.KeywordFunction)
        {
            Consume(); // async
            return ParseFunctionDeclaration(isAsync: true);
        }
        if (Current.Kind == JsTokenKind.KeywordImport)
        {
            // Static `import ... from '...'` is a source
            // element; dynamic `import(specifier)` is an
            // expression that happens to start with the
            // keyword. Disambiguate by peeking.
            if (Peek(1).Kind == JsTokenKind.LeftParen)
            {
                return ParseStatement();
            }
            return ParseImportDeclaration();
        }
        if (Current.Kind == JsTokenKind.KeywordExport)
        {
            return ParseExportDeclaration();
        }
        return ParseStatement();
    }

    // -------------------------------------------------------------------
    // ES2015 modules — import / export
    // -------------------------------------------------------------------

    /// <summary>
    /// <c>import X from "mod";</c>,
    /// <c>import { a, b as c } from "mod";</c>,
    /// <c>import * as ns from "mod";</c>,
    /// <c>import X, { a } from "mod";</c>,
    /// <c>import "mod";</c> (side-effect only).
    /// </summary>
    private ImportDeclaration ParseImportDeclaration()
    {
        int start = Current.Start;
        Consume(); // import
        var specifiers = new List<ImportSpecifier>();
        bool expectFrom = false;

        // Side-effect-only form: `import "./mod";`
        if (Current.Kind == JsTokenKind.StringLiteral)
        {
            var sourceTok = Consume();
            ConsumeSemicolon();
            return new ImportDeclaration(start, sourceTok.End, specifiers, sourceTok.StringValue!);
        }

        // Default import (binding name before anything else).
        if (Current.Kind == JsTokenKind.Identifier)
        {
            var defTok = Consume();
            specifiers.Add(new ImportSpecifier(
                defTok.Start, defTok.End,
                imported: "default",
                local: defTok.StringValue!,
                isDefault: true));
            // Optionally followed by `, { ... }` or `, * as ns`.
            if (Current.Kind == JsTokenKind.Comma)
            {
                Consume();
            }
            else
            {
                expectFrom = true;
            }
        }

        if (!expectFrom)
        {
            // Namespace import: `* as ns`
            if (Current.Kind == JsTokenKind.Star)
            {
                Consume();
                ExpectContextualKeyword("as", "import namespace");
                if (Current.Kind != JsTokenKind.Identifier)
                {
                    throw new JsParseException(
                        "Expected identifier after 'as' in import",
                        Current.Start);
                }
                var nsTok = Consume();
                specifiers.Add(new ImportSpecifier(
                    nsTok.Start, nsTok.End,
                    imported: "*",
                    local: nsTok.StringValue!,
                    isNamespace: true));
            }
            // Named-imports list: `{ a, b as c }`
            else if (Current.Kind == JsTokenKind.LeftBrace)
            {
                Consume();
                while (Current.Kind != JsTokenKind.RightBrace &&
                       Current.Kind != JsTokenKind.EndOfFile)
                {
                    if (Current.Kind != JsTokenKind.Identifier)
                    {
                        throw new JsParseException(
                            $"Expected identifier in named import, got {Current.Kind}",
                            Current.Start);
                    }
                    var importedTok = Consume();
                    string local = importedTok.StringValue!;
                    if (Current.Kind == JsTokenKind.Identifier &&
                        Current.StringValue == "as")
                    {
                        Consume();
                        if (Current.Kind != JsTokenKind.Identifier)
                        {
                            throw new JsParseException(
                                "Expected identifier after 'as' in import",
                                Current.Start);
                        }
                        var aliasTok = Consume();
                        local = aliasTok.StringValue!;
                    }
                    specifiers.Add(new ImportSpecifier(
                        importedTok.Start, importedTok.End,
                        imported: importedTok.StringValue!,
                        local: local));
                    if (Current.Kind == JsTokenKind.Comma) Consume();
                    else break;
                }
                Expect(JsTokenKind.RightBrace, "named imports");
            }
        }

        ExpectContextualKeyword("from", "import");
        if (Current.Kind != JsTokenKind.StringLiteral)
        {
            throw new JsParseException(
                "Expected module specifier string after 'from'",
                Current.Start);
        }
        var srcTok = Consume();
        int end = srcTok.End;
        ConsumeSemicolon();
        return new ImportDeclaration(start, end, specifiers, srcTok.StringValue!);
    }

    private void ExpectContextualKeyword(string keyword, string context)
    {
        if (Current.Kind != JsTokenKind.Identifier ||
            Current.StringValue != keyword)
        {
            throw new JsParseException(
                $"Expected '{keyword}' in {context}, got {Current.Kind}",
                Current.Start);
        }
        Consume();
    }

    /// <summary>
    /// <c>export const x = 1;</c>, <c>export function f() {}</c>,
    /// <c>export class C {}</c>, <c>export { a, b as c };</c>, or
    /// <c>export default expr;</c>. Re-export forms
    /// (<c>export * from ...</c>, <c>export { a } from ...</c>)
    /// are not yet supported.
    /// </summary>
    private Statement ParseExportDeclaration()
    {
        int start = Current.Start;
        Consume(); // export

        // Default export
        if (Current.Kind == JsTokenKind.KeywordDefault)
        {
            Consume();
            JsNode payload;
            if (Current.Kind == JsTokenKind.KeywordFunction)
            {
                // `export default function X()` is a named
                // declaration; `export default function ()`
                // is an anonymous function expression — in
                // both cases the exported value is the
                // function itself. If the next token after
                // `function` is a bare `(`, dispatch to the
                // expression form.
                if (Peek(1).Kind == JsTokenKind.LeftParen ||
                    Peek(1).Kind == JsTokenKind.Star && Peek(2).Kind == JsTokenKind.LeftParen)
                {
                    payload = ParseFunctionExpression();
                }
                else
                {
                    payload = ParseFunctionDeclaration();
                }
            }
            else if (Current.Kind == JsTokenKind.KeywordClass)
            {
                // Anonymous class: `export default class {}`
                if (Peek(1).Kind == JsTokenKind.LeftBrace ||
                    Peek(1).Kind == JsTokenKind.KeywordExtends)
                {
                    payload = ParseClassExpression();
                }
                else
                {
                    payload = ParseClassDeclaration();
                }
            }
            else
            {
                var expr = ParseAssignmentExpression(allowIn: true);
                ConsumeSemicolon();
                payload = expr;
            }
            return new ExportDefaultDeclaration(start, payload.End, payload);
        }

        // `export * from '...'` or `export * as ns from '...'`.
        if (Current.Kind == JsTokenKind.Star)
        {
            Consume();
            string? nsName = null;
            if (Current.Kind == JsTokenKind.Identifier && Current.StringValue == "as")
            {
                Consume();
                if (Current.Kind != JsTokenKind.Identifier)
                {
                    throw new JsParseException(
                        "Expected identifier after 'as' in export",
                        Current.Start);
                }
                var nsTok = Consume();
                nsName = nsTok.StringValue!;
            }
            ExpectContextualKeyword("from", "export *");
            if (Current.Kind != JsTokenKind.StringLiteral)
            {
                throw new JsParseException(
                    "Expected module specifier after 'from'",
                    Current.Start);
            }
            var srcTok = Consume();
            int starEnd = srcTok.End;
            ConsumeSemicolon();
            return new ExportAllDeclaration(start, starEnd, srcTok.StringValue!, nsName);
        }

        // `export { a, b as c };` specifier list (optionally
        // `export { ... } from './mod'`).
        if (Current.Kind == JsTokenKind.LeftBrace)
        {
            Consume();
            var specs = new List<ExportSpecifier>();
            while (Current.Kind != JsTokenKind.RightBrace &&
                   Current.Kind != JsTokenKind.EndOfFile)
            {
                // `default` is a valid source name in a
                // re-export: `export { default as x } from '...'`.
                string localName;
                int localStart, localEnd;
                if (Current.Kind == JsTokenKind.KeywordDefault)
                {
                    var defTok = Consume();
                    localName = "default";
                    localStart = defTok.Start;
                    localEnd = defTok.End;
                }
                else if (Current.Kind == JsTokenKind.Identifier)
                {
                    var localTok = Consume();
                    localName = localTok.StringValue!;
                    localStart = localTok.Start;
                    localEnd = localTok.End;
                }
                else
                {
                    throw new JsParseException(
                        $"Expected identifier in export list, got {Current.Kind}",
                        Current.Start);
                }
                string exported = localName;
                if (Current.Kind == JsTokenKind.Identifier &&
                    Current.StringValue == "as")
                {
                    Consume();
                    if (Current.Kind != JsTokenKind.Identifier)
                    {
                        throw new JsParseException(
                            "Expected identifier after 'as' in export",
                            Current.Start);
                    }
                    var aliasTok = Consume();
                    exported = aliasTok.StringValue!;
                }
                specs.Add(new ExportSpecifier(
                    localStart, localEnd,
                    local: localName,
                    exported: exported));
                if (Current.Kind == JsTokenKind.Comma) Consume();
                else break;
            }
            int braceEnd = Current.End;
            Expect(JsTokenKind.RightBrace, "export specifiers");

            // Optional re-export source: `export { ... } from './mod'`.
            string? source = null;
            if (Current.Kind == JsTokenKind.Identifier && Current.StringValue == "from")
            {
                Consume();
                if (Current.Kind != JsTokenKind.StringLiteral)
                {
                    throw new JsParseException(
                        "Expected module specifier after 'from'",
                        Current.Start);
                }
                var srcTok = Consume();
                source = srcTok.StringValue!;
                braceEnd = srcTok.End;
            }

            ConsumeSemicolon();
            return new ExportNamedDeclaration(start, braceEnd, declaration: null, specs, source);
        }

        // `export <declaration>`: var, let, const, function, class
        Statement decl;
        if (Current.Kind == JsTokenKind.KeywordVar ||
            Current.Kind == JsTokenKind.KeywordLet ||
            Current.Kind == JsTokenKind.KeywordConst)
        {
            decl = ParseVariableStatement();
        }
        else if (Current.Kind == JsTokenKind.KeywordFunction)
        {
            decl = ParseFunctionDeclaration();
        }
        else if (Current.Kind == JsTokenKind.KeywordClass)
        {
            decl = ParseClassDeclaration();
        }
        else
        {
            throw new JsParseException(
                $"Unsupported export form starting with {Current.Kind}",
                Current.Start);
        }
        return new ExportNamedDeclaration(
            start,
            decl.End,
            declaration: decl,
            specifiers: new List<ExportSpecifier>());
    }

    private Statement ParseStatement()
    {
        switch (Current.Kind)
        {
            case JsTokenKind.LeftBrace:     return ParseBlockStatement();
            case JsTokenKind.Semicolon:     return ParseEmptyStatement();
            case JsTokenKind.KeywordVar:
            case JsTokenKind.KeywordLet:
            case JsTokenKind.KeywordConst:  return ParseVariableStatement();
            case JsTokenKind.KeywordIf:     return ParseIfStatement();
            case JsTokenKind.KeywordWhile:  return ParseWhileStatement();
            case JsTokenKind.KeywordDo:     return ParseDoWhileStatement();
            case JsTokenKind.KeywordFor:    return ParseForStatement();
            case JsTokenKind.KeywordBreak:  return ParseBreakStatement();
            case JsTokenKind.KeywordContinue: return ParseContinueStatement();
            case JsTokenKind.KeywordReturn: return ParseReturnStatement();
            case JsTokenKind.KeywordThrow:  return ParseThrowStatement();
            case JsTokenKind.KeywordTry:    return ParseTryStatement();
            case JsTokenKind.KeywordSwitch: return ParseSwitchStatement();
            case JsTokenKind.KeywordWith:   return ParseWithStatement();
            case JsTokenKind.KeywordDebugger: return ParseDebuggerStatement();
            case JsTokenKind.KeywordFunction:
                // Function declarations are hoisted source elements —
                // appearing inside a block is non-standard but every
                // browser accepts it and treats it as a declaration.
                return ParseFunctionDeclaration();
            case JsTokenKind.KeywordClass:
                return ParseClassDeclaration();
        }

        // `async function foo() {}` at statement position is a
        // function declaration, same as its non-async form.
        if (CurrentIsAsyncKeyword() && Peek(1).Kind == JsTokenKind.KeywordFunction)
        {
            Consume(); // async
            return ParseFunctionDeclaration(isAsync: true);
        }

        // Labeled statement? Peek past the identifier for a ':'.
        if (Current.Kind == JsTokenKind.Identifier && Peek(1).Kind == JsTokenKind.Colon)
        {
            return ParseLabeledStatement();
        }

        return ParseExpressionStatement();
    }

    private BlockStatement ParseBlockStatement()
    {
        int start = Current.Start;
        Expect(JsTokenKind.LeftBrace, "block");
        var body = new List<Statement>();
        while (Current.Kind != JsTokenKind.RightBrace && Current.Kind != JsTokenKind.EndOfFile)
        {
            body.Add(ParseSourceElement());
        }
        int end = Current.End;
        Expect(JsTokenKind.RightBrace, "block");
        return new BlockStatement(start, end, body);
    }

    private EmptyStatement ParseEmptyStatement()
    {
        var tok = Consume();
        return new EmptyStatement(tok.Start, tok.End);
    }

    private DebuggerStatement ParseDebuggerStatement()
    {
        int start = Current.Start;
        Consume(); // debugger
        int end = Current.Start;
        ConsumeSemicolon();
        return new DebuggerStatement(start, end);
    }

    private VariableDeclaration ParseVariableStatement()
    {
        var decl = ParseVariableDeclaration(allowIn: true);
        ConsumeSemicolon();
        return decl;
    }

    /// <summary>
    /// Parse a <c>var</c>/<c>let</c>/<c>const</c> declaration without
    /// consuming a trailing semicolon — used for both statement form
    /// and <c>for (var x ...</c> loop headers.
    /// </summary>
    private VariableDeclaration ParseVariableDeclaration(bool allowIn, bool allowMissingInit = false)
    {
        int start = Current.Start;
        VariableDeclarationKind kind = Current.Kind switch
        {
            JsTokenKind.KeywordVar => VariableDeclarationKind.Var,
            JsTokenKind.KeywordLet => VariableDeclarationKind.Let,
            JsTokenKind.KeywordConst => VariableDeclarationKind.Const,
            _ => throw new JsParseException(
                $"Expected 'var', 'let', or 'const', got {Current.Kind}",
                Current.Start),
        };
        Consume();

        var decls = new List<VariableDeclarator>();
        decls.Add(ParseVariableDeclarator(allowIn, allowMissingInit));
        while (Match(JsTokenKind.Comma))
        {
            decls.Add(ParseVariableDeclarator(allowIn, allowMissingInit));
        }
        int end = decls[decls.Count - 1].End;
        return new VariableDeclaration(start, end, kind, decls);
    }

    private VariableDeclarator ParseVariableDeclarator(bool allowIn, bool allowMissingInit)
    {
        var id = ParseBindingTarget();
        Expression? init = null;
        if (Match(JsTokenKind.Assign))
        {
            init = ParseAssignmentExpression(allowIn);
        }
        else if (id is not Identifier && !allowMissingInit)
        {
            // ES2015: BindingPattern in a VariableDeclaration
            // requires an initializer in the normal form. The
            // for-in / for-of heads are the only exception —
            // the iterator supplies each step's value.
            throw new JsParseException(
                "Destructuring declaration requires an initializer",
                id.Start);
        }
        int end = init?.End ?? id.End;
        return new VariableDeclarator(id.Start, end, id, init);
    }

    private IfStatement ParseIfStatement()
    {
        int start = Current.Start;
        Consume(); // if
        Expect(JsTokenKind.LeftParen, "if");
        var test = ParseExpression(allowIn: true);
        Expect(JsTokenKind.RightParen, "if");
        var consequent = ParseStatement();
        Statement? alternate = null;
        if (Match(JsTokenKind.KeywordElse))
        {
            alternate = ParseStatement();
        }
        int end = (alternate ?? consequent).End;
        return new IfStatement(start, end, test, consequent, alternate);
    }

    private WhileStatement ParseWhileStatement()
    {
        int start = Current.Start;
        Consume(); // while
        Expect(JsTokenKind.LeftParen, "while");
        var test = ParseExpression(allowIn: true);
        Expect(JsTokenKind.RightParen, "while");
        var body = ParseStatement();
        return new WhileStatement(start, body.End, test, body);
    }

    private DoWhileStatement ParseDoWhileStatement()
    {
        int start = Current.Start;
        Consume(); // do
        var body = ParseStatement();
        Expect(JsTokenKind.KeywordWhile, "do-while");
        Expect(JsTokenKind.LeftParen, "do-while");
        var test = ParseExpression(allowIn: true);
        int end = Current.End;
        Expect(JsTokenKind.RightParen, "do-while");
        // Trailing semicolon is optional and semi-insertable even
        // when the next token could otherwise start a new statement,
        // per the spec's special case for do-while.
        if (Current.Kind == JsTokenKind.Semicolon) Consume();
        return new DoWhileStatement(start, end, body, test);
    }

    /// <summary>
    /// Parse a <c>for</c> loop. The tricky part is deciding whether
    /// the init clause starts a C-style loop or a <c>for..in</c>
    /// loop — we parse the init with <c>allowIn = false</c> so an
    /// <c>in</c> token is not swallowed as a binary operator, then
    /// check which flavor this is.
    /// </summary>
    private Statement ParseForStatement()
    {
        int start = Current.Start;
        Consume(); // for
        Expect(JsTokenKind.LeftParen, "for");

        JsNode? init = null;
        if (Current.Kind != JsTokenKind.Semicolon)
        {
            if (Current.Kind == JsTokenKind.KeywordVar ||
                Current.Kind == JsTokenKind.KeywordLet ||
                Current.Kind == JsTokenKind.KeywordConst)
            {
                // for-in / for-of heads allow
                // `for (var {a} of x)` with no initializer
                // on the declarator — the iterator supplies
                // the value at each step.
                init = ParseVariableDeclaration(allowIn: false, allowMissingInit: true);
            }
            else
            {
                init = ParseExpression(allowIn: false);
            }
        }

        // for..in form?
        if (Current.Kind == JsTokenKind.KeywordIn)
        {
            if (init is null)
            {
                throw new JsParseException("'for..in' requires an initializer", Current.Start);
            }
            if (init is VariableDeclaration vd && vd.Declarations.Count > 1)
            {
                throw new JsParseException(
                    "'for..in' variable declaration must have exactly one binding",
                    vd.Start);
            }
            if (init is Expression expr && !IsValidLhs(expr))
            {
                throw new JsParseException(
                    "Invalid left-hand side in 'for..in'",
                    expr.Start);
            }
            Consume(); // in
            var right = ParseExpression(allowIn: true);
            Expect(JsTokenKind.RightParen, "for-in");
            var body = ParseStatement();
            return new ForInStatement(start, body.End, init, right, body);
        }

        // for..of form? `of` is a contextual keyword (it is
        // tokenized as an ordinary identifier); accept it
        // only when it follows a valid head init.
        if (init is not null &&
            Current.Kind == JsTokenKind.Identifier &&
            Current.StringValue == "of")
        {
            if (init is VariableDeclaration vdOf && vdOf.Declarations.Count > 1)
            {
                throw new JsParseException(
                    "'for..of' variable declaration must have exactly one binding",
                    vdOf.Start);
            }
            if (init is Expression ofExpr && !IsValidLhs(ofExpr))
            {
                throw new JsParseException(
                    "Invalid left-hand side in 'for..of'",
                    ofExpr.Start);
            }
            Consume(); // of
            var rightOf = ParseAssignmentExpression(allowIn: true);
            Expect(JsTokenKind.RightParen, "for-of");
            var bodyOf = ParseStatement();
            return new ForOfStatement(start, bodyOf.End, init, rightOf, bodyOf);
        }

        Expect(JsTokenKind.Semicolon, "for");
        Expression? test = null;
        if (Current.Kind != JsTokenKind.Semicolon)
        {
            test = ParseExpression(allowIn: true);
        }
        Expect(JsTokenKind.Semicolon, "for");
        Expression? update = null;
        if (Current.Kind != JsTokenKind.RightParen)
        {
            update = ParseExpression(allowIn: true);
        }
        Expect(JsTokenKind.RightParen, "for");
        var cBody = ParseStatement();
        return new ForStatement(start, cBody.End, init, test, update, cBody);
    }

    private BreakStatement ParseBreakStatement()
    {
        int start = Current.Start;
        Consume(); // break
        Identifier? label = null;
        // Restricted production: label must be on the same line.
        if (Current.Kind == JsTokenKind.Identifier && !HasLineBeforeCurrent)
        {
            label = ParseBindingIdentifier();
        }
        int end = (label?.End) ?? start + "break".Length;
        ConsumeSemicolon();
        return new BreakStatement(start, end, label);
    }

    private ContinueStatement ParseContinueStatement()
    {
        int start = Current.Start;
        Consume(); // continue
        Identifier? label = null;
        if (Current.Kind == JsTokenKind.Identifier && !HasLineBeforeCurrent)
        {
            label = ParseBindingIdentifier();
        }
        int end = (label?.End) ?? start + "continue".Length;
        ConsumeSemicolon();
        return new ContinueStatement(start, end, label);
    }

    private ReturnStatement ParseReturnStatement()
    {
        int start = Current.Start;
        Consume(); // return
        Expression? arg = null;
        // Restricted production: if the expression starts on a new
        // line, `return` takes no argument (ASI inserts a semicolon).
        if (!HasLineBeforeCurrent &&
            Current.Kind != JsTokenKind.Semicolon &&
            Current.Kind != JsTokenKind.RightBrace &&
            Current.Kind != JsTokenKind.EndOfFile)
        {
            arg = ParseExpression(allowIn: true);
        }
        int end = arg?.End ?? start + "return".Length;
        ConsumeSemicolon();
        return new ReturnStatement(start, end, arg);
    }

    private ThrowStatement ParseThrowStatement()
    {
        int start = Current.Start;
        Consume(); // throw
        if (HasLineBeforeCurrent)
        {
            throw new JsParseException(
                "Illegal newline after 'throw'",
                Current.Start);
        }
        var arg = ParseExpression(allowIn: true);
        ConsumeSemicolon();
        return new ThrowStatement(start, arg.End, arg);
    }

    private TryStatement ParseTryStatement()
    {
        int start = Current.Start;
        Consume(); // try
        var block = ParseBlockStatement();
        CatchClause? handler = null;
        BlockStatement? finalizer = null;

        if (Current.Kind == JsTokenKind.KeywordCatch)
        {
            int cStart = Current.Start;
            Consume(); // catch
            Expect(JsTokenKind.LeftParen, "catch");
            var param = ParseBindingIdentifier();
            Expect(JsTokenKind.RightParen, "catch");
            var body = ParseBlockStatement();
            handler = new CatchClause(cStart, body.End, param, body);
        }
        if (Match(JsTokenKind.KeywordFinally))
        {
            finalizer = ParseBlockStatement();
        }
        if (handler is null && finalizer is null)
        {
            throw new JsParseException(
                "'try' must be followed by 'catch' or 'finally'",
                Current.Start);
        }
        int end = (finalizer?.End) ?? (handler?.End) ?? block.End;
        return new TryStatement(start, end, block, handler, finalizer);
    }

    private SwitchStatement ParseSwitchStatement()
    {
        int start = Current.Start;
        Consume(); // switch
        Expect(JsTokenKind.LeftParen, "switch");
        var discriminant = ParseExpression(allowIn: true);
        Expect(JsTokenKind.RightParen, "switch");
        Expect(JsTokenKind.LeftBrace, "switch");

        var cases = new List<SwitchCase>();
        bool sawDefault = false;
        while (Current.Kind != JsTokenKind.RightBrace && Current.Kind != JsTokenKind.EndOfFile)
        {
            int cStart = Current.Start;
            Expression? test;
            if (Current.Kind == JsTokenKind.KeywordCase)
            {
                Consume();
                test = ParseExpression(allowIn: true);
            }
            else if (Current.Kind == JsTokenKind.KeywordDefault)
            {
                if (sawDefault)
                {
                    throw new JsParseException(
                        "Duplicate 'default' clause in switch",
                        Current.Start);
                }
                sawDefault = true;
                Consume();
                test = null;
            }
            else
            {
                throw new JsParseException(
                    $"Expected 'case' or 'default', got {Current.Kind}",
                    Current.Start);
            }
            Expect(JsTokenKind.Colon, "switch case");
            var consequent = new List<Statement>();
            while (Current.Kind != JsTokenKind.KeywordCase &&
                   Current.Kind != JsTokenKind.KeywordDefault &&
                   Current.Kind != JsTokenKind.RightBrace &&
                   Current.Kind != JsTokenKind.EndOfFile)
            {
                consequent.Add(ParseSourceElement());
            }
            int cEnd = consequent.Count > 0 ? consequent[consequent.Count - 1].End : Current.Start;
            cases.Add(new SwitchCase(cStart, cEnd, test, consequent));
        }
        int end = Current.End;
        Expect(JsTokenKind.RightBrace, "switch");
        return new SwitchStatement(start, end, discriminant, cases);
    }

    private WithStatement ParseWithStatement()
    {
        int start = Current.Start;
        Consume(); // with
        Expect(JsTokenKind.LeftParen, "with");
        var obj = ParseExpression(allowIn: true);
        Expect(JsTokenKind.RightParen, "with");
        var body = ParseStatement();
        return new WithStatement(start, body.End, obj, body);
    }

    private LabeledStatement ParseLabeledStatement()
    {
        var label = ParseBindingIdentifier();
        Expect(JsTokenKind.Colon, "labeled statement");
        var body = ParseStatement();
        return new LabeledStatement(label.Start, body.End, label, body);
    }

    private ExpressionStatement ParseExpressionStatement()
    {
        int start = Current.Start;
        var expr = ParseExpression(allowIn: true);
        int end = expr.End;
        ConsumeSemicolon();
        return new ExpressionStatement(start, end, expr);
    }

    // -------------------------------------------------------------------
    // Functions
    // -------------------------------------------------------------------

    private FunctionDeclaration ParseFunctionDeclaration(bool isAsync = false)
    {
        int start = Current.Start;
        Consume(); // function
        bool isGenerator = Match(JsTokenKind.Star);
        var id = ParseBindingIdentifier();
        var paramList = ParseFormalParameters();
        var body = ParseFunctionBody(insideGenerator: isGenerator, insideAsync: isAsync);
        return new FunctionDeclaration(start, body.End, id, paramList, body, isGenerator, isAsync);
    }

    private FunctionExpression ParseFunctionExpression(bool isAsync = false)
    {
        int start = Current.Start;
        Consume(); // function
        bool isGenerator = Match(JsTokenKind.Star);
        Identifier? id = null;
        if (Current.Kind == JsTokenKind.Identifier)
        {
            id = ParseBindingIdentifier();
        }
        var paramList = ParseFormalParameters();
        var body = ParseFunctionBody(insideGenerator: isGenerator, insideAsync: isAsync);
        return new FunctionExpression(start, body.End, id, paramList, body, isGenerator, isAsync);
    }

    /// <summary>
    /// Is the current token an <c>async</c> keyword followed
    /// by something that makes this the start of an async
    /// function / arrow expression? We accept:
    /// <list type="bullet">
    /// <item><c>async function ...</c> — async function decl / expr</item>
    /// <item><c>async (</c> — async arrow with paren params
    ///   (we don't yet confirm there's a <c>=&gt;</c> — that
    ///   is done by the arrow-scan lookahead on the caller
    ///   side).</item>
    /// <item><c>async identifier =&gt;</c> — async single-ident
    ///   arrow.</item>
    /// </list>
    /// Since <c>async</c> is a contextual keyword (lexed as
    /// a plain <see cref="JsTokenKind.Identifier"/>), the
    /// parser has to detect it by looking at the string
    /// value and peeking ahead.
    /// </summary>
    private bool CurrentIsAsyncKeyword()
    {
        return Current.Kind == JsTokenKind.Identifier
            && Current.StringValue == "async"
            && !_hasLineBefore[_pos + 1]; // no LineTerminator between `async` and the follower
    }

    private List<FunctionParameter> ParseFormalParameters()
    {
        Expect(JsTokenKind.LeftParen, "parameter list");
        var list = new List<FunctionParameter>();
        if (Current.Kind != JsTokenKind.RightParen)
        {
            list.Add(ParseFunctionParameter());
            while (Match(JsTokenKind.Comma))
            {
                // A rest parameter must be the last entry — disallow
                // a trailing comma after one or another param after it.
                if (list[list.Count - 1].IsRest)
                {
                    throw new JsParseException(
                        "Rest parameter must be the last formal parameter",
                        Current.Start);
                }
                list.Add(ParseFunctionParameter());
            }
        }
        Expect(JsTokenKind.RightParen, "parameter list");
        return list;
    }

    /// <summary>
    /// One formal parameter, which may carry a default
    /// (<c>x = 1</c>) or be a rest parameter (<c>...args</c>).
    /// Rest params are identifier-only in this slice (pattern
    /// rest params and parameter destructuring are deferred).
    /// </summary>
    private FunctionParameter ParseFunctionParameter()
    {
        int start = Current.Start;
        if (Current.Kind == JsTokenKind.DotDotDot)
        {
            Consume();
            var restTarget = ParseBindingIdentifier();
            if (Current.Kind == JsTokenKind.Assign)
            {
                throw new JsParseException(
                    "Rest parameter may not have a default value",
                    Current.Start);
            }
            return new FunctionParameter(start, restTarget.End, restTarget, null, isRest: true);
        }
        // ES2015: a formal parameter can be any binding target —
        // an identifier, an object pattern, or an array pattern.
        // This lets users write `function f({a, b})` or
        // `function f([x, y, z])`.
        var target = ParseBindingTarget();
        Expression? @default = null;
        if (Match(JsTokenKind.Assign))
        {
            @default = ParseAssignmentExpression(allowIn: true);
        }
        int end = @default?.End ?? target.End;
        return new FunctionParameter(start, end, target, @default, isRest: false);
    }

    private BlockStatement ParseFunctionBody(bool insideGenerator = false, bool insideAsync = false)
    {
        // Function bodies accept source elements (nested function
        // declarations are hoisted), which ParseBlockStatement already
        // handles via ParseSourceElement. Save/restore the
        // generator- and async-body flags so yield / await are
        // only legal in the innermost enclosing matching function,
        // not leaked into nested function expressions.
        bool savedGen = _inGeneratorBody;
        bool savedAsync = _inAsyncBody;
        _inGeneratorBody = insideGenerator;
        _inAsyncBody = insideAsync;
        try
        {
            return ParseBlockStatement();
        }
        finally
        {
            _inGeneratorBody = savedGen;
            _inAsyncBody = savedAsync;
        }
    }

    private Identifier ParseBindingIdentifier()
    {
        if (Current.Kind != JsTokenKind.Identifier)
        {
            throw new JsParseException(
                $"Expected identifier, got {Current.Kind}",
                Current.Start);
        }
        var tok = Consume();
        return new Identifier(tok.Start, tok.End, tok.StringValue!);
    }

    // -------------------------------------------------------------------
    // Classes (ES2015)
    // -------------------------------------------------------------------

    /// <summary>
    /// <c>class Name [extends Expr] { body }</c> declaration.
    /// The binding is block-scoped like <c>let</c>.
    /// </summary>
    private ClassDeclaration ParseClassDeclaration()
    {
        int start = Current.Start;
        Consume(); // class
        var id = ParseBindingIdentifier();
        Expression? superClass = null;
        if (Match(JsTokenKind.KeywordExtends))
        {
            superClass = ParseLeftHandSideExpression();
        }
        var body = ParseClassBody();
        return new ClassDeclaration(start, body.End, id, superClass, body);
    }

    /// <summary>
    /// Class expression form — <c>class [Name] [extends Expr]
    /// { body }</c>. The name is optional; the parser stores it
    /// but this slice treats it as anonymous for self-reference
    /// purposes.
    /// </summary>
    private ClassExpression ParseClassExpression()
    {
        int start = Current.Start;
        Consume(); // class
        Identifier? id = null;
        if (Current.Kind == JsTokenKind.Identifier)
        {
            id = ParseBindingIdentifier();
        }
        Expression? superClass = null;
        if (Match(JsTokenKind.KeywordExtends))
        {
            superClass = ParseLeftHandSideExpression();
        }
        var body = ParseClassBody();
        return new ClassExpression(start, body.End, id, superClass, body);
    }

    private ClassBody ParseClassBody()
    {
        int start = Current.Start;
        Expect(JsTokenKind.LeftBrace, "class body");
        var methods = new List<MethodDefinition>();
        var fields = new List<ClassField>();
        while (Current.Kind != JsTokenKind.RightBrace && Current.Kind != JsTokenKind.EndOfFile)
        {
            if (Current.Kind == JsTokenKind.Semicolon)
            {
                // Empty class-element per spec.
                Consume();
                continue;
            }
            // ES2022 class field: `[static] name [= expr];`.
            // Differentiated from a method by looking at the
            // token after the name: a field has `=`, `;`, or
            // `}`, a method has `(`.
            if (TryParseClassField(out var field))
            {
                fields.Add(field);
                continue;
            }
            methods.Add(ParseMethodDefinition());
        }
        int end = Current.End;
        Expect(JsTokenKind.RightBrace, "class body");
        return new ClassBody(start, end, methods, fields);
    }

    private bool TryParseClassField(out ClassField field)
    {
        field = default!;
        // Peek ahead to see if this is a field form. A field
        // is: [static] IDENTIFIER [= expression] ;
        // A method is: [static] IDENTIFIER ( ...
        int save = _pos;
        bool isStatic = false;
        if (Current.Kind == JsTokenKind.KeywordStatic &&
            Peek(1).Kind != JsTokenKind.LeftParen)
        {
            // Peek past `static` to decide.
            if (Peek(1).Kind != JsTokenKind.Identifier) return false;
            var afterName = Peek(2).Kind;
            if (afterName != JsTokenKind.Assign &&
                afterName != JsTokenKind.Semicolon &&
                afterName != JsTokenKind.RightBrace)
            {
                return false;
            }
            Consume(); // static
            isStatic = true;
        }
        else
        {
            // Non-static fields aren't supported yet —
            // instance fields need constructor-time init.
            return false;
        }

        if (Current.Kind != JsTokenKind.Identifier)
        {
            _pos = save;
            return false;
        }
        int fieldStart = Current.Start;
        var nameTok = Consume();
        string name = nameTok.StringValue!;
        Expression? init = null;
        if (Match(JsTokenKind.Assign))
        {
            init = ParseAssignmentExpression(allowIn: true);
        }
        int fieldEnd = init?.End ?? nameTok.End;
        // Field declarations are semicolon-terminated
        // (semicolon is optional if followed by `}` or EOL).
        if (Current.Kind == JsTokenKind.Semicolon) Consume();
        field = new ClassField(fieldStart, fieldEnd, name, init, isStatic);
        return true;
    }

    /// <summary>
    /// <c>[static] name(params) { body }</c>. The
    /// <c>constructor</c> name produces a
    /// <see cref="MethodDefinitionKind.Constructor"/> entry;
    /// everything else is a regular method. Getters, setters,
    /// computed keys, async, and generator methods are
    /// deferred.
    /// </summary>
    private MethodDefinition ParseMethodDefinition()
    {
        int start = Current.Start;
        bool isStatic = false;
        // `static` is an ES2015 future-reserved keyword; in
        // class body position it's a modifier. A method
        // literally named "static" has `static` followed by
        // `(` — disambiguate via lookahead.
        if (Current.Kind == JsTokenKind.KeywordStatic &&
            Peek(1).Kind != JsTokenKind.LeftParen)
        {
            Consume();
            isStatic = true;
        }

        // ES2015 accessor methods: `get name() {}` /
        // `set name(v) {}`. Contextual keywords — detected by
        // matching `get` / `set` followed by an identifier-
        // style name and then `(`. A method literally named
        // `get` / `set` has the paren directly after, not
        // another name.
        MethodDefinitionKind accessorKind = MethodDefinitionKind.Method;
        if (Current.Kind == JsTokenKind.Identifier &&
            (Current.StringValue == "get" || Current.StringValue == "set") &&
            Peek(1).Kind == JsTokenKind.Identifier &&
            Peek(2).Kind == JsTokenKind.LeftParen)
        {
            accessorKind = Current.StringValue == "get"
                ? MethodDefinitionKind.Get
                : MethodDefinitionKind.Set;
            Consume();
        }

        // ES2017 async method: `async name(...) { ... }`.
        // Same contextual-keyword pattern as other `async`
        // recognition sites — consume only if it's followed
        // by an identifier and not a paren (so a method
        // literally named `async` still works). Mutually
        // exclusive with get/set — an accessor can't also be
        // async in this slice.
        bool isAsync = false;
        if (accessorKind == MethodDefinitionKind.Method &&
            Current.Kind == JsTokenKind.Identifier &&
            Current.StringValue == "async" &&
            Peek(1).Kind == JsTokenKind.Identifier)
        {
            Consume();
            isAsync = true;
        }

        // Method name — accept a plain Identifier, or a
        // contextual keyword that would otherwise be reserved
        // (like "static" standalone as a method name fallback).
        string methodName;
        int keyStart = Current.Start;
        int keyEnd = Current.End;
        if (Current.Kind == JsTokenKind.Identifier)
        {
            var tok = Consume();
            methodName = tok.StringValue!;
        }
        else
        {
            throw new JsParseException(
                $"Expected method name, got {Current.Kind}",
                Current.Start);
        }
        var key = new Identifier(keyStart, keyEnd, methodName);

        var paramList = ParseFormalParameters();
        var body = ParseFunctionBody(insideAsync: isAsync);

        var fnExpr = new FunctionExpression(
            start: key.Start,
            end: body.End,
            id: null,
            @params: paramList,
            body: body,
            isGenerator: false,
            isAsync: isAsync);

        MethodDefinitionKind kind;
        if (accessorKind != MethodDefinitionKind.Method)
        {
            kind = accessorKind;
        }
        else if (!isStatic && key.Name == "constructor")
        {
            kind = MethodDefinitionKind.Constructor;
        }
        else
        {
            kind = MethodDefinitionKind.Method;
        }
        return new MethodDefinition(start, body.End, key, fnExpr, kind, isStatic);
    }

    /// <summary>
    /// Binding target — either a simple identifier or a
    /// destructuring pattern (<c>{...}</c> or <c>[...]</c>).
    /// Returned as a <see cref="JsNode"/> because the three
    /// concrete node types don't share a closer common base.
    /// </summary>
    private JsNode ParseBindingTarget()
    {
        return Current.Kind switch
        {
            JsTokenKind.LeftBrace => ParseObjectPattern(),
            JsTokenKind.LeftBracket => ParseArrayPattern(),
            _ => ParseBindingIdentifier(),
        };
    }

    /// <summary>
    /// <c>{ a, b: x, c = 1, d: { e } = def }</c>. Shorthand
    /// (<c>{a}</c>) becomes a property whose Key equals its
    /// target name. Defaults are stored on the property, not
    /// on the inner target, so that nested patterns can still
    /// be destructured against the resolved value.
    /// </summary>
    private ObjectPattern ParseObjectPattern()
    {
        int start = Current.Start;
        Expect(JsTokenKind.LeftBrace, "object pattern");
        var props = new List<ObjectPatternProperty>();
        bool sawRest = false;
        if (Current.Kind != JsTokenKind.RightBrace)
        {
            props.Add(ParseObjectPatternProperty());
            if (props[props.Count - 1].IsRest) sawRest = true;
            while (Match(JsTokenKind.Comma))
            {
                // Trailing comma allowed.
                if (Current.Kind == JsTokenKind.RightBrace) break;
                if (sawRest)
                {
                    throw new JsParseException(
                        "Rest element must be the last element of an object pattern",
                        Current.Start);
                }
                var next = ParseObjectPatternProperty();
                props.Add(next);
                if (next.IsRest) sawRest = true;
            }
        }
        int end = Current.End;
        Expect(JsTokenKind.RightBrace, "object pattern");
        return new ObjectPattern(start, end, props);
    }

    private ObjectPatternProperty ParseObjectPatternProperty()
    {
        int start = Current.Start;
        // ES2018 rest: `...identifier` as the last element.
        if (Current.Kind == JsTokenKind.DotDotDot)
        {
            Consume();
            if (Current.Kind != JsTokenKind.Identifier)
            {
                throw new JsParseException(
                    "Rest element in object pattern must be an identifier",
                    Current.Start);
            }
            var restId = ParseBindingIdentifier();
            if (Current.Kind == JsTokenKind.Assign)
            {
                throw new JsParseException(
                    "Rest element in object pattern may not have a default",
                    Current.Start);
            }
            return new ObjectPatternProperty(
                start, restId.End,
                key: restId.Name,
                value: restId,
                @default: null,
                isRest: true);
        }
        if (Current.Kind != JsTokenKind.Identifier)
        {
            throw new JsParseException(
                $"Expected identifier in object pattern, got {Current.Kind}",
                Current.Start);
        }
        var keyTok = Consume();
        string key = keyTok.StringValue!;
        JsNode target;
        if (Match(JsTokenKind.Colon))
        {
            // Rename or nested pattern: { key: target }
            target = ParseBindingTarget();
        }
        else
        {
            // Shorthand: { key } — target is an Identifier with same name.
            target = new Identifier(keyTok.Start, keyTok.End, key);
        }
        Expression? @default = null;
        if (Match(JsTokenKind.Assign))
        {
            @default = ParseAssignmentExpression(allowIn: true);
        }
        int end = @default?.End ?? target.End;
        return new ObjectPatternProperty(start, end, key, target, @default);
    }

    /// <summary>
    /// <c>[ a, , b = 1, [c, d] ]</c>. Elisions (missing
    /// elements between commas) produce <c>null</c> entries
    /// in the element list.
    /// </summary>
    private ArrayPattern ParseArrayPattern()
    {
        int start = Current.Start;
        Expect(JsTokenKind.LeftBracket, "array pattern");
        var elements = new List<ArrayPatternElement?>();
        bool sawRest = false;
        while (Current.Kind != JsTokenKind.RightBracket)
        {
            if (Current.Kind == JsTokenKind.Comma)
            {
                // Elision — advance past the comma with a null slot.
                elements.Add(null);
                Consume();
                continue;
            }
            if (sawRest)
            {
                throw new JsParseException(
                    "Rest element must be the last element of an array pattern",
                    Current.Start);
            }
            var elem = ParseArrayPatternElement();
            elements.Add(elem);
            if (elem.IsRest) sawRest = true;
            if (Current.Kind != JsTokenKind.RightBracket)
            {
                Expect(JsTokenKind.Comma, "array pattern");
            }
        }
        int end = Current.End;
        Expect(JsTokenKind.RightBracket, "array pattern");
        return new ArrayPattern(start, end, elements);
    }

    private ArrayPatternElement ParseArrayPatternElement()
    {
        int start = Current.Start;
        if (Current.Kind == JsTokenKind.DotDotDot)
        {
            Consume();
            var restTarget = ParseBindingTarget();
            if (Current.Kind == JsTokenKind.Assign)
            {
                throw new JsParseException(
                    "Rest element may not have a default value",
                    Current.Start);
            }
            return new ArrayPatternElement(start, restTarget.End, restTarget, null, isRest: true);
        }
        var target = ParseBindingTarget();
        Expression? @default = null;
        if (Match(JsTokenKind.Assign))
        {
            @default = ParseAssignmentExpression(allowIn: true);
        }
        int end = @default?.End ?? target.End;
        return new ArrayPatternElement(start, end, target, @default);
    }

    // -------------------------------------------------------------------
    // Expressions
    // -------------------------------------------------------------------

    /// <summary>
    /// Expression = AssignmentExpression (',' AssignmentExpression)* .
    /// The comma case produces a <see cref="SequenceExpression"/>.
    /// </summary>
    private Expression ParseExpression(bool allowIn)
    {
        var first = ParseAssignmentExpression(allowIn);
        if (Current.Kind != JsTokenKind.Comma)
        {
            return first;
        }
        var list = new List<Expression> { first };
        while (Match(JsTokenKind.Comma))
        {
            list.Add(ParseAssignmentExpression(allowIn));
        }
        return new SequenceExpression(first.Start, list[list.Count - 1].End, list);
    }

    /// <summary>
    /// AssignmentExpression handles the right-associative assignment
    /// family and delegates to ConditionalExpression for everything
    /// below. The spec-approved way to distinguish "is this an
    /// assignment?" is: parse the LHS as a ConditionalExpression,
    /// then if the next token is an assignment operator, verify the
    /// LHS is a valid reference and recurse at the same level.
    ///
    /// ES2015: the grammar also admits arrow functions at this
    /// level. Before the conditional path we peek for the
    /// unambiguous arrow-function start patterns — a bare
    /// <c>Identifier => </c> or a parenthesized parameter list
    /// whose closing <c>)</c> is followed by <c>=&gt;</c>. The
    /// paren scan is lookahead-only; if the tokens don't
    /// actually form an arrow, we fall through and let the
    /// normal expression parse handle them.
    /// </summary>
    private Expression ParseAssignmentExpression(bool allowIn)
    {
        // ES2015: YieldExpression is part of AssignmentExpression
        // when we're inside a generator body. Parse it here so it
        // plugs into all the places an assignment expression is
        // accepted (function args, array/object literal elements,
        // return values, etc.).
        if (Current.Kind == JsTokenKind.KeywordYield && _inGeneratorBody)
        {
            return ParseYieldExpression(allowIn);
        }

        // ES2017: AwaitExpression — only legal inside async
        // function bodies. Same expression-level placement as
        // yield. `await` is a contextual keyword (a plain
        // Identifier token outside async bodies).
        if (_inAsyncBody &&
            Current.Kind == JsTokenKind.Identifier &&
            Current.StringValue == "await" &&
            CanStartExpression(Peek(1).Kind) &&
            !_hasLineBefore[_pos + 1])
        {
            return ParseAwaitExpression(allowIn);
        }

        var arrow = TryParseArrowFunction(allowIn);
        if (arrow is not null) return arrow;

        var left = ParseConditionalExpression(allowIn);
        var op = ToAssignmentOperator(Current.Kind);
        if (op is null) return left;

        if (!IsValidLhs(left))
        {
            throw new JsParseException(
                "Invalid assignment target",
                left.Start);
        }
        Consume();
        var right = ParseAssignmentExpression(allowIn);
        return new AssignmentExpression(left.Start, right.End, op.Value, left, right);
    }

    /// <summary>
    /// Parse a <c>yield</c> or <c>yield expr</c> expression.
    /// The argument is optional — if the token following
    /// <c>yield</c> cannot begin an expression, or if a line
    /// terminator separates them (the ASI "no LineTerminator
    /// here" rule from the spec), we treat it as
    /// <c>yield undefined</c>.
    /// </summary>
    private YieldExpression ParseYieldExpression(bool allowIn)
    {
        int start = Current.Start;
        Consume(); // yield
        // `yield* expr` — delegation form.
        bool isDelegate = false;
        if (Current.Kind == JsTokenKind.Star)
        {
            Consume();
            isDelegate = true;
        }
        // No argument if:
        //   - next token is something that cannot start an
        //     expression (common: ), ], }, ;, ,, EOF)
        //   - or there is a line terminator between `yield`
        //     and the next token (ASI's restricted production).
        if (isDelegate || (CanStartExpression(Current.Kind) && !_hasLineBefore[_pos]))
        {
            var arg = ParseAssignmentExpression(allowIn);
            return new YieldExpression(start, arg.End, arg, isDelegate);
        }
        return new YieldExpression(start, Current.Start, null);
    }

    /// <summary>
    /// <c>await expr</c>. The argument is parsed at unary
    /// precedence so <c>await x + 1</c> parses as
    /// <c>(await x) + 1</c>, matching the spec.
    /// </summary>
    private AwaitExpression ParseAwaitExpression(bool allowIn)
    {
        int start = Current.Start;
        Consume(); // await
        var arg = ParseUnaryExpression();
        return new AwaitExpression(start, arg.End, arg);
    }

    private static bool CanStartExpression(JsTokenKind kind) => kind switch
    {
        JsTokenKind.RightParen or
        JsTokenKind.RightBracket or
        JsTokenKind.RightBrace or
        JsTokenKind.Semicolon or
        JsTokenKind.Comma or
        JsTokenKind.Colon or
        JsTokenKind.EndOfFile => false,
        _ => true,
    };

    /// <summary>
    /// Peek for ES2015 arrow function syntax. Recognizes
    /// <c>identifier =&gt; body</c> and <c>( params ) =&gt; body</c>.
    /// Returns <c>null</c> if the current position doesn't begin
    /// an arrow; the caller then falls through to the normal
    /// expression parse. The scan-for-matching-paren path is
    /// read-only — it never mutates <c>_pos</c>.
    /// </summary>
    private Expression? TryParseArrowFunction(bool allowIn)
    {
        // ES2017 async arrow: `async x => body` or
        // `async (...) => body`. Detected by `async` followed
        // immediately (no LineTerminator) by an identifier
        // or a paren.
        bool isAsync = false;
        int rewindPos = _pos;
        if (CurrentIsAsyncKeyword())
        {
            var next = Peek(1).Kind;
            if ((next == JsTokenKind.Identifier && Peek(2).Kind == JsTokenKind.Arrow) ||
                next == JsTokenKind.LeftParen)
            {
                isAsync = true;
                _pos++; // consume `async`
            }
        }

        // Case 1: `x => body` — single identifier param.
        if (Current.Kind == JsTokenKind.Identifier &&
            Peek(1).Kind == JsTokenKind.Arrow)
        {
            return ParseSingleIdentifierArrow(allowIn, isAsync);
        }

        // Case 2: `(...) => body` — parenthesized params. Scan
        // forward for the matching close paren and check if
        // it's followed by `=>`. Nested parens must balance.
        if (Current.Kind == JsTokenKind.LeftParen)
        {
            int look = _pos + 1;
            int depth = 1;
            while (look < _tokens.Count)
            {
                var k = _tokens[look].Kind;
                if (k == JsTokenKind.EndOfFile) break;
                if (k == JsTokenKind.LeftParen) depth++;
                else if (k == JsTokenKind.RightParen)
                {
                    depth--;
                    if (depth == 0) break;
                }
                look++;
            }
            if (look < _tokens.Count &&
                look + 1 < _tokens.Count &&
                _tokens[look + 1].Kind == JsTokenKind.Arrow)
            {
                return ParseParenthesizedArrow(allowIn, isAsync);
            }
        }

        // Not actually an arrow after all — rewind past the
        // speculative `async` consumption so the outer
        // expression parser can handle this token.
        _pos = rewindPos;
        return null;
    }

    private Expression ParseSingleIdentifierArrow(bool allowIn, bool isAsync = false)
    {
        int start = Current.Start;
        var idTok = Consume();
        var param = new Identifier(idTok.Start, idTok.End, idTok.StringValue!);
        Expect(JsTokenKind.Arrow, "arrow function");
        bool savedAsync = _inAsyncBody;
        _inAsyncBody = isAsync;
        JsNode body;
        try { body = ParseArrowBody(allowIn); }
        finally { _inAsyncBody = savedAsync; }
        var fp = new FunctionParameter(param.Start, param.End, param, null, isRest: false);
        return new ArrowFunctionExpression(
            start,
            body.End,
            new List<FunctionParameter> { fp },
            body,
            isAsync);
    }

    private Expression ParseParenthesizedArrow(bool allowIn, bool isAsync = false)
    {
        int start = Current.Start;
        Consume(); // (
        var @params = new List<FunctionParameter>();
        if (Current.Kind != JsTokenKind.RightParen)
        {
            @params.Add(ParseFunctionParameter());
            while (Match(JsTokenKind.Comma))
            {
                if (@params[@params.Count - 1].IsRest)
                {
                    throw new JsParseException(
                        "Rest parameter must be the last formal parameter",
                        Current.Start);
                }
                @params.Add(ParseFunctionParameter());
            }
        }
        Expect(JsTokenKind.RightParen, "arrow params");
        Expect(JsTokenKind.Arrow, "arrow function");
        bool savedAsync = _inAsyncBody;
        _inAsyncBody = isAsync;
        JsNode body;
        try { body = ParseArrowBody(allowIn); }
        finally { _inAsyncBody = savedAsync; }
        return new ArrowFunctionExpression(start, body.End, @params, body, isAsync);
    }

    /// <summary>
    /// Parse the body of an arrow function. A <c>{</c>
    /// immediately after the <c>=&gt;</c> introduces a block
    /// body; anything else is a concise expression body. Per
    /// spec, an object literal in a concise body must be
    /// parenthesized (<c>x =&gt; ({a: 1})</c>); an unparenthesized
    /// <c>x =&gt; {a: 1}</c> is a block statement with a labeled
    /// statement in it, not an object literal.
    /// </summary>
    private JsNode ParseArrowBody(bool allowIn)
    {
        if (Current.Kind == JsTokenKind.LeftBrace)
        {
            return ParseBlockStatement();
        }
        return ParseAssignmentExpression(allowIn);
    }

    private Expression ParseConditionalExpression(bool allowIn)
    {
        var test = ParseBinaryExpression(1, allowIn);
        if (!Match(JsTokenKind.QuestionMark)) return test;

        // Per spec: consequent is AssignmentExpression with allowIn=true
        // regardless of outer context, alternate is AssignmentExpression
        // with the outer allowIn.
        var consequent = ParseAssignmentExpression(allowIn: true);
        Expect(JsTokenKind.Colon, "conditional expression");
        var alternate = ParseAssignmentExpression(allowIn);
        return new ConditionalExpression(test.Start, alternate.End, test, consequent, alternate);
    }

    /// <summary>
    /// Precedence-climbing binary parser. <paramref name="minPrecedence"/>
    /// is the lowest precedence we will accept at this call site.
    /// Right-associative binary operators don't exist in ES5 (ternary
    /// and assignment are handled above), so every step uses
    /// <c>opPrec + 1</c> for the recursive call — standard left-assoc.
    ///
    /// <c>&amp;&amp;</c> and <c>||</c> collapse into
    /// <see cref="LogicalExpression"/> instead of
    /// <see cref="BinaryExpression"/> because their evaluation model
    /// is genuinely different (short-circuit, result is an operand,
    /// not a coerced boolean).
    /// </summary>
    private Expression ParseBinaryExpression(int minPrecedence, bool allowIn)
    {
        var left = ParseUnaryExpression();
        while (true)
        {
            int prec = GetBinaryPrecedence(Current.Kind, allowIn);
            if (prec < minPrecedence) break;

            var opKind = Current.Kind;
            Consume();
            var right = ParseBinaryExpression(prec + 1, allowIn);

            if (opKind == JsTokenKind.AmpersandAmpersand)
            {
                left = new LogicalExpression(left.Start, right.End, LogicalOperator.And, left, right);
            }
            else if (opKind == JsTokenKind.PipePipe)
            {
                left = new LogicalExpression(left.Start, right.End, LogicalOperator.Or, left, right);
            }
            else if (opKind == JsTokenKind.QuestionQuestion)
            {
                left = new LogicalExpression(left.Start, right.End, LogicalOperator.Nullish, left, right);
            }
            else
            {
                left = new BinaryExpression(
                    left.Start, right.End, ToBinaryOperator(opKind), left, right);
            }
        }
        return left;
    }

    private Expression ParseUnaryExpression()
    {
        int start = Current.Start;
        UnaryOperator? unaryOp = Current.Kind switch
        {
            JsTokenKind.KeywordDelete => UnaryOperator.Delete,
            JsTokenKind.KeywordVoid => UnaryOperator.Void,
            JsTokenKind.KeywordTypeof => UnaryOperator.TypeOf,
            JsTokenKind.Plus => UnaryOperator.Plus,
            JsTokenKind.Minus => UnaryOperator.Minus,
            JsTokenKind.Exclamation => UnaryOperator.LogicalNot,
            JsTokenKind.Tilde => UnaryOperator.BitwiseNot,
            _ => null,
        };
        if (unaryOp is not null)
        {
            Consume();
            var arg = ParseUnaryExpression();
            return new UnaryExpression(start, arg.End, unaryOp.Value, arg);
        }

        if (Current.Kind == JsTokenKind.PlusPlus || Current.Kind == JsTokenKind.MinusMinus)
        {
            var op = Current.Kind == JsTokenKind.PlusPlus
                ? UpdateOperator.Increment
                : UpdateOperator.Decrement;
            Consume();
            var arg = ParseUnaryExpression();
            if (!IsValidLhs(arg))
            {
                throw new JsParseException(
                    op == UpdateOperator.Increment ? "Invalid '++' target" : "Invalid '--' target",
                    arg.Start);
            }
            return new UpdateExpression(start, arg.End, op, arg, prefix: true);
        }

        return ParsePostfixExpression();
    }

    private Expression ParsePostfixExpression()
    {
        var expr = ParseLeftHandSideExpression();
        // Restricted production: no line break before ++/-- .
        if (!HasLineBeforeCurrent &&
            (Current.Kind == JsTokenKind.PlusPlus || Current.Kind == JsTokenKind.MinusMinus))
        {
            if (!IsValidLhs(expr))
            {
                throw new JsParseException(
                    "Invalid target for postfix update",
                    expr.Start);
            }
            var op = Current.Kind == JsTokenKind.PlusPlus
                ? UpdateOperator.Increment
                : UpdateOperator.Decrement;
            int end = Current.End;
            Consume();
            return new UpdateExpression(expr.Start, end, op, expr, prefix: false);
        }
        return expr;
    }

    /// <summary>
    /// LeftHandSideExpression covers member access, function calls, and
    /// <c>new</c>. ES5 distinguishes between <c>NewExpression</c> (which
    /// allows <c>new</c> without argument list) and <c>CallExpression</c>
    /// (once you've started parsing call arguments you can't go back);
    /// we fold them into one loop for simplicity because the grammar
    /// difference only matters for error-reporting corner cases.
    /// </summary>
    private Expression ParseLeftHandSideExpression()
    {
        Expression expr = Current.Kind == JsTokenKind.KeywordNew
            ? ParseNewExpression()
            : ParsePrimaryExpression();

        // Track whether any `?.` appeared in this chain. The first
        // optional hop marks each subsequent hop as "still inside"
        // the chain even though they use regular `.` / `[` / `(`
        // — the whole chain must short-circuit uniformly.
        bool sawOptional = false;

        while (true)
        {
            if (Current.Kind == JsTokenKind.QuestionDot)
            {
                Consume();
                sawOptional = true;
                // `x?.[k]` and `x?.(` — the `?.` consumes before
                // the bracket/paren, then the normal member/call
                // path handles the rest with IsOptional=true.
                if (Current.Kind == JsTokenKind.LeftBracket)
                {
                    Consume();
                    var index = ParseExpression(allowIn: true);
                    int end = Current.End;
                    Expect(JsTokenKind.RightBracket, "optional member access");
                    expr = new MemberExpression(expr.Start, end, expr, index, computed: true, isOptional: true);
                    continue;
                }
                if (Current.Kind == JsTokenKind.LeftParen)
                {
                    var args = ParseArguments();
                    int end = _tokens[_pos - 1].End;
                    expr = new CallExpression(expr.Start, end, expr, args, isOptional: true);
                    continue;
                }
                // `x?.y` — fall through to the dotted-name path.
                var name = ParsePropertyName(keywordsAllowed: true);
                expr = new MemberExpression(expr.Start, name.End, expr, name, computed: false, isOptional: true);
                continue;
            }
            if (Current.Kind == JsTokenKind.Dot)
            {
                Consume();
                var name = ParsePropertyName(keywordsAllowed: true);
                expr = new MemberExpression(expr.Start, name.End, expr, name, computed: false);
            }
            else if (Current.Kind == JsTokenKind.LeftBracket)
            {
                Consume();
                var index = ParseExpression(allowIn: true);
                int end = Current.End;
                Expect(JsTokenKind.RightBracket, "member access");
                expr = new MemberExpression(expr.Start, end, expr, index, computed: true);
            }
            else if (Current.Kind == JsTokenKind.LeftParen)
            {
                var args = ParseArguments();
                int end = _tokens[_pos - 1].End;
                expr = new CallExpression(expr.Start, end, expr, args);
            }
            else if (Current.Kind == JsTokenKind.NoSubstitutionTemplate ||
                     Current.Kind == JsTokenKind.TemplateHead)
            {
                if (sawOptional)
                {
                    // Tagged templates inside an optional chain are
                    // a syntax error per ES2020 — the `?.` short-
                    // circuit and the tag's TemplateObject caching
                    // don't compose cleanly.
                    throw new JsParseException(
                        "Tagged template is not allowed in an optional chain",
                        Current.Start);
                }
                // Tagged template: `expr`template``. The
                // template literal becomes the sole
                // argument-like payload; the compiler
                // lowers to a call of `expr(strings, ...exprs)`.
                TemplateLiteral quasi;
                if (Current.Kind == JsTokenKind.NoSubstitutionTemplate)
                {
                    var tok = Consume();
                    quasi = new TemplateLiteral(
                        tok.Start,
                        tok.End,
                        new List<string> { tok.StringValue ?? string.Empty },
                        new List<Expression>());
                }
                else
                {
                    quasi = ParseTemplateLiteral();
                }
                expr = new TaggedTemplateExpression(expr.Start, quasi.End, expr, quasi);
            }
            else
            {
                break;
            }
        }

        // Wrap the whole chain in a ChainExpression so the
        // compiler has a single end-of-chain label to emit the
        // short-circuit jumps against.
        if (sawOptional)
        {
            expr = new ChainExpression(expr.Start, expr.End, expr);
        }
        return expr;
    }

    /// <summary>
    /// <c>new</c> is recursive: <c>new new Foo()(bar)</c> constructs
    /// <c>Foo</c>, then constructs the result with <c>bar</c>. We
    /// parse the callee as a fresh LeftHandSideExpression-minus-call
    /// to match — a member access after <c>new</c> is part of the
    /// constructor expression, but a call <i>becomes</i> the argument
    /// list for this <c>new</c>, not an independent call.
    /// </summary>
    private Expression ParseNewExpression()
    {
        int start = Current.Start;
        Consume(); // new
        Expression callee = Current.Kind == JsTokenKind.KeywordNew
            ? ParseNewExpression()
            : ParsePrimaryExpression();

        // Member access is part of the callee.
        while (Current.Kind == JsTokenKind.Dot || Current.Kind == JsTokenKind.LeftBracket)
        {
            if (Current.Kind == JsTokenKind.Dot)
            {
                Consume();
                var name = ParsePropertyName(keywordsAllowed: true);
                callee = new MemberExpression(callee.Start, name.End, callee, name, computed: false);
            }
            else
            {
                Consume();
                var index = ParseExpression(allowIn: true);
                int mEnd = Current.End;
                Expect(JsTokenKind.RightBracket, "new member access");
                callee = new MemberExpression(callee.Start, mEnd, callee, index, computed: true);
            }
        }

        // Argument list — optional. If present, consumes it.
        IReadOnlyList<Expression> args = Array.Empty<Expression>();
        int end = callee.End;
        if (Current.Kind == JsTokenKind.LeftParen)
        {
            args = ParseArguments();
            end = _tokens[_pos - 1].End;
        }
        return new NewExpression(start, end, callee, args);
    }

    private List<Expression> ParseArguments()
    {
        Expect(JsTokenKind.LeftParen, "argument list");
        var args = new List<Expression>();
        if (Current.Kind != JsTokenKind.RightParen)
        {
            args.Add(ParseArgumentOrSpread());
            while (Match(JsTokenKind.Comma))
            {
                args.Add(ParseArgumentOrSpread());
            }
        }
        Expect(JsTokenKind.RightParen, "argument list");
        return args;
    }

    /// <summary>
    /// A call or <c>new</c> argument, optionally preceded by
    /// <c>...</c> for the ES2015 spread form. Spread arguments
    /// produce a <see cref="SpreadElement"/>; the compiler
    /// recognizes that node and emits a spread-call lowering.
    /// </summary>
    private Expression ParseArgumentOrSpread()
    {
        if (Current.Kind == JsTokenKind.DotDotDot)
        {
            int start = Current.Start;
            Consume();
            var inner = ParseAssignmentExpression(allowIn: true);
            return new SpreadElement(start, inner.End, inner);
        }
        return ParseAssignmentExpression(allowIn: true);
    }

    // -------------------------------------------------------------------
    // Primary expressions
    // -------------------------------------------------------------------

    private Expression ParsePrimaryExpression()
    {
        var tok = Current;
        switch (tok.Kind)
        {
            case JsTokenKind.KeywordThis:
                Consume();
                return new ThisExpression(tok.Start, tok.End);

            // `async function ...` as an expression. Must be
            // matched before the generic Identifier case so the
            // contextual `async` keyword doesn't fall through.
            case JsTokenKind.Identifier when tok.StringValue == "async"
                && Peek(1).Kind == JsTokenKind.KeywordFunction
                && !_hasLineBefore[_pos + 1]:
                Consume(); // async
                return ParseFunctionExpression(isAsync: true);

            case JsTokenKind.Identifier:
                Consume();
                return new Identifier(tok.Start, tok.End, tok.StringValue!);

            case JsTokenKind.NullLiteral:
                Consume();
                return new Literal(tok.Start, tok.End, LiteralKind.Null, null);

            case JsTokenKind.TrueLiteral:
                Consume();
                return new Literal(tok.Start, tok.End, LiteralKind.Boolean, true);

            case JsTokenKind.FalseLiteral:
                Consume();
                return new Literal(tok.Start, tok.End, LiteralKind.Boolean, false);

            case JsTokenKind.NumberLiteral:
                Consume();
                return new Literal(tok.Start, tok.End, LiteralKind.Number, tok.NumberValue);

            case JsTokenKind.BigIntLiteral:
                Consume();
                return new Literal(tok.Start, tok.End, LiteralKind.BigInt,
                    ParseBigIntLiteralValue(tok));

            case JsTokenKind.StringLiteral:
                Consume();
                return new Literal(tok.Start, tok.End, LiteralKind.String, tok.StringValue);

            case JsTokenKind.LeftBracket:
                return ParseArrayLiteral();

            case JsTokenKind.LeftBrace:
                return ParseObjectLiteral();

            case JsTokenKind.LeftParen:
                Consume();
                var expr = ParseExpression(allowIn: true);
                Expect(JsTokenKind.RightParen, "parenthesized expression");
                return expr;

            case JsTokenKind.KeywordFunction:
                return ParseFunctionExpression();

            case JsTokenKind.KeywordClass:
                return ParseClassExpression();

            case JsTokenKind.KeywordSuper:
                Consume();
                return new Super(tok.Start, tok.End);

            case JsTokenKind.KeywordImport when Peek(1).Kind == JsTokenKind.LeftParen:
                {
                    // Dynamic `import(specifier)`. Lowered
                    // to a call to a hidden built-in
                    // `$importModule` that returns a Promise
                    // resolving to the target module's
                    // exports namespace.
                    int importStart = Current.Start;
                    Consume(); // import
                    Expect(JsTokenKind.LeftParen, "import call");
                    var specArg = ParseAssignmentExpression(allowIn: true);
                    int importEnd = Current.End;
                    Expect(JsTokenKind.RightParen, "import call");
                    var callee = new Identifier(importStart, importStart + 6, "$importModule");
                    return new CallExpression(
                        importStart,
                        importEnd,
                        callee,
                        new List<Expression> { specArg });
                }

            case JsTokenKind.NoSubstitutionTemplate:
                Consume();
                return new TemplateLiteral(
                    tok.Start,
                    tok.End,
                    new List<string> { tok.StringValue ?? string.Empty },
                    new List<Expression>());

            case JsTokenKind.TemplateHead:
                return ParseTemplateLiteral();
        }

        throw new JsParseException(
            $"Unexpected token {tok.Kind}",
            tok.Start);
    }

    /// <summary>
    /// Parse a template literal that starts with a
    /// <see cref="JsTokenKind.TemplateHead"/>. The token stream
    /// alternates head → expression → middle → expression →
    /// ... → tail. Expressions parse as regular full
    /// expressions (not just assignment expressions — the spec
    /// allows any expression inside a template interpolation).
    /// </summary>
    private TemplateLiteral ParseTemplateLiteral()
    {
        int start = Current.Start;
        var quasis = new List<string>();
        var expressions = new List<Expression>();

        var head = Consume(); // TemplateHead
        quasis.Add(head.StringValue ?? string.Empty);

        while (true)
        {
            var expr = ParseExpression(allowIn: true);
            expressions.Add(expr);

            if (Current.Kind == JsTokenKind.TemplateMiddle)
            {
                var mid = Consume();
                quasis.Add(mid.StringValue ?? string.Empty);
                continue;
            }

            if (Current.Kind == JsTokenKind.TemplateTail)
            {
                var tail = Consume();
                quasis.Add(tail.StringValue ?? string.Empty);
                return new TemplateLiteral(start, tail.End, quasis, expressions);
            }

            throw new JsParseException(
                $"Expected TemplateMiddle or TemplateTail, got {Current.Kind}",
                Current.Start);
        }
    }

    private ArrayExpression ParseArrayLiteral()
    {
        int start = Current.Start;
        Expect(JsTokenKind.LeftBracket, "array literal");
        var elements = new List<Expression?>();

        while (Current.Kind != JsTokenKind.RightBracket && Current.Kind != JsTokenKind.EndOfFile)
        {
            if (Current.Kind == JsTokenKind.Comma)
            {
                // Elision (hole): [1, , 3]
                elements.Add(null);
                Consume();
                continue;
            }
            if (Current.Kind == JsTokenKind.DotDotDot)
            {
                int spreadStart = Current.Start;
                Consume();
                var inner = ParseAssignmentExpression(allowIn: true);
                elements.Add(new SpreadElement(spreadStart, inner.End, inner));
            }
            else
            {
                elements.Add(ParseAssignmentExpression(allowIn: true));
            }
            if (Current.Kind != JsTokenKind.RightBracket)
            {
                Expect(JsTokenKind.Comma, "array literal");
            }
        }
        int end = Current.End;
        Expect(JsTokenKind.RightBracket, "array literal");
        return new ArrayExpression(start, end, elements);
    }

    private ObjectExpression ParseObjectLiteral()
    {
        int start = Current.Start;
        Expect(JsTokenKind.LeftBrace, "object literal");
        var props = new List<Property>();

        while (Current.Kind != JsTokenKind.RightBrace && Current.Kind != JsTokenKind.EndOfFile)
        {
            props.Add(ParseObjectProperty());
            if (Current.Kind != JsTokenKind.RightBrace)
            {
                Expect(JsTokenKind.Comma, "object literal");
                // Trailing comma is legal: { a: 1, }
                if (Current.Kind == JsTokenKind.RightBrace) break;
            }
        }
        int end = Current.End;
        Expect(JsTokenKind.RightBrace, "object literal");
        return new ObjectExpression(start, end, props);
    }

    private Property ParseObjectProperty()
    {
        // ES2018 spread: `{ ...source }`. The resulting
        // Property has IsSpread = true and stores the source
        // expression in Value; the compiler treats it as a
        // runtime enumerate-and-copy rather than a single-
        // property init.
        if (Current.Kind == JsTokenKind.DotDotDot)
        {
            int spreadStart = Current.Start;
            Consume();
            var source = ParseAssignmentExpression(allowIn: true);
            return new Property(
                start: spreadStart,
                end: source.End,
                key: new Identifier(spreadStart, spreadStart, "<spread>"),
                value: source,
                kind: PropertyKind.Init,
                isSpread: true);
        }

        // get / set accessors: the identifier 'get' or 'set' followed
        // by another property name (not by ':' or ',' or '}').
        if (Current.Kind == JsTokenKind.Identifier &&
            (Current.StringValue == "get" || Current.StringValue == "set"))
        {
            var next = Peek(1).Kind;
            if (next != JsTokenKind.Colon && next != JsTokenKind.Comma && next != JsTokenKind.RightBrace)
            {
                return ParseAccessorProperty();
            }
        }

        int start = Current.Start;
        // ES2015 shorthand: `{ a, b }` is equivalent to
        // `{ a: a, b: b }`. We detect a bare identifier
        // followed by `,` or `}` and synthesize the value.
        if (Current.Kind == JsTokenKind.Identifier &&
            (Peek(1).Kind == JsTokenKind.Comma || Peek(1).Kind == JsTokenKind.RightBrace))
        {
            var idTok = Consume();
            var keyId = new Identifier(idTok.Start, idTok.End, idTok.StringValue!);
            var valueId = new Identifier(idTok.Start, idTok.End, idTok.StringValue!);
            return new Property(start, idTok.End, keyId, valueId, PropertyKind.Init);
        }
        var key = ParsePropertyName(keywordsAllowed: true);
        Expect(JsTokenKind.Colon, "object property");
        var value = ParseAssignmentExpression(allowIn: true);
        return new Property(start, value.End, key, value, PropertyKind.Init);
    }

    private Property ParseAccessorProperty()
    {
        int start = Current.Start;
        var kind = Current.StringValue == "get" ? PropertyKind.Get : PropertyKind.Set;
        Consume(); // get / set
        var key = ParsePropertyName(keywordsAllowed: true);
        var paramList = ParseFormalParameters();
        var body = ParseFunctionBody();

        // Wrap in a FunctionExpression to carry the body. The
        // compiler knows getters/setters have fixed arity (0 for
        // get, 1 for set) and will validate there.
        var fn = new FunctionExpression(start, body.End, id: null, paramList, body);
        return new Property(start, body.End, key, fn, kind);
    }

    /// <summary>
    /// Parse a property name. In object literals and dot member access
    /// this can be an identifier, a reserved word (ES5 explicitly allows
    /// <c>{ if: 1 }</c>), a string literal, or a number literal.
    /// </summary>
    private Expression ParsePropertyName(bool keywordsAllowed)
    {
        var tok = Current;
        if (tok.Kind == JsTokenKind.Identifier)
        {
            Consume();
            return new Identifier(tok.Start, tok.End, tok.StringValue!);
        }
        if (tok.Kind == JsTokenKind.StringLiteral)
        {
            Consume();
            return new Literal(tok.Start, tok.End, LiteralKind.String, tok.StringValue);
        }
        if (tok.Kind == JsTokenKind.NumberLiteral)
        {
            Consume();
            return new Literal(tok.Start, tok.End, LiteralKind.Number, tok.NumberValue);
        }
        if (keywordsAllowed && IsKeyword(tok.Kind))
        {
            Consume();
            // Store the textual form so the compiler can emit the
            // right property lookup.
            var text = _source.Substring(tok.Start, tok.Length);
            return new Identifier(tok.Start, tok.End, text);
        }
        throw new JsParseException(
            $"Expected property name, got {tok.Kind}",
            tok.Start);
    }

    // -------------------------------------------------------------------
    // Lookup tables
    // -------------------------------------------------------------------

    /// <summary>
    /// Return the precedence (1 = lowest binary, higher = tighter) of a
    /// token interpreted as a binary operator, or 0 if the token is not
    /// a binary operator in this context. <paramref name="allowIn"/>
    /// controls whether <c>in</c> is a valid operator — see the
    /// <c>for..in</c> disambiguation note on the class header.
    /// </summary>
    private static int GetBinaryPrecedence(JsTokenKind kind, bool allowIn) => kind switch
    {
        // ?? sits at the same level as || and is mutually
        // exclusive with it at that level (the spec forbids
        // mixing `a || b ?? c` without explicit parens, but
        // we don't enforce that — it parses left-to-right).
        JsTokenKind.QuestionQuestion => 4,
        JsTokenKind.PipePipe => 4,
        JsTokenKind.AmpersandAmpersand => 5,
        JsTokenKind.Pipe => 6,
        JsTokenKind.Caret => 7,
        JsTokenKind.Ampersand => 8,
        JsTokenKind.Equal or JsTokenKind.NotEqual
            or JsTokenKind.StrictEqual or JsTokenKind.StrictNotEqual => 9,
        JsTokenKind.LessThan or JsTokenKind.LessThanEqual
            or JsTokenKind.GreaterThan or JsTokenKind.GreaterThanEqual
            or JsTokenKind.KeywordInstanceof => 10,
        JsTokenKind.KeywordIn => allowIn ? 10 : 0,
        JsTokenKind.LeftShift or JsTokenKind.RightShift
            or JsTokenKind.UnsignedRightShift => 11,
        JsTokenKind.Plus or JsTokenKind.Minus => 12,
        JsTokenKind.Star or JsTokenKind.Slash or JsTokenKind.Percent => 13,
        _ => 0,
    };

    private static BinaryOperator ToBinaryOperator(JsTokenKind kind) => kind switch
    {
        JsTokenKind.Equal => BinaryOperator.Equal,
        JsTokenKind.NotEqual => BinaryOperator.NotEqual,
        JsTokenKind.StrictEqual => BinaryOperator.StrictEqual,
        JsTokenKind.StrictNotEqual => BinaryOperator.StrictNotEqual,
        JsTokenKind.LessThan => BinaryOperator.LessThan,
        JsTokenKind.LessThanEqual => BinaryOperator.LessThanEqual,
        JsTokenKind.GreaterThan => BinaryOperator.GreaterThan,
        JsTokenKind.GreaterThanEqual => BinaryOperator.GreaterThanEqual,
        JsTokenKind.KeywordInstanceof => BinaryOperator.InstanceOf,
        JsTokenKind.KeywordIn => BinaryOperator.In,
        JsTokenKind.LeftShift => BinaryOperator.LeftShift,
        JsTokenKind.RightShift => BinaryOperator.RightShift,
        JsTokenKind.UnsignedRightShift => BinaryOperator.UnsignedRightShift,
        JsTokenKind.Plus => BinaryOperator.Add,
        JsTokenKind.Minus => BinaryOperator.Subtract,
        JsTokenKind.Star => BinaryOperator.Multiply,
        JsTokenKind.Slash => BinaryOperator.Divide,
        JsTokenKind.Percent => BinaryOperator.Modulo,
        JsTokenKind.Ampersand => BinaryOperator.BitwiseAnd,
        JsTokenKind.Pipe => BinaryOperator.BitwiseOr,
        JsTokenKind.Caret => BinaryOperator.BitwiseXor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a binary operator"),
    };

    /// <summary>
    /// Convert the digit text captured by the lexer's
    /// <see cref="JsTokenKind.BigIntLiteral"/> token into a
    /// <see cref="System.Numerics.BigInteger"/>. Accepts the
    /// canonical decimal form (<c>42</c>) and the hex form
    /// (<c>0x1f</c>) — the lexer handles the <c>n</c> suffix
    /// before handing the text off here.
    /// </summary>
    private static System.Numerics.BigInteger ParseBigIntLiteralValue(JsToken tok)
    {
        var text = tok.StringValue ?? "0";
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            // BigInteger.Parse requires a leading 0 to
            // interpret the value as unsigned; the spec
            // guarantees BigInt literals are non-negative.
            var hexDigits = "0" + text.Substring(2);
            return System.Numerics.BigInteger.Parse(
                hexDigits,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        return System.Numerics.BigInteger.Parse(
            text,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static AssignmentOperator? ToAssignmentOperator(JsTokenKind kind) => kind switch
    {
        JsTokenKind.Assign => AssignmentOperator.Assign,
        JsTokenKind.PlusAssign => AssignmentOperator.AddAssign,
        JsTokenKind.MinusAssign => AssignmentOperator.SubtractAssign,
        JsTokenKind.StarAssign => AssignmentOperator.MultiplyAssign,
        JsTokenKind.SlashAssign => AssignmentOperator.DivideAssign,
        JsTokenKind.PercentAssign => AssignmentOperator.ModuloAssign,
        JsTokenKind.LeftShiftAssign => AssignmentOperator.LeftShiftAssign,
        JsTokenKind.RightShiftAssign => AssignmentOperator.RightShiftAssign,
        JsTokenKind.UnsignedRightShiftAssign => AssignmentOperator.UnsignedRightShiftAssign,
        JsTokenKind.AmpersandAssign => AssignmentOperator.BitwiseAndAssign,
        JsTokenKind.PipeAssign => AssignmentOperator.BitwiseOrAssign,
        JsTokenKind.CaretAssign => AssignmentOperator.BitwiseXorAssign,
        JsTokenKind.AmpersandAmpersandAssign => AssignmentOperator.LogicalAndAssign,
        JsTokenKind.PipePipeAssign => AssignmentOperator.LogicalOrAssign,
        JsTokenKind.QuestionQuestionAssign => AssignmentOperator.NullishAssign,
        _ => null,
    };

    private static bool IsKeyword(JsTokenKind kind) =>
        kind >= JsTokenKind.KeywordBreak && kind <= JsTokenKind.KeywordStatic;

    /// <summary>
    /// Early-exit check for "is this expression a valid reference?"
    /// used by assignment, prefix/postfix update, and the left side
    /// of <c>for..in</c>. In ES5 only identifiers and member accesses
    /// qualify. ESTree-style parsers typically do this check at
    /// semantic-analysis time; we do it inline because the error
    /// message is better when we still know the immediate context.
    /// </summary>
    private static bool IsValidLhs(Expression expr) =>
        expr is Identifier
        || expr is MemberExpression
        // Destructuring assignment: the cover grammar lets an
        // ObjectLiteral / ArrayLiteral stand in for an
        // ObjectAssignmentPattern / ArrayAssignmentPattern.
        // The compiler reinterprets these at assignment time.
        || expr is ObjectExpression
        || expr is ArrayExpression;
}

/// <summary>
/// Thrown when the parser encounters a syntax error. Carries the
/// zero-based source offset so callers can resolve line / column on
/// demand.
/// </summary>
public sealed class JsParseException : Exception
{
    public int Offset { get; }

    public JsParseException(string message, int offset) : base(message)
    {
        Offset = offset;
    }
}

