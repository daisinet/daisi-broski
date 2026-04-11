namespace Daisi.Broski.Engine.Js;

/// <summary>
/// AST node types produced by <see cref="JsParser"/> for the ES5 core
/// grammar. The shape follows ESTree (https://github.com/estree/estree)
/// node-for-node so the spec can be read alongside the code, but we
/// use a sealed-class hierarchy instead of a <c>type</c>-string discriminator
/// so downstream consumers (the bytecode compiler, phase 3a slice 3) get
/// exhaustiveness from the C# type system rather than from spelling.
///
/// Every node carries its source <see cref="Start"/> / <see cref="End"/>
/// offsets (half-open, character indices into the original source text)
/// for diagnostics; we resolve line / column lazily on demand.
///
/// Phase 3a scope: every ES5 statement and expression form. Deferred:
///
/// - <b>Regex literals.</b> Same context-sensitivity problem as the
///   lexer — the parser will call a future <c>ReLex</c> entry point when
///   it's in a context where <c>/</c> means the start of a regex.
///   Parser currently sees <c>/</c> as division and will never produce
///   a <see cref="Literal"/> with a regex value.
/// - <b>ES2015+ forms</b> — arrow functions, template literals,
///   destructuring, <c>let</c>/<c>const</c> block scoping, classes.
///   Phase 3b. The lexer recognizes the keywords; the parser rejects
///   them for now with a descriptive error.
/// </summary>
public abstract class JsNode
{
    public int Start { get; }
    public int End { get; }

    protected JsNode(int start, int end)
    {
        Start = start;
        End = end;
    }
}

/// <summary>Base for every expression form.</summary>
public abstract class Expression : JsNode
{
    protected Expression(int start, int end) : base(start, end) { }
}

/// <summary>Base for every statement / declaration form.</summary>
public abstract class Statement : JsNode
{
    protected Statement(int start, int end) : base(start, end) { }
}

// ---------------------------------------------------------------------------
// Program root
// ---------------------------------------------------------------------------

/// <summary>
/// The top-level node returned by <see cref="JsParser.ParseProgram"/>.
/// <see cref="Body"/> is a flat list of statements and function
/// declarations — the parser does not separate them into a declaration
/// block, matching ESTree.
/// </summary>
public sealed class Program : JsNode
{
    public IReadOnlyList<Statement> Body { get; }

    public Program(int start, int end, IReadOnlyList<Statement> body) : base(start, end)
    {
        Body = body;
    }
}

// ---------------------------------------------------------------------------
// Literals and primary expressions
// ---------------------------------------------------------------------------

/// <summary>
/// Kind tag for <see cref="Literal"/>. We keep number / string / bool
/// / null as separate kinds so the compiler doesn't have to inspect the
/// boxed <see cref="Literal.Value"/> at every site.
/// </summary>
public enum LiteralKind
{
    Null,
    Boolean,
    Number,
    String,
}

/// <summary>
/// A primitive literal value — number, string, boolean, or null.
/// <see cref="Value"/> is boxed; the bytecode compiler reads
/// <see cref="Kind"/> first and then casts.
/// </summary>
public sealed class Literal : Expression
{
    public LiteralKind Kind { get; }
    public object? Value { get; }

    public Literal(int start, int end, LiteralKind kind, object? value) : base(start, end)
    {
        Kind = kind;
        Value = value;
    }
}

public sealed class Identifier : Expression
{
    public string Name { get; }

    public Identifier(int start, int end, string name) : base(start, end)
    {
        Name = name;
    }
}

public sealed class ThisExpression : Expression
{
    public ThisExpression(int start, int end) : base(start, end) { }
}

// ---------------------------------------------------------------------------
// Composite literal expressions
// ---------------------------------------------------------------------------

/// <summary>
/// Array literal. Holes (elisions) are represented as <c>null</c> entries
/// in <see cref="Elements"/>, matching ESTree's convention. Trailing commas
/// do not produce a hole — <c>[1, 2,]</c> has two elements, not three.
/// </summary>
public sealed class ArrayExpression : Expression
{
    public IReadOnlyList<Expression?> Elements { get; }

    public ArrayExpression(int start, int end, IReadOnlyList<Expression?> elements) : base(start, end)
    {
        Elements = elements;
    }
}

public enum PropertyKind
{
    Init,   // foo: bar
    Get,    // get foo() { ... }
    Set,    // set foo(v) { ... }
}

