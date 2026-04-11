namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Every lexical token kind produced by <see cref="JsLexer"/>, covering
/// the ES5 core plus the ES2015+ keywords we know about. Phase 3a ships
/// a lexer that recognizes all of these; the parser (phase 3a cont'd)
/// decides which combinations are actually legal in its current grammar
/// mode (strict, module, etc.).
///
/// The enum is split into loose sections so it's easy to iterate one
/// category (e.g. "is this any keyword?") via range checks in the few
/// places that care.
/// </summary>
public enum JsTokenKind
{
    // Terminators / synthetic
    Unknown = 0,
    EndOfFile,

    // Literals
    Identifier,
    NumberLiteral,
    StringLiteral,
    NullLiteral,
    TrueLiteral,
    FalseLiteral,
    /// <summary>
    /// Backtick-delimited template literal with no
    /// interpolations (e.g. <c>`hello`</c>). The
    /// <see cref="JsToken.StringValue"/> is the fully-decoded
    /// string content (escapes resolved, CRLF normalized to LF).
    /// </summary>
    NoSubstitutionTemplate,
    /// <summary>
    /// Opening portion of a template literal with one or more
    /// interpolations — from the opening backtick up to (but
    /// not including) the first <c>${</c>. Followed by
    /// expression tokens and a <see cref="TemplateMiddle"/> or
    /// <see cref="TemplateTail"/>.
    /// </summary>
    TemplateHead,
    /// <summary>
    /// Middle portion of a template literal — from a <c>}</c>
    /// closing an interpolation up to the next <c>${</c>.
    /// </summary>
    TemplateMiddle,
    /// <summary>
    /// Tail portion of a template literal — from a <c>}</c>
    /// closing the last interpolation up to the closing
    /// backtick.
    /// </summary>
    TemplateTail,
    // RegexLiteral, BigIntLiteral — deferred

    // Punctuators (in roughly the order they appear in the spec)
    LeftParen,          // (
    RightParen,         // )
    LeftBrace,          // {
    RightBrace,         // }
    LeftBracket,        // [
    RightBracket,       // ]
    Dot,                // .
    DotDotDot,          // ...
    Semicolon,          // ;
    Comma,              // ,
    QuestionMark,       // ?
    QuestionDot,        // ?. (ES2020 optional chaining)
    QuestionQuestion,   // ?? (ES2020 nullish coalescing)
    Colon,              // :
    Arrow,              // =>  (ES2015; recognized, parser can reject if needed)

    // Relational
    LessThan,           // <
    GreaterThan,        // >
    LessThanEqual,      // <=
    GreaterThanEqual,   // >=

    // Equality
    Equal,              // ==
    NotEqual,           // !=
    StrictEqual,        // ===
    StrictNotEqual,     // !==

    // Arithmetic
    Plus,               // +
    Minus,              // -
    Star,               // *
    Slash,              // /
    Percent,            // %
    PlusPlus,           // ++
    MinusMinus,         // --

    // Bitwise shift
    LeftShift,          // <<
    RightShift,         // >>
    UnsignedRightShift, // >>>

    // Bitwise
    Ampersand,          // &
    Pipe,               // |
    Caret,              // ^
    Tilde,              // ~

    // Logical
    AmpersandAmpersand, // &&
    PipePipe,           // ||
    Exclamation,        // !

    // Assignment
    Assign,             // =
    PlusAssign,         // +=
    MinusAssign,        // -=
    StarAssign,         // *=
    SlashAssign,        // /=
    PercentAssign,      // %=
    LeftShiftAssign,    // <<=
    RightShiftAssign,   // >>=
    UnsignedRightShiftAssign, // >>>=
    AmpersandAssign,    // &=
    PipeAssign,         // |=
    CaretAssign,        // ^=
    // ES2021 short-circuit assignment operators.
    AmpersandAmpersandAssign, // &&=
    PipePipeAssign,           // ||=
    QuestionQuestionAssign,   // ??=

    // --- Keywords (ES5) ---
    // Kept as their own enum values so the parser doesn't have to do
    // string comparisons for the hot-path keyword checks. The lexer
    // disambiguates identifier vs keyword via a small lookup table.
    KeywordBreak,
    KeywordCase,
    KeywordCatch,
    KeywordContinue,
    KeywordDebugger,
    KeywordDefault,
    KeywordDelete,
    KeywordDo,
    KeywordElse,
    KeywordFinally,
    KeywordFor,
    KeywordFunction,
    KeywordIf,
    KeywordIn,
    KeywordInstanceof,
    KeywordNew,
    KeywordReturn,
    KeywordSwitch,
    KeywordThis,
    KeywordThrow,
    KeywordTry,
    KeywordTypeof,
    KeywordVar,
    KeywordVoid,
    KeywordWhile,
    KeywordWith,

    // ES5 future-reserved + ES2015+ keywords. The lexer recognizes
    // them so they can't be used as identifiers in strict mode, but
    // the parser enforces the strict-mode rule.
    KeywordClass,
    KeywordConst,
    KeywordEnum,
    KeywordExport,
    KeywordExtends,
    KeywordImport,
    KeywordSuper,
    KeywordLet,
    KeywordYield,
    KeywordStatic,
}

/// <summary>
/// One token emitted by <see cref="JsLexer"/>. Carries the kind, the
/// original source span (for error reporting and for the parser to
/// reconstruct line/column on demand), and pre-computed
/// <see cref="StringValue"/> / <see cref="NumberValue"/> fields for
/// literals so downstream consumers don't re-parse.
/// </summary>
public readonly struct JsToken
{
    public JsTokenKind Kind { get; }

    /// <summary>Offset into the source text where this token starts.</summary>
    public int Start { get; }

    /// <summary>Length in characters of the token's source text.</summary>
    public int Length { get; }

    /// <summary>
    /// For <see cref="JsTokenKind.StringLiteral"/>: the decoded string
    /// value (escape sequences resolved). For
    /// <see cref="JsTokenKind.Identifier"/>: the raw identifier text.
    /// Null for other token kinds.
    /// </summary>
    public string? StringValue { get; }

    /// <summary>
    /// For <see cref="JsTokenKind.NumberLiteral"/>: the parsed IEEE-754
    /// value. JavaScript numbers are all doubles, so integers and
    /// floats share this field. <see cref="double.NaN"/> for other
    /// token kinds.
    /// </summary>
    public double NumberValue { get; }

    public int End => Start + Length;

    public JsToken(
        JsTokenKind kind,
        int start,
        int length,
        string? stringValue = null,
        double numberValue = double.NaN)
    {
        Kind = kind;
        Start = start;
        Length = length;
        StringValue = stringValue;
        NumberValue = numberValue;
    }

    public override string ToString() => StringValue is not null
        ? $"{Kind}({StringValue})"
        : Kind.ToString();
}
