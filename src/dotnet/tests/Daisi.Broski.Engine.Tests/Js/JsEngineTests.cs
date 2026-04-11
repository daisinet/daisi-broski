using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsEngineTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    private static double Num(object? v) => Assert.IsType<double>(v);
    private static string Str(object? v) => Assert.IsType<string>(v);
    private static bool Bool(object? v) => Assert.IsType<bool>(v);

    // -------- literals --------

    [Fact]
    public void Number_literal_evaluates_to_itself()
    {
        Assert.Equal(42.0, Num(Eval("42;")));
    }

    [Fact]
    public void String_literal_decodes_escapes()
    {
        Assert.Equal("hello\nworld", Str(Eval("'hello\\nworld';")));
    }

    [Fact]
    public void Boolean_and_null_and_undefined_literals()
    {
        Assert.Equal(true, Eval("true;"));
        Assert.Equal(false, Eval("false;"));
        Assert.IsType<JsNull>(Eval("null;"));
    }

    // -------- arithmetic --------

    [Fact]
    public void Integer_arithmetic_follows_precedence()
    {
        Assert.Equal(14.0, Num(Eval("2 + 3 * 4;")));
        Assert.Equal(20.0, Num(Eval("(2 + 3) * 4;")));
    }

    [Fact]
    public void Mixed_integer_and_float()
    {
        Assert.Equal(2.5, Num(Eval("5 / 2;")));
        Assert.Equal(1.0, Num(Eval("5 % 2;")));
    }

    [Fact]
    public void Division_by_zero_yields_infinity()
    {
        Assert.Equal(double.PositiveInfinity, Num(Eval("1 / 0;")));
        Assert.Equal(double.NegativeInfinity, Num(Eval("-1 / 0;")));
    }

    [Fact]
    public void Zero_divided_by_zero_is_NaN()
    {
        Assert.True(double.IsNaN(Num(Eval("0 / 0;"))));
    }

    [Fact]
    public void Negation_and_unary_plus()
    {
        Assert.Equal(-5.0, Num(Eval("-5;")));
        Assert.Equal(5.0, Num(Eval("+'5';")));
        Assert.True(double.IsNaN(Num(Eval("+'abc';"))));
    }

    // -------- string concatenation --------

    [Fact]
    public void String_plus_string_concatenates()
    {
        Assert.Equal("foobar", Str(Eval("'foo' + 'bar';")));
    }

    [Fact]
    public void String_plus_number_concatenates_via_string_coercion()
    {
        Assert.Equal("x1", Str(Eval("'x' + 1;")));
        Assert.Equal("1x", Str(Eval("1 + 'x';")));
    }

    [Fact]
    public void Number_plus_number_is_addition_not_concat()
    {
        Assert.Equal(3.0, Num(Eval("1 + 2;")));
    }

    // -------- coercion quirks --------

    [Fact]
    public void Loose_equality_coerces_types()
    {
        Assert.Equal(true, Eval("1 == '1';"));
        Assert.Equal(true, Eval("0 == false;"));
        Assert.Equal(true, Eval("null == undefined;"));
        Assert.Equal(false, Eval("null == 0;"));
    }

    [Fact]
    public void Strict_equality_does_not_coerce()
    {
        Assert.Equal(false, Eval("1 === '1';"));
        Assert.Equal(true, Eval("1 === 1;"));
        Assert.Equal(false, Eval("null === undefined;"));
    }

    [Fact]
    public void NaN_is_never_equal_to_itself()
    {
        Assert.Equal(false, Eval("NaN === NaN;") ?? true);
    }

    [Fact]
    public void ToBoolean_truthiness()
    {
        Assert.Equal(false, Eval("!1;"));
        Assert.Equal(true, Eval("!0;"));
        Assert.Equal(true, Eval("!'';"));
        Assert.Equal(false, Eval("!'x';"));
        Assert.Equal(true, Eval("!null;"));
        Assert.Equal(true, Eval("!undefined;"));
    }

    // -------- bitwise --------

    [Fact]
    public void Bitwise_operators_return_int32()
    {
        Assert.Equal(5.0, Num(Eval("1 | 4;")));
        Assert.Equal(0.0, Num(Eval("1 & 4;")));
        Assert.Equal(5.0, Num(Eval("1 ^ 4;")));
        Assert.Equal(-2.0, Num(Eval("~1;")));
    }

    [Fact]
    public void Shifts_respect_int32_semantics()
    {
        Assert.Equal(8.0, Num(Eval("1 << 3;")));
        Assert.Equal(-1.0, Num(Eval("-2 >> 1;")));
        // Unsigned right shift on a negative number produces a
        // large positive result.
        Assert.Equal(2147483647.0, Num(Eval("-2 >>> 1;")));
    }

    // -------- variables --------

    [Fact]
    public void Var_declaration_and_read()
    {
        var eng = new JsEngine();
        eng.Evaluate("var x = 10;");
        Assert.Equal(10.0, eng.Globals["x"]);
    }

    [Fact]
    public void Var_without_initializer_is_undefined()
    {
        var eng = new JsEngine();
        eng.Evaluate("var x;");
        Assert.IsType<JsUndefined>(eng.Globals["x"]);
    }

    [Fact]
    public void Multiple_declarators_in_one_statement()
    {
        var eng = new JsEngine();
        eng.Evaluate("var a = 1, b = 2, c = a + b;");
        Assert.Equal(1.0, eng.Globals["a"]);
        Assert.Equal(2.0, eng.Globals["b"]);
        Assert.Equal(3.0, eng.Globals["c"]);
    }

    [Fact]
    public void Assignment_as_an_expression_yields_the_assigned_value()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate("var x; x = 7;");
        Assert.Equal(7.0, Num(result));
        Assert.Equal(7.0, eng.Globals["x"]);
    }

    [Fact]
    public void Compound_assignment_operators()
    {
        var eng = new JsEngine();
        eng.Evaluate("var x = 5; x += 3;");
        Assert.Equal(8.0, eng.Globals["x"]);

        eng.Evaluate("x *= 2;");
        Assert.Equal(16.0, eng.Globals["x"]);

        eng.Evaluate("x -= 1;");
        Assert.Equal(15.0, eng.Globals["x"]);
    }

    [Fact]
    public void Reading_undeclared_throws_runtime_error()
    {
        Assert.Throws<JsRuntimeException>(() => Eval("nonexistent;"));
    }

    // -------- var hoisting --------

    [Fact]
    public void Vars_are_hoisted_to_undefined_at_program_start()
    {
        // `x` is declared below its use; hoisting should bind it to
        // undefined before the first line runs.
        Assert.Equal("undefined", Str(Eval("var t = typeof x; var x = 1; t;")));
    }

    // -------- update expressions --------

    [Fact]
    public void Prefix_increment_returns_new_value()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate("var x = 5; ++x;");
        Assert.Equal(6.0, Num(result));
        Assert.Equal(6.0, eng.Globals["x"]);
    }

    [Fact]
    public void Postfix_increment_returns_old_value()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate("var x = 5; x++;");
        Assert.Equal(5.0, Num(result));
        Assert.Equal(6.0, eng.Globals["x"]);
    }

    [Fact]
    public void Prefix_decrement_returns_new_value()
    {
        var eng = new JsEngine();
        var result = eng.Evaluate("var x = 5; --x;");
        Assert.Equal(4.0, Num(result));
    }

    // -------- typeof --------

    [Fact]
    public void TypeOf_returns_spec_strings()
    {
        Assert.Equal("number", Str(Eval("typeof 1;")));
        Assert.Equal("string", Str(Eval("typeof 'x';")));
        Assert.Equal("boolean", Str(Eval("typeof true;")));
        Assert.Equal("undefined", Str(Eval("typeof undefined;")));
        // The historical quirk.
        Assert.Equal("object", Str(Eval("typeof null;")));
    }

    [Fact]
    public void TypeOf_on_undeclared_identifier_is_undefined_without_throwing()
    {
        Assert.Equal("undefined", Str(Eval("typeof undeclared_var;")));
    }

    // -------- logical short-circuit --------

    [Fact]
    public void Logical_and_short_circuits_and_returns_operand()
    {
        Assert.Equal(0.0, Num(Eval("0 && 'never';")));
        Assert.Equal("yes", Str(Eval("1 && 'yes';")));
    }

    [Fact]
    public void Logical_or_short_circuits_and_returns_operand()
    {
        Assert.Equal("first", Str(Eval("'first' || 'never';")));
        Assert.Equal("fallback", Str(Eval("0 || 'fallback';")));
    }

    [Fact]
    public void Logical_chains_are_left_to_right()
    {
        // `a || b && c` -> `a || (b && c)`.
        var eng = new JsEngine();
        eng.Evaluate("var a = 0, b = 1, c = 2; var r = a || b && c;");
        Assert.Equal(2.0, eng.Globals["r"]);
    }

    // -------- ternary --------

    [Fact]
    public void Conditional_expression_picks_branch()
    {
        Assert.Equal(1.0, Num(Eval("true ? 1 : 2;")));
        Assert.Equal(2.0, Num(Eval("false ? 1 : 2;")));
    }

    // -------- control flow --------

    [Fact]
    public void If_statement_runs_consequent()
    {
        var eng = new JsEngine();
        eng.Evaluate("var x; if (true) { x = 1; } else { x = 2; }");
        Assert.Equal(1.0, eng.Globals["x"]);
    }

    [Fact]
    public void If_statement_runs_alternate_when_false()
    {
        var eng = new JsEngine();
        eng.Evaluate("var x; if (false) { x = 1; } else { x = 2; }");
        Assert.Equal(2.0, eng.Globals["x"]);
    }

    [Fact]
    public void While_loop_runs_until_false()
    {
        var eng = new JsEngine();
        eng.Evaluate("var i = 0; while (i < 5) { i = i + 1; }");
        Assert.Equal(5.0, eng.Globals["i"]);
    }

    [Fact]
    public void Do_while_runs_body_at_least_once()
    {
        var eng = new JsEngine();
        eng.Evaluate("var i = 10; do { i = i + 1; } while (i < 5);");
        Assert.Equal(11.0, eng.Globals["i"]);
    }

    [Fact]
    public void For_loop_counts_to_ten()
    {
        var eng = new JsEngine();
        eng.Evaluate("var sum = 0; for (var i = 0; i < 10; i++) { sum = sum + i; }");
        Assert.Equal(45.0, eng.Globals["sum"]);
    }

    [Fact]
    public void For_loop_with_empty_init_and_update()
    {
        var eng = new JsEngine();
        eng.Evaluate("var i = 0; for (;;) { i++; if (i === 3) { break; } }");
        Assert.Equal(3.0, eng.Globals["i"]);
    }

    [Fact]
    public void Break_exits_while_loop()
    {
        var eng = new JsEngine();
        eng.Evaluate("var i = 0; while (true) { if (i === 3) { break; } i = i + 1; }");
        Assert.Equal(3.0, eng.Globals["i"]);
    }

    [Fact]
    public void Continue_skips_rest_of_while_body()
    {
        var eng = new JsEngine();
        eng.Evaluate(
            "var sum = 0; var i = 0; " +
            "while (i < 10) { i = i + 1; if (i % 2 === 0) { continue; } sum = sum + i; }");
        // Odd numbers 1+3+5+7+9 = 25.
        Assert.Equal(25.0, eng.Globals["sum"]);
    }

    [Fact]
    public void Continue_in_for_loop_hits_update_clause()
    {
        var eng = new JsEngine();
        eng.Evaluate(
            "var sum = 0; " +
            "for (var i = 0; i < 10; i++) { if (i === 5) { continue; } sum = sum + 1; }");
        // 10 iterations, 1 skipped → sum = 9.
        Assert.Equal(9.0, eng.Globals["sum"]);
    }

    [Fact]
    public void Nested_loops_with_break_only_exit_inner()
    {
        var eng = new JsEngine();
        eng.Evaluate(
            "var outer = 0; " +
            "for (var i = 0; i < 3; i++) { " +
            "  for (var j = 0; j < 3; j++) { " +
            "    if (j === 2) { break; } " +
            "  } " +
            "  outer = outer + 1; " +
            "}");
        Assert.Equal(3.0, eng.Globals["outer"]);
    }

    // -------- completion tracking --------

    [Fact]
    public void Completion_is_value_of_last_expression_statement()
    {
        Assert.Equal(3.0, Num(Eval("1; 2; 3;")));
    }

    [Fact]
    public void Completion_is_undefined_for_declaration_only_programs()
    {
        Assert.IsType<JsUndefined>(Eval("var x = 1;"));
    }

    [Fact]
    public void Completion_survives_through_non_expression_statements()
    {
        // `42;` sets completion, then `var x;` does not overwrite it.
        Assert.Equal(42.0, Num(Eval("42; var x;")));
    }

    // -------- reuse across Evaluate calls --------

    [Fact]
    public void Engine_retains_globals_across_evaluate_calls()
    {
        var eng = new JsEngine();
        eng.Evaluate("var x = 10;");
        eng.Evaluate("x = x + 5;");
        Assert.Equal(15.0, eng.Globals["x"]);
    }

    // -------- realistic mini-program --------

    [Fact]
    public void Fibonacci_iterative_computes_correctly()
    {
        // Classic iterative fib — no function calls, just a loop.
        var eng = new JsEngine();
        eng.Evaluate(@"
            var a = 0, b = 1, n = 10;
            for (var i = 0; i < n; i = i + 1) {
                var t = a + b;
                a = b;
                b = t;
            }
            a;
        ");
        Assert.Equal(55.0, eng.Globals["a"]);
    }

    [Fact]
    public void Sum_of_squares_by_loop()
    {
        Assert.Equal(
            385.0, // 1^2 + 2^2 + ... + 10^2
            Num(Eval(@"
                var sum = 0;
                for (var i = 1; i <= 10; i++) { sum = sum + i * i; }
                sum;
            ")));
    }

    // -------- unsupported forms should throw at compile time --------

    [Fact]
    public void Function_declaration_throws_compile_error()
    {
        Assert.Throws<JsCompileException>(() => Eval("function f() { return 1; }"));
    }

    [Fact]
    public void Try_statement_throws_compile_error()
    {
        Assert.Throws<JsCompileException>(() => Eval("try { } catch (e) { }"));
    }
}