/// <summary>
/// One key/value slot in an object literal. The key is always a
/// <see cref="Literal"/> (number or string) or an
/// <see cref="Identifier"/> (bare name shorthand). ES5 allows keywords
/// as bare keys (<c>{ if: 1 }</c>); the parser accepts them and stores
/// them as <see cref="Identifier"/> instances.
/// </summary>
public sealed class Property : JsNode
{
    public Expression Key { get; }
    public Expression Value { get; }
    public PropertyKind Kind { get; }
    public bool Computed { get; } // ES5: always false; reserved for phase 3b

    public Property(int start, int end, Expression key, Expression value, PropertyKind kind) : base(start, end)
    {
        Key = key;
        Value = value;
        Kind = kind;
        Computed = false;
    }
}

public sealed class ObjectExpression : Expression
{
    public IReadOnlyList<Property> Properties { get; }

    public ObjectExpression(int start, int end, IReadOnlyList<Property> properties) : base(start, end)
    {
        Properties = properties;
    }
}

// ---------------------------------------------------------------------------
// Function expression / declaration
// ---------------------------------------------------------------------------

/// <summary>
/// Function body carrier for <see cref="FunctionDeclaration"/> and
/// <see cref="FunctionExpression"/>. ESTree calls this
/// <c>FunctionBody</c>; we reuse <see cref="BlockStatement"/> because
/// structurally it is identical and the compiler handles them the
/// same way.
/// </summary>
public sealed class FunctionExpression : Expression
{
    public Identifier? Id { get; }
    public IReadOnlyList<Identifier> Params { get; }
    public BlockStatement Body { get; }

    public FunctionExpression(
        int start,
        int end,
        Identifier? id,
        IReadOnlyList<Identifier> @params,
        BlockStatement body) : base(start, end)
    {
        Id = id;
        Params = @params;
        Body = body;
    }
}

/// <summary>
/// ES2015 arrow function expression. The body is either an
/// <see cref="Expression"/> (concise form, <c>x =&gt; x + 1</c>)
/// or a <see cref="BlockStatement"/> (block form,
/// <c>x =&gt; { ... }</c>). Arrow functions differ from
/// regular functions in two key ways the compiler and VM must
/// honor:
///
/// - <b>Lexical <c>this</c>.</b> Arrows don't bind their own
///   <c>this</c>; they capture it from the enclosing function
///   at the point the arrow is created.
/// - <b>No own <c>arguments</c>.</b> References to
///   <c>arguments</c> inside an arrow resolve to the enclosing
///   function's arguments object (via the env chain), not the
///   arrow's own call-site arguments.
///
/// Additionally, arrows cannot be used as constructors —
/// <c>new arrowFn()</c> throws <c>TypeError</c>.
/// </summary>
public sealed class ArrowFunctionExpression : Expression
{
    public IReadOnlyList<Identifier> Params { get; }
    public JsNode Body { get; }
    public bool IsExpressionBody => Body is Expression;

    public ArrowFunctionExpression(
        int start,
        int end,
        IReadOnlyList<Identifier> @params,
        JsNode body) : base(start, end)
    {
        Params = @params;
        Body = body;
    }
}

/// <summary>
/// ES2015 template literal — a backtick-delimited string with
/// optional <c>${expression}</c> interpolations. The AST
/// splits the source into alternating quasis (decoded string
/// parts) and expressions; the invariant is
/// <c>Quasis.Count == Expressions.Count + 1</c> (one more
/// quasi than expression, because quasis appear both before
/// the first expression and after the last one).
///
/// A no-interpolation template like <c>`hello`</c> has one
/// quasi and zero expressions. A <c>`a${x}b${y}c`</c> has
/// three quasis (<c>"a"</c>, <c>"b"</c>, <c>"c"</c>) and two
/// expressions.
///
/// Tagged templates (<c>tag`...`</c>) are a separate feature
/// deferred to a later slice.
/// </summary>
public sealed class TemplateLiteral : Expression
{
    public IReadOnlyList<string> Quasis { get; }
    public IReadOnlyList<Expression> Expressions { get; }

    public TemplateLiteral(
        int start,
        int end,
        IReadOnlyList<string> quasis,
        IReadOnlyList<Expression> expressions) : base(start, end)
    {
        Quasis = quasis;
        Expressions = expressions;
    }
}

public sealed class FunctionDeclaration : Statement
{
    public Identifier Id { get; }
    public IReadOnlyList<Identifier> Params { get; }
    public BlockStatement Body { get; }

    public FunctionDeclaration(
        int start,
        int end,
        Identifier id,
        IReadOnlyList<Identifier> @params,
        BlockStatement body) : base(start, end)
    {
        Id = id;
        Params = @params;
        Body = body;
    }
}

