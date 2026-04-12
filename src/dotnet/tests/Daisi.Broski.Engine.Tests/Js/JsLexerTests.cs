using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsLexerTests
{
    // -------- basics / EOF --------

    [Fact]
    public void Empty_source_emits_only_EOF()
    {
        var tokens = Lex("");
        Assert.Single(tokens);
        Assert.Equal(JsTokenKind.EndOfFile, tokens[0].Kind);
    }

    [Fact]
    public void Whitespace_only_source_emits_only_EOF()
    {
        var tokens = Lex("  \t\r\n  ");
        Assert.Single(tokens);
        Assert.Equal(JsTokenKind.EndOfFile, tokens[0].Kind);
    }

    // -------- identifiers + keywords --------

    [Fact]
    public void Simple_identifier()
    {
        var tokens = Lex("foo");
        Assert.Equal(JsTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("foo", tokens[0].StringValue);
    }

    [Fact]
    public void Identifier_with_underscore_and_dollar_and_digit()
    {
        var tokens = Lex("_foo $bar baz_1 $_");
        Assert.Equal(JsTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("_foo", tokens[0].StringValue);
        Assert.Equal("$bar", tokens[1].StringValue);
        Assert.Equal("baz_1", tokens[2].StringValue);
        Assert.Equal("$_", tokens[3].StringValue);
    }

    [Fact]
    public void All_ES5_keywords_map_to_their_token_kinds()
    {
        var pairs = new (string src, JsTokenKind kind)[]
        {
            ("var", JsTokenKind.KeywordVar),
            ("function", JsTokenKind.KeywordFunction),
            ("return", JsTokenKind.KeywordReturn),
            ("if", JsTokenKind.KeywordIf),
            ("else", JsTokenKind.KeywordElse),
            ("for", JsTokenKind.KeywordFor),
            ("while", JsTokenKind.KeywordWhile),
            ("do", JsTokenKind.KeywordDo),
            ("break", JsTokenKind.KeywordBreak),
            ("continue", JsTokenKind.KeywordContinue),
            ("this", JsTokenKind.KeywordThis),
            ("new", JsTokenKind.KeywordNew),
            ("typeof", JsTokenKind.KeywordTypeof),
            ("instanceof", JsTokenKind.KeywordInstanceof),
            ("in", JsTokenKind.KeywordIn),
            ("delete", JsTokenKind.KeywordDelete),
            ("void", JsTokenKind.KeywordVoid),
            ("throw", JsTokenKind.KeywordThrow),
            ("try", JsTokenKind.KeywordTry),
            ("catch", JsTokenKind.KeywordCatch),
            ("finally", JsTokenKind.KeywordFinally),
            ("switch", JsTokenKind.KeywordSwitch),
            ("case", JsTokenKind.KeywordCase),
            ("default", JsTokenKind.KeywordDefault),
            ("debugger", JsTokenKind.KeywordDebugger),
            ("with", JsTokenKind.KeywordWith),
        };

        foreach (var (src, kind) in pairs)
        {
            var tokens = Lex(src);
            Assert.Equal(kind, tokens[0].Kind);
        }
    }

    [Fact]
    public void Es2015_keywords_map_to_their_token_kinds()
    {
        Assert.Equal(JsTokenKind.KeywordLet, Lex("let")[0].Kind);
        Assert.Equal(JsTokenKind.KeywordConst, Lex("const")[0].Kind);
        Assert.Equal(JsTokenKind.KeywordClass, Lex("class")[0].Kind);
        Assert.Equal(JsTokenKind.KeywordExtends, Lex("extends")[0].Kind);
        Assert.Equal(JsTokenKind.KeywordSuper, Lex("super")[0].Kind);
        Assert.Equal(JsTokenKind.KeywordImport, Lex("import")[0].Kind);
        Assert.Equal(JsTokenKind.KeywordExport, Lex("export")[0].Kind);
        Assert.Equal(JsTokenKind.KeywordYield, Lex("yield")[0].Kind);
        Assert.Equal(JsTokenKind.KeywordStatic, Lex("static")[0].Kind);
    }

    [Fact]
    public void Null_true_false_are_literal_tokens_not_identifiers()
    {
        Assert.Equal(JsTokenKind.NullLiteral, Lex("null")[0].Kind);
        Assert.Equal(JsTokenKind.TrueLiteral, Lex("true")[0].Kind);
        Assert.Equal(JsTokenKind.FalseLiteral, Lex("false")[0].Kind);
    }

    [Fact]
    public void Identifier_that_starts_with_keyword_prefix_is_still_identifier()
    {
        // "returnType" should not be read as the 'return' keyword.
        var tokens = Lex("returnType varName iff");
        Assert.Equal(JsTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("returnType", tokens[0].StringValue);
        Assert.Equal(JsTokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("varName", tokens[1].StringValue);
        Assert.Equal(JsTokenKind.Identifier, tokens[2].Kind);
        Assert.Equal("iff", tokens[2].StringValue);
    }

    // -------- numbers --------

    [Fact]
    public void Integer_literal()
    {
        var tokens = Lex("42");
        Assert.Equal(JsTokenKind.NumberLiteral, tokens[0].Kind);
        Assert.Equal(42.0, tokens[0].NumberValue);
    }

    [Fact]
    public void Decimal_with_fractional_part()
    {
        Assert.Equal(3.14, Lex("3.14")[0].NumberValue);
    }

    [Fact]
    public void Leading_dot_decimal_literal()
    {
        Assert.Equal(0.5, Lex(".5")[0].NumberValue);
    }

    [Fact]
    public void Trailing_dot_decimal_literal()
    {
        Assert.Equal(5.0, Lex("5.")[0].NumberValue);
    }

    [Fact]
    public void Scientific_notation()
    {
        Assert.Equal(1e10, Lex("1e10")[0].NumberValue);
        Assert.Equal(1.5e10, Lex("1.5e10")[0].NumberValue);
        Assert.Equal(1e-10, Lex("1e-10")[0].NumberValue);
        Assert.Equal(1e10, Lex("1E+10")[0].NumberValue);
    }

    [Fact]
    public void Hex_literals()
    {
        Assert.Equal(0x1F, Lex("0x1F")[0].NumberValue);
        Assert.Equal(0xFF, Lex("0xFF")[0].NumberValue);
        Assert.Equal(0xDEADBEEF, Lex("0xDEADBEEF")[0].NumberValue);
        Assert.Equal(0x0, Lex("0X0")[0].NumberValue);
    }

    [Fact]
    public void Lone_zero()
    {
        Assert.Equal(0.0, Lex("0")[0].NumberValue);
    }

    // -------- strings --------

    [Fact]
    public void Simple_double_quoted_string()
    {
        var t = Lex("\"hello\"")[0];
        Assert.Equal(JsTokenKind.StringLiteral, t.Kind);
        Assert.Equal("hello", t.StringValue);
    }

    [Fact]
    public void Simple_single_quoted_string()
    {
        var t = Lex("'world'")[0];
        Assert.Equal(JsTokenKind.StringLiteral, t.Kind);
        Assert.Equal("world", t.StringValue);
    }

    [Fact]
    public void Empty_string()
    {
        var t = Lex("\"\"")[0];
        Assert.Equal(JsTokenKind.StringLiteral, t.Kind);
        Assert.Equal("", t.StringValue);
    }

    [Fact]
    public void String_with_common_escapes()
    {
        var t = Lex("\"a\\nb\\tc\\rd\\\\e\\\"f\\'g\"")[0];
        Assert.Equal("a\nb\tc\rd\\e\"f'g", t.StringValue);
    }

    [Fact]
    public void String_with_hex_escape()
    {
        var t = Lex("\"\\x41\\x42\"")[0];
        Assert.Equal("AB", t.StringValue);
    }

    [Fact]
    public void String_with_unicode_escape()
    {
        // \u00E9 = é
        var t = Lex("\"caf\\u00E9\"")[0];
        Assert.Equal("café", t.StringValue);
    }

    [Fact]
    public void String_with_null_escape()
    {
        var t = Lex("\"\\0\"")[0];
        Assert.Equal("\0", t.StringValue);
    }

    [Fact]
    public void String_line_continuation_produces_no_output()
    {
        // "a\<LF>b" → "ab" (line continuation)
        var t = Lex("\"a\\\nb\"")[0];
        Assert.Equal("ab", t.StringValue);
    }

    [Fact]
    public void Unterminated_string_produces_unknown_token()
    {
        Assert.Equal(JsTokenKind.Unknown, Lex("\"abc")[0].Kind);
    }

    [Fact]
    public void String_with_newline_inside_produces_unknown_token()
    {
        Assert.Equal(JsTokenKind.Unknown, Lex("\"abc\ndef\"")[0].Kind);
    }

    // -------- punctuators --------

    [Fact]
    public void Single_char_punctuators()
    {
        // The `/` punctuator is context-sensitive — a
        // slash after an operator is a regex literal, not
        // division. This test keeps the input as a
        // realistic expression (identifier-operator-
        // identifier pattern) so every punctuator appears
        // in a position that resolves unambiguously.
        var tokens = Lex("a ( ) { } [ ] ; , : ? ~ + - * a / a % & | ^ ! = < >");
        var kinds = tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(JsTokenKind.Identifier, kinds[0]);
        Assert.Equal(JsTokenKind.LeftParen, kinds[1]);
        Assert.Equal(JsTokenKind.RightParen, kinds[2]);
        Assert.Equal(JsTokenKind.LeftBrace, kinds[3]);
        Assert.Equal(JsTokenKind.RightBrace, kinds[4]);
        Assert.Equal(JsTokenKind.LeftBracket, kinds[5]);
        Assert.Equal(JsTokenKind.RightBracket, kinds[6]);
        Assert.Equal(JsTokenKind.Semicolon, kinds[7]);
        Assert.Equal(JsTokenKind.Comma, kinds[8]);
        Assert.Equal(JsTokenKind.Colon, kinds[9]);
        Assert.Equal(JsTokenKind.QuestionMark, kinds[10]);
        Assert.Equal(JsTokenKind.Tilde, kinds[11]);
        Assert.Equal(JsTokenKind.Plus, kinds[12]);
        Assert.Equal(JsTokenKind.Minus, kinds[13]);
        Assert.Equal(JsTokenKind.Star, kinds[14]);
        // kinds[15] is the second `a`
        Assert.Equal(JsTokenKind.Slash, kinds[16]);
        // kinds[17] is the third `a`
        Assert.Equal(JsTokenKind.Percent, kinds[18]);
        Assert.Equal(JsTokenKind.Ampersand, kinds[19]);
        Assert.Equal(JsTokenKind.Pipe, kinds[20]);
        Assert.Equal(JsTokenKind.Caret, kinds[21]);
        Assert.Equal(JsTokenKind.Exclamation, kinds[22]);
        Assert.Equal(JsTokenKind.Assign, kinds[23]);
        Assert.Equal(JsTokenKind.LessThan, kinds[24]);
        Assert.Equal(JsTokenKind.GreaterThan, kinds[25]);
    }

    [Fact]
    public void Dot_punctuators()
    {
        Assert.Equal(JsTokenKind.Dot, Lex(".")[0].Kind);
        var spread = Lex("...");
        Assert.Equal(JsTokenKind.DotDotDot, spread[0].Kind);
        Assert.Equal(3, spread[0].Length);
    }

    [Fact]
    public void Equality_operators()
    {
        Assert.Equal(JsTokenKind.Assign, Lex("=")[0].Kind);
        Assert.Equal(JsTokenKind.Equal, Lex("==")[0].Kind);
        Assert.Equal(JsTokenKind.StrictEqual, Lex("===")[0].Kind);
        Assert.Equal(JsTokenKind.NotEqual, Lex("!=")[0].Kind);
        Assert.Equal(JsTokenKind.StrictNotEqual, Lex("!==")[0].Kind);
    }

    [Fact]
    public void Relational_operators()
    {
        Assert.Equal(JsTokenKind.LessThanEqual, Lex("<=")[0].Kind);
        Assert.Equal(JsTokenKind.GreaterThanEqual, Lex(">=")[0].Kind);
    }

    [Fact]
    public void Shift_operators()
    {
        Assert.Equal(JsTokenKind.LeftShift, Lex("<<")[0].Kind);
        Assert.Equal(JsTokenKind.RightShift, Lex(">>")[0].Kind);
        Assert.Equal(JsTokenKind.UnsignedRightShift, Lex(">>>")[0].Kind);
    }

    [Fact]
    public void Shift_assign_operators()
    {
        Assert.Equal(JsTokenKind.LeftShiftAssign, Lex("<<=")[0].Kind);
        Assert.Equal(JsTokenKind.RightShiftAssign, Lex(">>=")[0].Kind);
        Assert.Equal(JsTokenKind.UnsignedRightShiftAssign, Lex(">>>=")[0].Kind);
    }

    [Fact]
    public void Logical_and_increment_operators()
    {
        Assert.Equal(JsTokenKind.AmpersandAmpersand, Lex("&&")[0].Kind);
        Assert.Equal(JsTokenKind.PipePipe, Lex("||")[0].Kind);
        Assert.Equal(JsTokenKind.PlusPlus, Lex("++")[0].Kind);
        Assert.Equal(JsTokenKind.MinusMinus, Lex("--")[0].Kind);
    }

    [Fact]
    public void Compound_assignment_operators()
    {
        // Prefix each expression with `a` so `/=` lexes as
        // division-assign rather than the start of a regex
        // literal at position 0 of the input.
        Assert.Equal(JsTokenKind.PlusAssign, Lex("a+=")[1].Kind);
        Assert.Equal(JsTokenKind.MinusAssign, Lex("a-=")[1].Kind);
        Assert.Equal(JsTokenKind.StarAssign, Lex("a*=")[1].Kind);
        Assert.Equal(JsTokenKind.SlashAssign, Lex("a/=")[1].Kind);
        Assert.Equal(JsTokenKind.PercentAssign, Lex("a%=")[1].Kind);
        Assert.Equal(JsTokenKind.AmpersandAssign, Lex("a&=")[1].Kind);
        Assert.Equal(JsTokenKind.PipeAssign, Lex("a|=")[1].Kind);
        Assert.Equal(JsTokenKind.CaretAssign, Lex("a^=")[1].Kind);
    }

    [Fact]
    public void Arrow_operator()
    {
        Assert.Equal(JsTokenKind.Arrow, Lex("=>")[0].Kind);
    }

    [Fact]
    public void Punctuator_disambiguation_greedy_longest_match()
    {
        // ">>>=" should lex as one UnsignedRightShiftAssign, not
        // ">>>" + "=" or ">>" + ">" + "=".
        var t = Lex(">>>=");
        Assert.Equal(JsTokenKind.UnsignedRightShiftAssign, t[0].Kind);
        Assert.Equal(4, t[0].Length);
    }

    // -------- comments --------

    [Fact]
    public void Line_comment_is_skipped()
    {
        var tokens = Lex("a // this is a comment\nb");
        Assert.Equal(JsTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("a", tokens[0].StringValue);
        Assert.Equal(JsTokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("b", tokens[1].StringValue);
        Assert.Equal(JsTokenKind.EndOfFile, tokens[2].Kind);
    }

    [Fact]
    public void Block_comment_is_skipped()
    {
        var tokens = Lex("a /* this\nspans lines */ b");
        Assert.Equal("a", tokens[0].StringValue);
        Assert.Equal("b", tokens[1].StringValue);
    }

    [Fact]
    public void Unterminated_block_comment_stops_at_eof_cleanly()
    {
        // Unterminated block comment should not hang or throw; it
        // eats everything to EOF.
        var tokens = Lex("a /* unterminated");
        Assert.Equal("a", tokens[0].StringValue);
        Assert.Equal(JsTokenKind.EndOfFile, tokens[1].Kind);
    }

    // -------- real-world snippets --------

    [Fact]
    public void Variable_declaration_snippet()
    {
        var tokens = Lex("var x = 42;");
        Assert.Equal(JsTokenKind.KeywordVar, tokens[0].Kind);
        Assert.Equal(JsTokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("x", tokens[1].StringValue);
        Assert.Equal(JsTokenKind.Assign, tokens[2].Kind);
        Assert.Equal(JsTokenKind.NumberLiteral, tokens[3].Kind);
        Assert.Equal(42.0, tokens[3].NumberValue);
        Assert.Equal(JsTokenKind.Semicolon, tokens[4].Kind);
        Assert.Equal(JsTokenKind.EndOfFile, tokens[5].Kind);
    }

    [Fact]
    public void Function_declaration_snippet()
    {
        var tokens = Lex("function add(a, b) { return a + b; }");
        var kinds = tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(JsTokenKind.KeywordFunction, kinds[0]);
        Assert.Equal(JsTokenKind.Identifier, kinds[1]); // add
        Assert.Equal(JsTokenKind.LeftParen, kinds[2]);
        Assert.Equal(JsTokenKind.Identifier, kinds[3]); // a
        Assert.Equal(JsTokenKind.Comma, kinds[4]);
        Assert.Equal(JsTokenKind.Identifier, kinds[5]); // b
        Assert.Equal(JsTokenKind.RightParen, kinds[6]);
        Assert.Equal(JsTokenKind.LeftBrace, kinds[7]);
        Assert.Equal(JsTokenKind.KeywordReturn, kinds[8]);
        Assert.Equal(JsTokenKind.Identifier, kinds[9]); // a
        Assert.Equal(JsTokenKind.Plus, kinds[10]);
        Assert.Equal(JsTokenKind.Identifier, kinds[11]); // b
        Assert.Equal(JsTokenKind.Semicolon, kinds[12]);
        Assert.Equal(JsTokenKind.RightBrace, kinds[13]);
        Assert.Equal(JsTokenKind.EndOfFile, kinds[14]);
    }

    [Fact]
    public void Object_literal_snippet()
    {
        var tokens = Lex("var obj = { name: 'foo', count: 3 };");
        // Spot-check: verify the string and number literals came through.
        var nameValue = tokens.First(t => t.Kind == JsTokenKind.StringLiteral);
        Assert.Equal("foo", nameValue.StringValue);
        var countValue = tokens.First(t => t.Kind == JsTokenKind.NumberLiteral);
        Assert.Equal(3.0, countValue.NumberValue);
    }

    [Fact]
    public void Chained_method_call_snippet()
    {
        var tokens = Lex("foo.bar(baz).qux = 1;");
        var kinds = tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(JsTokenKind.Identifier, kinds[0]);
        Assert.Equal(JsTokenKind.Dot, kinds[1]);
        Assert.Equal(JsTokenKind.Identifier, kinds[2]);
        Assert.Equal(JsTokenKind.LeftParen, kinds[3]);
        Assert.Equal(JsTokenKind.Identifier, kinds[4]);
        Assert.Equal(JsTokenKind.RightParen, kinds[5]);
        Assert.Equal(JsTokenKind.Dot, kinds[6]);
        Assert.Equal(JsTokenKind.Identifier, kinds[7]);
        Assert.Equal(JsTokenKind.Assign, kinds[8]);
        Assert.Equal(JsTokenKind.NumberLiteral, kinds[9]);
        Assert.Equal(JsTokenKind.Semicolon, kinds[10]);
    }

    [Fact]
    public void Position_tracking_start_and_length()
    {
        var tokens = Lex("ab cd");
        Assert.Equal(0, tokens[0].Start);
        Assert.Equal(2, tokens[0].Length);
        Assert.Equal(3, tokens[1].Start);
        Assert.Equal(2, tokens[1].Length);
    }

    // -------- helper --------

    private static List<JsToken> Lex(string src)
    {
        var lexer = new JsLexer(src);
        return lexer.Tokens().ToList();
    }
}
