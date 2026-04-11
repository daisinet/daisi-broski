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
        return ParseStatement();
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
    private VariableDeclaration ParseVariableDeclaration(bool allowIn)
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
        decls.Add(ParseVariableDeclarator(allowIn));
        while (Match(JsTokenKind.Comma))
        {
            decls.Add(ParseVariableDeclarator(allowIn));
        }
        int end = decls[decls.Count - 1].End;
        return new VariableDeclaration(start, end, kind, decls);
    }

    private VariableDeclarator ParseVariableDeclarator(bool allowIn)
    {
        var id = ParseBindingIdentifier();
        Expression? init = null;
        if (Match(JsTokenKind.Assign))
        {
            init = ParseAssignmentExpression(allowIn);
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
                init = ParseVariableDeclaration(allowIn: false);
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

    private FunctionDeclaration ParseFunctionDeclaration()
    {
        int start = Current.Start;
        Consume(); // function
        var id = ParseBindingIdentifier();
        var paramList = ParseFormalParameters();
        var body = ParseFunctionBody();
        return new FunctionDeclaration(start, body.End, id, paramList, body);
    }

    private FunctionExpression ParseFunctionExpression()
    {
        int start = Current.Start;
        Consume(); // function
        Identifier? id = null;
        if (Current.Kind == JsTokenKind.Identifier)
        {
            id = ParseBindingIdentifier();
        }
        var paramList = ParseFormalParameters();
        var body = ParseFunctionBody();
        return new FunctionExpression(start, body.End, id, paramList, body);
    }

    private List<Identifier> ParseFormalParameters()
    {
        Expect(JsTokenKind.LeftParen, "parameter list");
        var list = new List<Identifier>();
        if (Current.Kind != JsTokenKind.RightParen)
        {
            list.Add(ParseBindingIdentifier());
            while (Match(JsTokenKind.Comma))
            {
                list.Add(ParseBindingIdentifier());
            }
        }
        Expect(JsTokenKind.RightParen, "parameter list");
        return list;
    }

    private BlockStatement ParseFunctionBody()
    {
        // Function bodies accept source elements (nested function
        // declarations are hoisted), which ParseBlockStatement already
        // handles via ParseSourceElement.
        return ParseBlockStatement();
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
    /// </summary>
    private Expression ParseAssignmentExpression(bool allowIn)
    {
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

        while (true)
        {
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
            else
            {
                break;
            }
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
            args.Add(ParseAssignmentExpression(allowIn: true));
            while (Match(JsTokenKind.Comma))
            {
                args.Add(ParseAssignmentExpression(allowIn: true));
            }
        }
        Expect(JsTokenKind.RightParen, "argument list");
        return args;
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
        }

        throw new JsParseException(
            $"Unexpected token {tok.Kind}",
            tok.Start);
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
            elements.Add(ParseAssignmentExpression(allowIn: true));
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
        expr is Identifier || expr is MemberExpression;
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

