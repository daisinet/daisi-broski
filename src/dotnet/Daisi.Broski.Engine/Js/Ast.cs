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
    /// <summary>
    /// Elements of the array literal. <c>null</c> entries
    /// represent elisions (<c>[1, , 3]</c>). A spread element
    /// (<c>[...arr]</c>) is modeled as a
    /// <see cref="SpreadElement"/>; the compiler recognizes
    /// it and emits an append-spread opcode instead of a
    /// straight push.
    /// </summary>
    public IReadOnlyList<Expression?> Elements { get; }

    public ArrayExpression(int start, int end, IReadOnlyList<Expression?> elements) : base(start, end)
    {
        Elements = elements;
    }
}

/// <summary>
/// ES2015 spread element <c>...expr</c>. Appears as an
/// <see cref="ArrayExpression"/> element and as a
/// <see cref="CallExpression"/> / <see cref="NewExpression"/>
/// argument. The compiler's array-literal and call lowerings
/// recognize this node and emit the runtime-flatten opcodes
/// (<see cref="Js.OpCode.ArrayAppendSpread"/> /
/// <see cref="Js.OpCode.CallSpread"/>) instead of pushing
/// the expression value directly as a single slot.
///
/// Spread on object literals (<c>{...obj}</c>) is ES2018 and
/// deferred.
/// </summary>
public sealed class SpreadElement : Expression
{
    public Expression Argument { get; }

    public SpreadElement(int start, int end, Expression argument) : base(start, end)
    {
        Argument = argument;
    }
}

public enum PropertyKind
{
    Init,   // foo: bar
    Get,    // get foo() { ... }
    Set,    // set foo(v) { ... }
}

/// <summary>
/// One key/value slot in an object literal. The key is a
/// <see cref="Literal"/> (number or string) or an
/// <see cref="Identifier"/> (bare name shorthand). ES5 allows
/// keywords as bare keys (<c>{ if: 1 }</c>); the parser accepts
/// them and stores them as <see cref="Identifier"/> instances.
/// The ES2018 spread form <c>{...source}</c> is represented
/// by setting <see cref="IsSpread"/> to true — in that case
/// <see cref="Key"/> is a placeholder (the spread source is
/// stored in <see cref="Value"/>).
/// </summary>
public sealed class Property : JsNode
{
    public Expression Key { get; }
    public Expression Value { get; }
    public PropertyKind Kind { get; }
    public bool Computed { get; } // ES5: always false; reserved for phase 3b
    /// <summary>
    /// <c>true</c> for an ES2018 <c>{ ...source }</c> spread
    /// entry. When set, <see cref="Value"/> is the source
    /// expression whose own enumerable properties get copied
    /// into the literal; <see cref="Key"/> is unused.
    /// </summary>
    public bool IsSpread { get; }

    public Property(int start, int end, Expression key, Expression value, PropertyKind kind, bool isSpread = false) : base(start, end)
    {
        Key = key;
        Value = value;
        Kind = kind;
        Computed = false;
        IsSpread = isSpread;
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
    public IReadOnlyList<FunctionParameter> Params { get; }
    public BlockStatement Body { get; }
    /// <summary>
    /// <c>true</c> for ES2015 generator functions
    /// (<c>function* foo(){ yield 1; }</c>). Carried all the
    /// way into <see cref="JsFunctionTemplate"/> so the VM's
    /// call machinery can route to the generator-object
    /// path instead of actually running the body.
    /// </summary>
    public bool IsGenerator { get; }
    /// <summary>
    /// <c>true</c> for ES2017 async functions
    /// (<c>async function foo(){ await x; }</c>). Async
    /// functions compile like generators under the hood and
    /// the VM's call machinery wraps them in a Promise +
    /// auto-stepping loop.
    /// </summary>
    public bool IsAsync { get; }

    public FunctionExpression(
        int start,
        int end,
        Identifier? id,
        IReadOnlyList<FunctionParameter> @params,
        BlockStatement body,
        bool isGenerator = false,
        bool isAsync = false) : base(start, end)
    {
        Id = id;
        Params = @params;
        Body = body;
        IsGenerator = isGenerator;
        IsAsync = isAsync;
    }
}

/// <summary>
/// One formal parameter in a function declaration, function
/// expression, or arrow function. Represents the whole
/// <c>target [= default]</c> form, plus the ES2015 rest
/// parameter marker.
///
/// - <b>Target</b> is currently always an <see cref="Js.Identifier"/>;
///   function-parameter destructuring is a deferred slice.
/// - <b>Default</b> is the optional initializer expression for
///   an ES2015 default parameter (<c>function f(x = 1)</c>).
///   Applied when the caller passed <c>undefined</c> (or
///   nothing) for the slot.
/// - <b>IsRest</b> flags the ES2015 rest parameter
///   (<c>function f(...args)</c>). The parser enforces that
///   a rest parameter must be the last entry and may not
///   carry a default.
/// </summary>
public sealed class FunctionParameter : JsNode
{
    public JsNode Target { get; }
    public Expression? Default { get; }
    public bool IsRest { get; }

