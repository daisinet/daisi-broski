using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-1: optional chaining <c>?.</c>, nullish coalescing
/// <c>??</c>, and logical assignment operators <c>&amp;&amp;=</c>,
/// <c>||=</c>, <c>??=</c>.
///
/// These are the three ES2020–2021 operators whose sole job is
/// to short-circuit on a specific left-operand condition, so
/// the tests focus on:
/// <list type="bullet">
///   <item>The short-circuit branch returns the expected
///     sentinel (undefined for <c>?.</c>, the left value for
///     <c>??</c>, or leaves the target unchanged for a
///     logical assign).</item>
///   <item>The right-hand side is not evaluated when the
///     short-circuit triggers — tested by asserting an
///     observable side-effect counter.</item>
///   <item>Nested chains and mixed ?./. hops still short-
///     circuit at the first nullish hop.</item>
/// </list>
/// </summary>
public class JsOptionalChainingTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Nullish coalescing ??
    // ========================================================

    [Fact]
    public void Nullish_returns_left_when_left_is_non_nullish()
    {
        Assert.Equal(1.0, Eval("1 ?? 2;"));
    }

    [Fact]
    public void Nullish_returns_right_when_left_is_null()
    {
        Assert.Equal(2.0, Eval("null ?? 2;"));
    }

    [Fact]
    public void Nullish_returns_right_when_left_is_undefined()
    {
        Assert.Equal(2.0, Eval("undefined ?? 2;"));
    }

    [Fact]
    public void Nullish_returns_left_for_falsy_zero()
    {
        // Unlike ||, ?? does NOT fall through on 0.
        Assert.Equal(0.0, Eval("0 ?? 2;"));
    }

    [Fact]
    public void Nullish_returns_left_for_empty_string()
    {
        Assert.Equal("", Eval("'' ?? 'fallback';"));
    }

    [Fact]
    public void Nullish_returns_left_for_false()
    {
        Assert.Equal(false, Eval("false ?? true;"));
    }

    [Fact]
    public void Nullish_short_circuits_right_side()
    {
        // When left is non-nullish, the right side — and its
        // side effects — must not run. hits stays at 0.
        Assert.Equal(
            0.0,
            Eval("var hits = 0; var r = 1 ?? (hits = 99, 2); hits;"));
    }

    [Fact]
    public void Nullish_evaluates_right_on_null()
    {
        Assert.Equal(
            99.0,
            Eval("var hits = 0; null ?? (hits = 99); hits;"));
    }

    [Fact]
    public void Nullish_is_right_associative()
    {
        // a ?? b ?? c — first non-nullish wins.
        Assert.Equal(3.0, Eval("null ?? undefined ?? 3;"));
    }

    // ========================================================
    // Optional chaining ?.
    // ========================================================

    [Fact]
    public void Optional_dot_returns_value_when_object_present()
    {
        Assert.Equal(
            1.0,
            Eval("var o = { a: 1 }; o?.a;"));
    }

    [Fact]
    public void Optional_dot_returns_undefined_when_object_null()
    {
        Assert.Equal(
            JsValue.Undefined,
            Eval("var o = null; o?.a;"));
    }

    [Fact]
    public void Optional_dot_returns_undefined_when_object_undefined()
    {
        Assert.Equal(
            JsValue.Undefined,
            Eval("var o = undefined; o?.a;"));
    }

    [Fact]
    public void Optional_bracket_returns_value_when_object_present()
    {
        Assert.Equal(
            "hi",
            Eval("var o = { x: 'hi' }; o?.['x'];"));
    }

    [Fact]
    public void Optional_bracket_returns_undefined_on_null()
    {
        Assert.Equal(
            JsValue.Undefined,
            Eval("var o = null; o?.['x'];"));
    }

    [Fact]
    public void Optional_call_invokes_function_when_present()
    {
        Assert.Equal(
            7.0,
            Eval("var f = function() { return 7; }; f?.();"));
    }

    [Fact]
    public void Optional_call_returns_undefined_when_callee_null()
    {
        Assert.Equal(
            JsValue.Undefined,
            Eval("var f = null; f?.();"));
    }

    [Fact]
    public void Optional_call_returns_undefined_when_callee_undefined()
    {
        Assert.Equal(
            JsValue.Undefined,
            Eval("var f; f?.();"));
    }

    [Fact]
    public void Optional_method_call_short_circuits_on_null_receiver()
    {
        Assert.Equal(
            JsValue.Undefined,
            Eval("var o = null; o?.method();"));
    }

    [Fact]
    public void Optional_method_call_runs_when_receiver_present()
    {
        Assert.Equal(
            "hi",
            Eval("var o = { say: function() { return 'hi'; } }; o?.say();"));
    }

    [Fact]
    public void Optional_method_call_preserves_this_binding()
    {
        // `this` inside the method should bind to the object,
        // not `undefined`, even when reached via ?.
        Assert.Equal(
            42.0,
            Eval("var o = { v: 42, get: function() { return this.v; } }; o?.get();"));
    }

    [Fact]
    public void Optional_chain_short_circuits_mid_chain()
    {
        // a?.b.c.d — if a is null, the whole chain shorts.
        Assert.Equal(
            JsValue.Undefined,
            Eval("var a = null; a?.b.c.d;"));
    }

    [Fact]
    public void Optional_chain_traverses_present_object()
    {
        Assert.Equal(
            5.0,
            Eval("var a = { b: { c: { d: 5 } } }; a?.b.c.d;"));
    }

    [Fact]
    public void Optional_chain_nested_optional_hops()
    {
        // a?.b?.c?.d — each hop short-circuits if needed.
        Assert.Equal(
            JsValue.Undefined,
            Eval("var a = { b: null }; a?.b?.c?.d;"));
    }

    [Fact]
    public void Optional_chain_mixed_hops_all_present()
    {
        Assert.Equal(
            9.0,
            Eval("var a = { b: { c: { d: 9 } } }; a?.b?.c?.d;"));
    }

    [Fact]
    public void Optional_call_then_member()
    {
        // f?.().x — if f is nullish, whole thing is undefined.
        Assert.Equal(
            JsValue.Undefined,
            Eval("var f = null; f?.().x;"));
    }

    [Fact]
    public void Optional_call_then_member_present()
    {
        Assert.Equal(
            8.0,
            Eval("var f = function() { return { x: 8 }; }; f?.().x;"));
    }

    [Fact]
    public void Optional_chain_short_circuits_arguments()
    {
        // When the chain shorts, the argument list is not
        // evaluated.
        Assert.Equal(
            0.0,
            Eval("var hits = 0; var f = null; f?.(hits = 99); hits;"));
    }

    [Fact]
    public void Optional_member_call_does_not_throw_on_missing_method()
    {
        // o.method?.() — method doesn't exist, chain shorts.
        Assert.Equal(
            JsValue.Undefined,
            Eval("var o = {}; o.method?.();"));
    }

    [Fact]
    public void Optional_chain_with_nullish_coalescing_default()
    {
        // Canonical ES2020 pattern: fall back to a default when
        // a chain short-circuits.
        Assert.Equal(
            "default",
            Eval("var o = null; var r = o?.name ?? 'default'; r;"));
    }

    // ========================================================
    // Logical assignment &&= ||= ??=
    // ========================================================

    [Fact]
    public void LogicalAndAssign_assigns_when_target_truthy()
    {
        Assert.Equal(
            2.0,
            Eval("var x = 1; x &&= 2; x;"));
    }

    [Fact]
    public void LogicalAndAssign_keeps_target_when_falsy()
    {
        Assert.Equal(
            0.0,
            Eval("var x = 0; x &&= 2; x;"));
    }

    [Fact]
    public void LogicalAndAssign_short_circuits_rhs()
    {
        Assert.Equal(
            0.0,
            Eval("var hits = 0; var x = 0; x &&= (hits = 99); hits;"));
    }

    [Fact]
    public void LogicalOrAssign_assigns_when_target_falsy()
    {
        Assert.Equal(
            5.0,
            Eval("var x = 0; x ||= 5; x;"));
    }

    [Fact]
    public void LogicalOrAssign_keeps_target_when_truthy()
    {
        Assert.Equal(
            1.0,
            Eval("var x = 1; x ||= 5; x;"));
    }

    [Fact]
    public void LogicalOrAssign_short_circuits_rhs()
    {
        Assert.Equal(
            0.0,
            Eval("var hits = 0; var x = 1; x ||= (hits = 99); hits;"));
    }

    [Fact]
    public void NullishAssign_assigns_when_target_null()
    {
        Assert.Equal(
            5.0,
            Eval("var x = null; x ??= 5; x;"));
    }

    [Fact]
    public void NullishAssign_assigns_when_target_undefined()
    {
        Assert.Equal(
            5.0,
            Eval("var x; x ??= 5; x;"));
    }

    [Fact]
    public void NullishAssign_keeps_target_when_non_nullish()
    {
        // Zero is non-nullish; ??= leaves it alone.
        Assert.Equal(
            0.0,
            Eval("var x = 0; x ??= 5; x;"));
    }

    [Fact]
    public void NullishAssign_short_circuits_rhs()
    {
        Assert.Equal(
            0.0,
            Eval("var hits = 0; var x = 0; x ??= (hits = 99); hits;"));
    }

    [Fact]
    public void LogicalAssign_on_object_property()
    {
        Assert.Equal(
            "set",
            Eval("var o = { a: null }; o.a ??= 'set'; o.a;"));
    }

    [Fact]
    public void LogicalAssign_on_object_property_keeps_existing()
    {
        Assert.Equal(
            "orig",
            Eval("var o = { a: 'orig' }; o.a ??= 'new'; o.a;"));
    }

    [Fact]
    public void LogicalAndAssign_on_object_property()
    {
        Assert.Equal(
            2.0,
            Eval("var o = { x: 1 }; o.x &&= 2; o.x;"));
    }

    [Fact]
    public void LogicalOrAssign_on_computed_member()
    {
        Assert.Equal(
            9.0,
            Eval("var o = { v: 0 }; o['v'] ||= 9; o.v;"));
    }

    [Fact]
    public void LogicalAssign_returns_final_value()
    {
        // The assignment expression's result is the final
        // value of the target, whichever branch was taken.
        Assert.Equal(
            7.0,
            Eval("var x = null; (x ??= 7);"));
    }

    [Fact]
    public void LogicalAssign_on_member_returns_existing_when_short_circuits()
    {
        Assert.Equal(
            "orig",
            Eval("var o = { a: 'orig' }; (o.a ??= 'new');"));
    }

    // ========================================================
    // Combinations
    // ========================================================

    [Fact]
    public void Optional_chain_with_logical_assign_fallback()
    {
        // Common pattern: `obj.count ??= 0` to lazily init.
        Assert.Equal(
            0.0,
            Eval("var o = {}; o.count ??= 0; o.count;"));
    }

    [Fact]
    public void Nullish_coalescing_binds_looser_than_comparison()
    {
        // a ?? b > c parses as a ?? (b > c)? Or (a ?? b) > c?
        // Spec: ?? is at precedence 3 (below comparison at 7),
        // so `null ?? 1 > 0` is `null ?? (1 > 0)` = true.
        Assert.Equal(true, Eval("null ?? 1 > 0;"));
    }
}