// ---------------------------------------------------------------------------
// Operator expressions
// ---------------------------------------------------------------------------

public enum UnaryOperator
{
    Minus,          // -x
    Plus,           // +x
    LogicalNot,     // !x
    BitwiseNot,     // ~x
    TypeOf,         // typeof x
    Void,           // void x
    Delete,         // delete x
}

public sealed class UnaryExpression : Expression
{
    public UnaryOperator Operator { get; }
    public Expression Argument { get; }

    public UnaryExpression(int start, int end, UnaryOperator op, Expression argument) : base(start, end)
    {
        Operator = op;
        Argument = argument;
    }
}

public enum UpdateOperator
{
    Increment, // ++
    Decrement, // --
}

/// <summary>
/// <c>++x</c> / <c>x++</c> / <c>--x</c> / <c>x--</c>.
/// <see cref="Prefix"/> distinguishes the two positions because they
/// evaluate differently (prefix returns the new value, postfix the old).
/// </summary>
public sealed class UpdateExpression : Expression
{
    public UpdateOperator Operator { get; }
    public Expression Argument { get; }
    public bool Prefix { get; }

    public UpdateExpression(int start, int end, UpdateOperator op, Expression argument, bool prefix) : base(start, end)
    {
        Operator = op;
        Argument = argument;
        Prefix = prefix;
    }
}

public enum BinaryOperator
{
    // Equality
    Equal,              // ==
    NotEqual,           // !=
    StrictEqual,        // ===
    StrictNotEqual,     // !==

    // Relational
    LessThan,           // <
    LessThanEqual,      // <=
    GreaterThan,        // >
    GreaterThanEqual,   // >=
    InstanceOf,         // instanceof
    In,                 // in

    // Shift
    LeftShift,          // <<
    RightShift,         // >>
    UnsignedRightShift, // >>>

    // Arithmetic
    Add,                // +
    Subtract,           // -
    Multiply,           // *
    Divide,             // /
    Modulo,             // %

    // Bitwise
    BitwiseAnd,         // &
    BitwiseOr,          // |
    BitwiseXor,         // ^
}

public sealed class BinaryExpression : Expression
{
    public BinaryOperator Operator { get; }
    public Expression Left { get; }
    public Expression Right { get; }

    public BinaryExpression(int start, int end, BinaryOperator op, Expression left, Expression right) : base(start, end)
    {
        Operator = op;
        Left = left;
        Right = right;
    }
}

public enum LogicalOperator
{
    And, // &&
    Or,  // ||
}

/// <summary>
/// Short-circuiting logical expression. Separated from
/// <see cref="BinaryExpression"/> because the compiler emits different
/// bytecode (jump-based evaluation) and the result type differs (the
/// operand that "won", not a boolean).
/// </summary>
public sealed class LogicalExpression : Expression
{
    public LogicalOperator Operator { get; }
    public Expression Left { get; }
    public Expression Right { get; }

    public LogicalExpression(int start, int end, LogicalOperator op, Expression left, Expression right) : base(start, end)
    {
        Operator = op;
        Left = left;
        Right = right;
    }
}

public enum AssignmentOperator
{
    Assign,             // =
    AddAssign,          // +=
    SubtractAssign,     // -=
    MultiplyAssign,     // *=
    DivideAssign,       // /=
    ModuloAssign,       // %=
    LeftShiftAssign,    // <<=
    RightShiftAssign,   // >>=
    UnsignedRightShiftAssign, // >>>=
    BitwiseAndAssign,   // &=
    BitwiseOrAssign,    // |=
    BitwiseXorAssign,   // ^=
}

public sealed class AssignmentExpression : Expression
{
    public AssignmentOperator Operator { get; }
    public Expression Left { get; }
    public Expression Right { get; }

    public AssignmentExpression(int start, int end, AssignmentOperator op, Expression left, Expression right) : base(start, end)
    {
        Operator = op;
        Left = left;
        Right = right;
    }
}

public sealed class ConditionalExpression : Expression
{
    public Expression Test { get; }
    public Expression Consequent { get; }
    public Expression Alternate { get; }

    public ConditionalExpression(int start, int end, Expression test, Expression consequent, Expression alternate) : base(start, end)
    {
        Test = test;
        Consequent = consequent;
        Alternate = alternate;
    }
}