    public FunctionParameter(
        int start,
        int end,
        JsNode target,
        Expression? @default,
        bool isRest) : base(start, end)
    {
        Target = target;
        Default = @default;
        IsRest = isRest;
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
    public IReadOnlyList<FunctionParameter> Params { get; }
    public JsNode Body { get; }
    public bool IsExpressionBody => Body is Expression;
    /// <summary>
    /// <c>true</c> for <c>async (...) =&gt; body</c> and
    /// <c>async x =&gt; body</c>. Arrow generators
    /// (<c>async * () =&gt; ...</c>) are not valid in
    /// ES2017+ anyway.
    /// </summary>
    public bool IsAsync { get; }

    public ArrowFunctionExpression(
        int start,
        int end,
        IReadOnlyList<FunctionParameter> @params,
        JsNode body,
        bool isAsync = false) : base(start, end)
    {
        Params = @params;
        Body = body;
        IsAsync = isAsync;
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

/// <summary>
/// Tagged template — <c>tag`str ${expr} str`</c>. Lowered
/// by the compiler to a call <c>tag(strings, ...exprs)</c>
/// where <c>strings</c> is an array of the literal string
/// parts (cooked form). Proper <c>.raw</c> property
/// support is a documented deferral.
/// </summary>
public sealed class TaggedTemplateExpression : Expression
{
    public Expression Tag { get; }
    public TemplateLiteral Quasi { get; }

    public TaggedTemplateExpression(int start, int end, Expression tag, TemplateLiteral quasi) : base(start, end)
    {
        Tag = tag;
        Quasi = quasi;
    }
}

public sealed class FunctionDeclaration : Statement
{
    public Identifier Id { get; }
    public IReadOnlyList<FunctionParameter> Params { get; }
    public BlockStatement Body { get; }
    public bool IsGenerator { get; }
    public bool IsAsync { get; }

    public FunctionDeclaration(
        int start,
        int end,
        Identifier id,
        IReadOnlyList<FunctionParameter> @params,
        BlockStatement body,
        bool isGenerator = false,
        bool isAsync = false) : base(start, end)
    {
        Id = id;
        Params = @params;
        Body = body;
        IsGenerator = isGenerator;
        IsAsync = isAsync;
    }
}

/// <summary>
/// ES2017 <c>await expr</c> expression — only valid inside
/// an async function body. At compile time, await lowers
/// to the same bytecode pattern as <c>yield</c>
/// (<c>YieldValue</c> + <c>YieldResume</c>); at runtime,
/// the async driver wraps the yielded value in a promise
/// and resumes the generator with the fulfilled value (or
/// throws the rejection into the generator via the yield
/// resume's throw mode).
/// </summary>
public sealed class AwaitExpression : Expression
{
    public Expression Argument { get; }

    public AwaitExpression(int start, int end, Expression argument) : base(start, end)
    {
        Argument = argument;
    }
}

// ---------------------------------------------------------------------------
// ES2015 modules — import / export declarations
// ---------------------------------------------------------------------------

/// <summary>
/// One specifier inside an <c>import { a, b as c } from "..."</c>
/// list. <see cref="Imported"/> is the name as it appears in
/// the source module; <see cref="Local"/> is the local name
/// it's bound to in the importing module.
/// </summary>
public sealed class ImportSpecifier : JsNode
{
    public string Imported { get; }
    public string Local { get; }
    /// <summary><c>true</c> for <c>import X from "..."</c>
    /// (default import). <see cref="Imported"/> is unused
    /// in that case; the value comes from the source
    /// module's <c>default</c> export.</summary>
    public bool IsDefault { get; }
    /// <summary><c>true</c> for
    /// <c>import * as ns from "..."</c>. The local name
    /// binds the entire exports namespace object.</summary>
    public bool IsNamespace { get; }

    public ImportSpecifier(
        int start,
        int end,
        string imported,
        string local,
        bool isDefault = false,
        bool isNamespace = false) : base(start, end)
    {
        Imported = imported;
        Local = local;
        IsDefault = isDefault;
        IsNamespace = isNamespace;
    }
}

/// <summary>
/// ES2015 <c>import ... from "source"</c> declaration. A
/// zero-specifier import (<c>import "./side-effects"</c>)
/// has an empty <see cref="Specifiers"/> list and still
/// causes the module loader to evaluate the source module
/// for its side effects.
/// </summary>
public sealed class ImportDeclaration : Statement
{
    public IReadOnlyList<ImportSpecifier> Specifiers { get; }
    public string Source { get; }

    public ImportDeclaration(
        int start,
        int end,
        IReadOnlyList<ImportSpecifier> specifiers,
        string source) : base(start, end)
    {
        Specifiers = specifiers;
        Source = source;
    }
}

public sealed class ExportSpecifier : JsNode
{
    public string Local { get; }
    public string Exported { get; }

    public ExportSpecifier(int start, int end, string local, string exported) : base(start, end)
    {
        Local = local;
        Exported = exported;
    }
}

/// <summary>
/// <c>export const x = 1;</c>, <c>export function f() {}</c>,
/// <c>export class C {}</c>, or a specifier list
/// <c>export { a, b as c };</c>. When
/// <see cref="Declaration"/> is non-null,
/// <see cref="Specifiers"/> is empty and the names introduced
/// by the declaration are exported under those same names.
/// When <see cref="Specifiers"/> is non-empty,
/// <see cref="Declaration"/> is null and the specifier list
/// re-exports already-bound identifiers.
/// </summary>
public sealed class ExportNamedDeclaration : Statement
{
    public Statement? Declaration { get; }
    public IReadOnlyList<ExportSpecifier> Specifiers { get; }
    /// <summary>
    /// Source module for a re-export of the form
    /// <c>export { a, b as c } from './mod'</c>. Null for
    /// the non-re-export forms. The named specifiers still
    /// live in <see cref="Specifiers"/>; the loader reads
    /// each specifier's <c>Local</c> from the source
    /// module's exports and exports it under the
    /// <c>Exported</c> name.
    /// </summary>
    public string? Source { get; }

    public ExportNamedDeclaration(
        int start,
        int end,
        Statement? declaration,
        IReadOnlyList<ExportSpecifier> specifiers,
        string? source = null) : base(start, end)
    {
        Declaration = declaration;
        Specifiers = specifiers;
        Source = source;
    }
}

/// <summary>
/// <c>export * from './mod'</c> / <c>export * as ns from './mod'</c>.
/// The former copies every named export from the source
/// module into the current module's exports; the latter
/// wraps them under <see cref="Namespace"/>.
/// </summary>
public sealed class ExportAllDeclaration : Statement
{
    public string Source { get; }
    /// <summary>
    /// <c>null</c> for a wildcard re-export; a string for
    /// <c>export * as ns from '...'</c>.
    /// </summary>
    public string? Namespace { get; }

    public ExportAllDeclaration(int start, int end, string source, string? @namespace) : base(start, end)
    {
        Source = source;
        Namespace = @namespace;
    }
}

/// <summary>
/// <c>export default expr;</c> — binds the expression's
/// value to the module's <c>default</c> export slot.
/// </summary>
public sealed class ExportDefaultDeclaration : Statement
{
    public JsNode Declaration { get; }

    public ExportDefaultDeclaration(int start, int end, JsNode declaration) : base(start, end)
    {
        Declaration = declaration;
    }
}

/// <summary>
/// ES2015 <c>yield</c> expression — only legal inside a
/// generator function body. May appear with an argument
/// (<c>yield x</c>) or without (<c>yield</c>, equivalent to
/// <c>yield undefined</c>). Delegation (<c>yield*</c>) is
/// not yet supported.
///
/// At runtime, evaluating a yield expression suspends the
/// generator: the argument value becomes the result returned
/// from <c>gen.next()</c>, and the value subsequently passed
/// to <c>gen.next(sent)</c> becomes the value of the yield
/// expression itself. Both directions round-trip through the
/// compiler's <c>YieldValue</c> / <c>YieldResume</c> opcode
/// pair.
/// </summary>
public sealed class YieldExpression : Expression
{
    public Expression? Argument { get; }
    /// <summary>
    /// <c>true</c> for <c>yield* expr</c> — the argument
    /// is an iterable whose values are re-yielded one at a
    /// time by the enclosing generator. Mandatory
    /// <see cref="Argument"/> when set.
    /// </summary>
    public bool Delegate { get; }

    public YieldExpression(int start, int end, Expression? argument, bool @delegate = false) : base(start, end)
    {
        Argument = argument;
        Delegate = @delegate;
    }
}

// ---------------------------------------------------------------------------
// ES2015 classes
// ---------------------------------------------------------------------------

/// <summary>
/// Kind of a <see cref="MethodDefinition"/> — the class
/// body's constructor method, a regular prototype method, or
/// (in future slices) a getter/setter accessor. Phase 3b-6
/// only accepts <see cref="Constructor"/> and
/// <see cref="Method"/>.
/// </summary>
public enum MethodDefinitionKind
{
    Constructor,
    Method,
    Get,
    Set,
}

/// <summary>
/// One method entry inside a <see cref="ClassBody"/>. The
/// value is always a synthesized <see cref="FunctionExpression"/>
/// — the parser builds it from the <c>name(params){body}</c>
/// source. <see cref="IsStatic"/> is set for <c>static method()</c>
/// entries; they install on the class function itself
/// rather than on <c>class.prototype</c>.
/// </summary>
public sealed class MethodDefinition : JsNode
{
    public Identifier Key { get; }
    public FunctionExpression Value { get; }
    public MethodDefinitionKind Kind { get; }
    public bool IsStatic { get; }

    public MethodDefinition(
        int start,
        int end,
        Identifier key,
        FunctionExpression value,
        MethodDefinitionKind kind,
        bool isStatic) : base(start, end)
    {
        Key = key;
        Value = value;
        Kind = kind;
        IsStatic = isStatic;
    }
}

/// <summary>
/// Body of a class declaration or class expression — lists
/// of method definitions and (ES2022) field definitions.
/// Semicolons between entries are allowed and ignored per
/// spec. The compiler installs methods first, then static
/// fields; instance fields are a deferred slice item.
/// </summary>
public sealed class ClassBody : JsNode
{
    public IReadOnlyList<MethodDefinition> Methods { get; }
    public IReadOnlyList<ClassField> Fields { get; }

    public ClassBody(
        int start,
        int end,
        IReadOnlyList<MethodDefinition> methods,
        IReadOnlyList<ClassField>? fields = null) : base(start, end)
    {
        Methods = methods;
        Fields = fields ?? Array.Empty<ClassField>();
    }
}

/// <summary>
/// ES2022 class field declaration — <c>static name = expr;</c>
/// or (deferred) <c>name = expr;</c>. For now we only
/// support the <c>static</c> form; instance fields require
/// constructor-time initialization and are a later slice.
/// </summary>
public sealed class ClassField : JsNode
{
    public string Name { get; }
    public Expression? Initializer { get; }
    public bool IsStatic { get; }

    public ClassField(
        int start,
        int end,
        string name,
        Expression? initializer,
        bool isStatic) : base(start, end)
    {
        Name = name;
        Initializer = initializer;
        IsStatic = isStatic;
    }
}

/// <summary>
/// ES2015 class declaration — <c>class Foo [extends Bar]
/// { methods }</c>. Compiled as a block-scoped binding (like
/// <c>let</c>) whose initializer runs the class-assembly
/// bytecode.
/// </summary>
public sealed class ClassDeclaration : Statement
{
    public Identifier Id { get; }
    public Expression? SuperClass { get; }
    public ClassBody Body { get; }

    public ClassDeclaration(
        int start,
        int end,
        Identifier id,
        Expression? superClass,
        ClassBody body) : base(start, end)
    {
        Id = id;
        SuperClass = superClass;
        Body = body;
    }
}

/// <summary>
/// ES2015 class expression — <c>class [Name] [extends Bar]
/// { methods }</c>. Produces the class function value;
/// the optional <see cref="Id"/> is visible inside the
/// class body for self-reference (deferred; treated as
/// anonymous in this slice).
/// </summary>
public sealed class ClassExpression : Expression
{
    public Identifier? Id { get; }
    public Expression? SuperClass { get; }
    public ClassBody Body { get; }

    public ClassExpression(
        int start,
        int end,
        Identifier? id,
        Expression? superClass,
        ClassBody body) : base(start, end)
    {
        Id = id;
        SuperClass = superClass;
        Body = body;
    }
}

/// <summary>
/// The <c>super</c> primary expression. Only legal as:
/// <list type="bullet">
/// <item>the callee of a <see cref="CallExpression"/>
///   (<c>super(args)</c>, a super-constructor call) inside a
///   derived-class constructor</item>
/// <item>the <c>object</c> of a <see cref="MemberExpression"/>
///   (<c>super.method(args)</c>) inside any class method</item>
/// </list>
/// All other uses are rejected at compile time.
/// </summary>
public sealed class Super : Expression
{
    public Super(int start, int end) : base(start, end) { }
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
    And,     // &&
    Or,      // ||
    Nullish, // ?? (ES2020)
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
    // ES2021 short-circuit assignment.
    LogicalAndAssign,   // &&=
    LogicalOrAssign,    // ||=
    NullishAssign,      // ??=
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
    /// <summary>
    /// True when this step was written with <c>?.</c> — the
    /// containing <see cref="ChainExpression"/> short-circuits
    /// the whole chain to <c>undefined</c> if <see cref="Object"/>
    /// evaluates to <c>null</c> or <c>undefined</c>.
    /// </summary>
    public bool IsOptional { get; }

    public MemberExpression(int start, int end, Expression @object, Expression property, bool computed, bool isOptional = false) : base(start, end)
    {
        Object = @object;
        Property = property;
        Computed = computed;
        IsOptional = isOptional;
    }
}

public sealed class CallExpression : Expression
{
    public Expression Callee { get; }
    public IReadOnlyList<Expression> Arguments { get; }
    /// <summary>
    /// True when this call was written with <c>?.(</c> — the
    /// containing <see cref="ChainExpression"/> short-circuits
    /// the whole chain to <c>undefined</c> if <see cref="Callee"/>
    /// evaluates to <c>null</c> or <c>undefined</c>.
    /// </summary>
    public bool IsOptional { get; }

    public CallExpression(int start, int end, Expression callee, IReadOnlyList<Expression> arguments, bool isOptional = false) : base(start, end)
    {
        Callee = callee;
        Arguments = arguments;
        IsOptional = isOptional;
    }
}

/// <summary>
/// Wraps the top of an optional chain (<c>?.</c> / <c>?.(</c> /
/// <c>?.[</c>). Marks the scope within which a nullish short-
/// circuit unwinds to <c>undefined</c>. Inside the expression,
/// <see cref="MemberExpression.IsOptional"/> and
/// <see cref="CallExpression.IsOptional"/> flag each hop that
/// may trigger the short-circuit — the nearest enclosing
/// <c>ChainExpression</c> is where they all jump to.
/// </summary>
public sealed class ChainExpression : Expression
{
    public Expression Expression { get; }

    public ChainExpression(int start, int end, Expression expression) : base(start, end)
    {
        Expression = expression;
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
    /// <summary>
    /// The left-hand side of the declarator. May be an
    /// <see cref="Identifier"/> (simple binding: <c>var x</c>),
    /// an <see cref="ObjectPattern"/> (object destructuring:
    /// <c>var {a, b} = obj</c>), or an <see cref="ArrayPattern"/>
    /// (array destructuring: <c>var [x, y] = arr</c>).
    /// </summary>
    public JsNode Id { get; }
    public Expression? Init { get; }

    public VariableDeclarator(int start, int end, JsNode id, Expression? init) : base(start, end)
    {
        Id = id;
        Init = init;
    }
}

// ---------------------------------------------------------------------------
// Destructuring patterns
// ---------------------------------------------------------------------------

/// <summary>
/// Object destructuring pattern — <c>{a, b: x, c = 1}</c>.
/// Used as the left-hand side of a variable declarator for
/// destructuring assignment. Each property has a source-side
/// <see cref="ObjectPatternProperty.Key"/>, a target-side
/// binding (which may itself be another pattern, for nesting),
/// and an optional default expression used when the source
/// property is <c>undefined</c>.
/// </summary>
public sealed class ObjectPattern : JsNode
{
    public IReadOnlyList<ObjectPatternProperty> Properties { get; }

    public ObjectPattern(
        int start,
        int end,
        IReadOnlyList<ObjectPatternProperty> properties) : base(start, end)
    {
        Properties = properties;
    }
}

public sealed class ObjectPatternProperty : JsNode
{
    /// <summary>Property name to read from the source object.</summary>
    public string Key { get; }

    /// <summary>
    /// Binding target. For <c>{a}</c> and <c>{a = 1}</c> this
    /// is an <see cref="Identifier"/> with the same name as
    /// <see cref="Key"/> (shorthand). For <c>{a: x}</c> it's
    /// an <see cref="Identifier"/> with a different name. For
    /// <c>{a: {b}}</c> it's an inner <see cref="ObjectPattern"/>
    /// or <see cref="ArrayPattern"/>.
    /// </summary>
    public JsNode Value { get; }

    /// <summary>
    /// Default expression used when the source property
    /// resolves to <c>undefined</c>. Evaluated lazily — only
    /// if the default is actually needed at runtime.
    /// </summary>
    public Expression? Default { get; }

    /// <summary>
    /// <c>true</c> for an ES2018 <c>{a, ...rest}</c> trailing
    /// rest element. <see cref="Key"/> is unused when set;
    /// <see cref="Value"/> is the <see cref="Identifier"/>
    /// that binds the collected leftover properties as a
    /// fresh object. The parser enforces that a rest
    /// property is the last entry in the object pattern.
    /// </summary>
    public bool IsRest { get; }

    public ObjectPatternProperty(
        int start,
        int end,
        string key,
        JsNode value,
        Expression? @default,
        bool isRest = false) : base(start, end)
    {
        Key = key;
        Value = value;
        Default = @default;
        IsRest = isRest;
    }
}

/// <summary>
/// Array destructuring pattern — <c>[a, b = 1, [c, d]]</c>.
/// Elements may be <c>null</c> to represent an elision
/// (<c>[a, , c]</c> skips the middle slot).
/// </summary>
public sealed class ArrayPattern : JsNode
{
    public IReadOnlyList<ArrayPatternElement?> Elements { get; }

    public ArrayPattern(
        int start,
        int end,
        IReadOnlyList<ArrayPatternElement?> elements) : base(start, end)
    {
        Elements = elements;
    }
}

public sealed class ArrayPatternElement : JsNode
{
    public JsNode Target { get; }
    public Expression? Default { get; }

    /// <summary>
    /// <c>true</c> if this element captures the tail of the
    /// source as a fresh array, i.e. the ES2015
    /// <c>var [a, ...rest] = arr</c> form. The parser
    /// enforces that a rest element must be the last entry
    /// and may not carry a default.
    /// </summary>
    public bool IsRest { get; }

    public ArrayPatternElement(
        int start,
        int end,
        JsNode target,
        Expression? @default,
        bool isRest = false) : base(start, end)
    {
        Target = target;
        Default = @default;
        IsRest = isRest;
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

/// <summary>
/// ES2015 <c>for (lhs of iter) body</c> — drives iteration
/// through the iterator protocol (<c>iter[Symbol.iterator]()</c>,
/// then repeated <c>next()</c> calls). The <c>lhs</c> is either
/// a <see cref="VariableDeclaration"/> introducing a fresh
/// binding or an existing assignment target expression; in
/// both cases the compiler lowers it to the iteration binding
/// pattern at each step.
/// </summary>
public sealed class ForOfStatement : Statement
{
    public JsNode Left { get; }
    public Expression Right { get; }
    public Statement Body { get; }

    public ForOfStatement(int start, int end, JsNode left, Expression right, Statement body) : base(start, end)
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
