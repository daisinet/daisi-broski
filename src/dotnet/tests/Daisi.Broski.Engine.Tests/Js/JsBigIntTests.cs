using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-11: <c>BigInt</c> — the final phase 3c
/// primitive type. Covers literal syntax, the
/// <c>BigInt()</c> coercion function, <c>typeof</c>,
/// arithmetic, ordered comparison with Numbers,
/// equality, and JSON serialization rejection.
/// </summary>
public class JsBigIntTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Literal syntax
    // ========================================================

    [Fact]
    public void Decimal_bigint_literal()
    {
        Assert.Equal("42", Eval("String(42n);"));
    }

    [Fact]
    public void Large_decimal_bigint_literal()
    {
        Assert.Equal(
            "9007199254740993",
            Eval("String(9007199254740993n);"));
    }

    [Fact]
    public void Hex_bigint_literal()
    {
        Assert.Equal("255", Eval("String(0xFFn);"));
    }

    // ========================================================
    // typeof
    // ========================================================

    [Fact]
    public void TypeOf_bigint_literal()
    {
        Assert.Equal("bigint", Eval("typeof 42n;"));
    }

    [Fact]
    public void TypeOf_BigInt_coercion()
    {
        Assert.Equal("bigint", Eval("typeof BigInt('7');"));
    }

    // ========================================================
    // BigInt() coercion
    // ========================================================

    [Fact]
    public void BigInt_from_number()
    {
        Assert.Equal("42", Eval("String(BigInt(42));"));
    }

    [Fact]
    public void BigInt_from_string_decimal()
    {
        Assert.Equal(
            "12345678901234567890",
            Eval("String(BigInt('12345678901234567890'));"));
    }

    [Fact]
    public void BigInt_from_string_hex()
    {
        Assert.Equal("255", Eval("String(BigInt('0xff'));"));
    }

    [Fact]
    public void BigInt_from_boolean_true()
    {
        Assert.Equal("1", Eval("String(BigInt(true));"));
    }

    [Fact]
    public void BigInt_from_non_integer_number_throws()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { BigInt(3.14); false; }
                catch (e) { e instanceof RangeError; }
            "));
    }

    [Fact]
    public void BigInt_from_NaN_throws()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { BigInt(NaN); false; }
                catch (e) { e instanceof RangeError; }
            "));
    }

    [Fact]
    public void BigInt_from_invalid_string_throws()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { BigInt('not a number'); false; }
                catch (e) { e instanceof SyntaxError; }
            "));
    }

    // ========================================================
    // Arithmetic
    // ========================================================

    [Fact]
    public void BigInt_addition()
    {
        Assert.Equal("5", Eval("String(2n + 3n);"));
    }

    [Fact]
    public void BigInt_subtraction()
    {
        Assert.Equal("10", Eval("String(13n - 3n);"));
    }

    [Fact]
    public void BigInt_multiplication()
    {
        Assert.Equal("42", Eval("String(6n * 7n);"));
    }

    [Fact]
    public void BigInt_division_truncates()
    {
        Assert.Equal("3", Eval("String(10n / 3n);"));
    }

    [Fact]
    public void BigInt_modulo()
    {
        Assert.Equal("1", Eval("String(10n % 3n);"));
    }

    [Fact]
    public void BigInt_division_by_zero_throws()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { 10n / 0n; false; }
                catch (e) { e instanceof RangeError; }
            "));
    }

    [Fact]
    public void BigInt_very_large_arithmetic()
    {
        // 2^64 via repeated multiplication — we don't ship
        // the `**` exponent operator yet, so build the value
        // by squaring four times.
        Assert.Equal(
            "18446744073709551616",
            Eval(@"
                var x = 2n;
                x = x * x; // 4
                x = x * x; // 16
                x = x * x; // 256
                x = x * x; // 65536
                x = x * x; // 2^32
                x = x * x; // 2^64
                String(x);
            "));
    }

    [Fact]
    public void BigInt_unary_negate()
    {
        Assert.Equal("-5", Eval("String(-5n);"));
    }

    [Fact]
    public void BigInt_mixed_with_Number_throws()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { 1n + 2; false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    // ========================================================
    // Equality
    // ========================================================

    [Fact]
    public void Strict_equal_same_bigint()
    {
        Assert.Equal(true, Eval("5n === 5n;"));
    }

    [Fact]
    public void Strict_equal_different_bigints()
    {
        Assert.Equal(false, Eval("5n === 6n;"));
    }

    [Fact]
    public void Strict_equal_bigint_vs_number_is_false()
    {
        Assert.Equal(false, Eval("5n === 5;"));
    }

    [Fact]
    public void Loose_equal_bigint_vs_number_with_same_value()
    {
        Assert.Equal(true, Eval("5n == 5;"));
    }

    [Fact]
    public void Loose_equal_bigint_vs_nonmatching_number()
    {
        Assert.Equal(false, Eval("5n == 6;"));
    }

    [Fact]
    public void Loose_equal_bigint_vs_string()
    {
        Assert.Equal(true, Eval("42n == '42';"));
    }

    // ========================================================
    // Ordered comparison with Number
    // ========================================================

    [Fact]
    public void BigInt_less_than_number()
    {
        Assert.Equal(true, Eval("3n < 5;"));
    }

    [Fact]
    public void BigInt_greater_than_number()
    {
        Assert.Equal(true, Eval("10n > 5;"));
    }

    [Fact]
    public void BigInt_less_than_equal_number()
    {
        Assert.Equal(true, Eval("5n <= 5;"));
    }

    [Fact]
    public void BigInt_compared_with_NaN_is_false()
    {
        Assert.Equal(false, Eval("5n < NaN;"));
    }

    // ========================================================
    // String concatenation
    // ========================================================

    [Fact]
    public void String_concat_with_bigint()
    {
        Assert.Equal("value=42", Eval("'value=' + 42n;"));
    }

    // ========================================================
    // ToBoolean
    // ========================================================

    [Fact]
    public void BigInt_zero_is_falsy()
    {
        Assert.Equal(
            "zero",
            Eval(@"
                var result;
                if (0n) result = 'truthy';
                else result = 'zero';
                result;
            "));
    }

    [Fact]
    public void BigInt_nonzero_is_truthy()
    {
        Assert.Equal(
            "truthy",
            Eval(@"
                var result;
                if (42n) result = 'truthy';
                else result = 'zero';
                result;
            "));
    }

    // ========================================================
    // JSON serialization
    // ========================================================

    [Fact]
    public void JSON_stringify_rejects_bigint()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { JSON.stringify(42n); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    [Fact]
    public void JSON_stringify_rejects_nested_bigint()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { JSON.stringify({ count: 10n }); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }
}