/// <summary>
/// Property access — <c>obj.foo</c> (<see cref="Computed"/> = false) or
/// <c>obj[expr]</c> (<see cref="Computed"/> = true). For dotted access
/// <see cref="Property"/> is an <see cref="Identifier"/>; for computed
/// access it is any expression.
/// </summary>
public sealed class MemberExpression : Expression
{
    public Expression Object { get; }
    public Expression Property { get; }
    public bool Computed { get; }

    public MemberExpression(int start, int end, Expression @object, Expression property, bool computed) : base(start, end)
    {
        Object = @object;
        Property = property;
        Computed = computed;
    }
}

public sealed class CallExpression : Expression
{
    public Expression Callee { get; }
    public IReadOnlyList<Expression> Arguments { get; }

    public CallExpression(int start, int end, Expression callee, IReadOnlyList<Expression> arguments) : base(start, end)
    {
        Callee = callee;
        Arguments = arguments;
    }
}

public sealed class NewExpression : Expression
{
    public Expression Callee { get; }
    public IReadOnlyList<Expression> Arguments { get; }

    public NewExpression(int start, int end, Expression callee, IReadOnlyList<Expression> arguments) : base(start, end)
    {
        Callee = callee;
        Arguments = arguments;
    }
}

/// <summary>
/// Comma-operator expression: <c>a, b, c</c>. The result is the last
/// expression; the rest are evaluated for side effects. Only produced
/// in contexts where a comma is not the list separator (i.e. inside
/// parens, for-loop init/update, etc.).
/// </summary>
public sealed class SequenceExpression : Expression
{
    public IReadOnlyList<Expression> Expressions { get; }

    public SequenceExpression(int start, int end, IReadOnlyList<Expression> expressions) : base(start, end)
    {
        Expressions = expressions;
    }
}

// ---------------------------------------------------------------------------
// Simple statements
// ---------------------------------------------------------------------------

public sealed class EmptyStatement : Statement
{
    public EmptyStatement(int start, int end) : base(start, end) { }
}

public sealed class DebuggerStatement : Statement
{
    public DebuggerStatement(int start, int end) : base(start, end) { }
}

public sealed class ExpressionStatement : Statement
{
    public Expression Expression { get; }

    public ExpressionStatement(int start, int end, Expression expression) : base(start, end)
    {
        Expression = expression;
    }
}

public sealed class BlockStatement : Statement
{
    public IReadOnlyList<Statement> Body { get; }

    public BlockStatement(int start, int end, IReadOnlyList<Statement> body) : base(start, end)
    {
        Body = body;
    }
}

// ---------------------------------------------------------------------------
// Variable declarations
// ---------------------------------------------------------------------------

public enum VariableDeclarationKind
{
    Var,   // ES5
    Let,   // ES2015 (accepted by parser, rejected by later phases)
    Const, // ES2015
}

public sealed class VariableDeclarator : JsNode
{
    public Identifier Id { get; }
    public Expression? Init { get; }

    public VariableDeclarator(int start, int end, Identifier id, Expression? init) : base(start, end)
    {
        Id = id;
        Init = init;
    }
}

/// <summary>
/// <c>var a = 1, b = 2;</c>. Also appears inside <c>for</c> and
/// <c>for (var x in obj)</c> headers as a non-statement use.
/// </summary>
public sealed class VariableDeclaration : Statement
{
    public VariableDeclarationKind Kind { get; }
    public IReadOnlyList<VariableDeclarator> Declarations { get; }

    public VariableDeclaration(
        int start,
        int end,
        VariableDeclarationKind kind,
        IReadOnlyList<VariableDeclarator> declarations) : base(start, end)
    {
        Kind = kind;
        Declarations = declarations;
    }
}

// ---------------------------------------------------------------------------
// Control flow
// ---------------------------------------------------------------------------

public sealed class IfStatement : Statement
{
    public Expression Test { get; }
    public Statement Consequent { get; }
    public Statement? Alternate { get; }

    public IfStatement(int start, int end, Expression test, Statement consequent, Statement? alternate) : base(start, end)
    {
        Test = test;
        Consequent = consequent;
        Alternate = alternate;
    }
}

public sealed class WhileStatement : Statement
{
    public Expression Test { get; }
    public Statement Body { get; }

    public WhileStatement(int start, int end, Expression test, Statement body) : base(start, end)
    {
        Test = test;
        Body = body;
    }
}

public sealed class DoWhileStatement : Statement
{
    public Statement Body { get; }
    public Expression Test { get; }

    public DoWhileStatement(int start, int end, Statement body, Expression test) : base(start, end)
    {
        Body = body;
        Test = test;
    }
}

/// <summary>
/// C-style <c>for</c> loop. All three header slots are optional;
/// <see cref="Init"/> may be a <see cref="VariableDeclaration"/>
/// (no trailing semicolon) or an <see cref="Expression"/>.
/// </summary>
public sealed class ForStatement : Statement
{
    public JsNode? Init { get; } // VariableDeclaration or Expression or null
    public Expression? Test { get; }
    public Expression? Update { get; }
    public Statement Body { get; }

    public ForStatement(
        int start,
        int end,
        JsNode? init,
        Expression? test,
        Expression? update,
        Statement body) : base(start, end)
    {
        Init = init;
        Test = test;
        Update = update;
        Body = body;
    }
}

/// <summary>
/// <c>for (x in obj)</c> and <c>for (var x in obj)</c>. <see cref="Left"/>
/// is either a <see cref="VariableDeclaration"/> with exactly one
/// declarator (ES5 forbids multiple) or an arbitrary expression that must
/// be a valid assignment target.
/// </summary>
public sealed class ForInStatement : Statement
{
    public JsNode Left { get; }
    public Expression Right { get; }
    public Statement Body { get; }

    public ForInStatement(int start, int end, JsNode left, Expression right, Statement body) : base(start, end)
    {
        Left = left;
        Right = right;
        Body = body;
    }
}

public sealed class BreakStatement : Statement
{
    public Identifier? Label { get; }

    public BreakStatement(int start, int end, Identifier? label) : base(start, end)
    {
        Label = label;
    }
}

public sealed class ContinueStatement : Statement
{
    public Identifier? Label { get; }

    public ContinueStatement(int start, int end, Identifier? label) : base(start, end)
    {
        Label = label;
    }
}

public sealed class ReturnStatement : Statement
{
    public Expression? Argument { get; }

    public ReturnStatement(int start, int end, Expression? argument) : base(start, end)
    {
        Argument = argument;
    }
}

public sealed class ThrowStatement : Statement
{
    public Expression Argument { get; }

    public ThrowStatement(int start, int end, Expression argument) : base(start, end)
    {
        Argument = argument;
    }
}

public sealed class CatchClause : JsNode
{
    public Identifier Param { get; }
    public BlockStatement Body { get; }

    public CatchClause(int start, int end, Identifier param, BlockStatement body) : base(start, end)
    {
        Param = param;
        Body = body;
    }
}

public sealed class TryStatement : Statement
{
    public BlockStatement Block { get; }
    public CatchClause? Handler { get; }
    public BlockStatement? Finalizer { get; }

    public TryStatement(
        int start,
        int end,
        BlockStatement block,
        CatchClause? handler,
        BlockStatement? finalizer) : base(start, end)
    {
        Block = block;
        Handler = handler;
        Finalizer = finalizer;
    }
}

/// <summary>
/// One <c>case</c> clause inside a switch. <see cref="Test"/> is null
/// for the <c>default</c> clause. <see cref="Consequent"/> is the list
/// of statements up to the next case / closing brace — ES5 allows
/// fallthrough, so the consequent does not include an implicit break.
/// </summary>
public sealed class SwitchCase : JsNode
{
    public Expression? Test { get; }
    public IReadOnlyList<Statement> Consequent { get; }

    public SwitchCase(int start, int end, Expression? test, IReadOnlyList<Statement> consequent) : base(start, end)
    {
        Test = test;
        Consequent = consequent;
    }
}

public sealed class SwitchStatement : Statement
{
    public Expression Discriminant { get; }
    public IReadOnlyList<SwitchCase> Cases { get; }

    public SwitchStatement(int start, int end, Expression discriminant, IReadOnlyList<SwitchCase> cases) : base(start, end)
    {
        Discriminant = discriminant;
        Cases = cases;
    }
}

public sealed class LabeledStatement : Statement
{
    public Identifier Label { get; }
    public Statement Body { get; }

    public LabeledStatement(int start, int end, Identifier label, Statement body) : base(start, end)
    {
        Label = label;
        Body = body;
    }
}

/// <summary>
/// <c>with (obj) { ... }</c>. Forbidden in strict mode; the parser
/// accepts it unconditionally and the later semantic pass rejects it
/// when running in strict mode.
/// </summary>
public sealed class WithStatement : Statement
{
    public Expression Object { get; }
    public Statement Body { get; }

    public WithStatement(int start, int end, Expression @object, Statement body) : base(start, end)
    {
        Object = @object;
        Body = body;
    }
}
