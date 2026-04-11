using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsControlFlowTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // -------- for..in --------

    [Fact]
    public void For_in_enumerates_own_object_keys_in_insertion_order()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var o = {a: 1, b: 2, c: 3};
            var keys = '';
            for (var k in o) {
                keys = keys + k;
            }
        ");
        Assert.Equal("abc", eng.Globals["keys"]);
    }

    [Fact]
    public void For_in_visits_every_key_and_sums_values()
    {
        Assert.Equal(
            6.0,
            Eval(@"
                var o = {x: 1, y: 2, z: 3};
                var sum = 0;
                for (var k in o) { sum = sum + o[k]; }
                sum;
            "));
    }

    [Fact]
    public void For_in_over_array_yields_index_strings()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var a = ['x', 'y', 'z'];
            var out = '';
            for (var i in a) { out = out + i; }
        ");
        Assert.Equal("012", eng.Globals["out"]);
    }

    [Fact]
    public void For_in_array_length_is_not_enumerated()
    {
        // A real ES5 array should iterate only the indices and
        // any string-keyed own properties — not the non-enumerable
        // `length` property.
        var eng = new JsEngine();
        eng.Evaluate(@"
            var a = [10, 20, 30];
            var sawLength = false;
            for (var k in a) { if (k === 'length') { sawLength = true; } }
        ");
        Assert.Equal(false, eng.Globals["sawLength"]);
    }

    [Fact]
    public void For_in_null_or_undefined_iterates_zero_keys()
    {
        Assert.Equal(
            0.0,
            Eval(@"
                var n = 0;
                for (var k in null) { n = n + 1; }
                for (var k2 in undefined) { n = n + 1; }
                n;
            "));
    }

    [Fact]
    public void For_in_walks_prototype_chain()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            function Parent() {}
            Parent.prototype.inherited = 'y';
            function Child() { this.own = 'x'; }
            Child.prototype = new Parent();
            var c = new Child();
            var keys = '';
            for (var k in c) { keys = keys + k + ','; }
        ");
        // Own first ('own'), then inherited ('inherited').
        Assert.Contains("own", (string)eng.Globals["keys"]!);
        Assert.Contains("inherited", (string)eng.Globals["keys"]!);
    }

    [Fact]
    public void For_in_break_exits()
    {
        Assert.Equal(
            "a",
            Eval(@"
                var o = {a: 1, b: 2, c: 3};
                var out = '';
                for (var k in o) { out = out + k; break; }
                out;
            "));
    }

    [Fact]
    public void For_in_continue_skips_iteration()
    {
        Assert.Equal(
            "ac",
            Eval(@"
                var o = {a: 1, b: 2, c: 3};
                var out = '';
                for (var k in o) {
                    if (k === 'b') { continue; }
                    out = out + k;
                }
                out;
            "));
    }

    [Fact]
    public void For_in_with_non_var_identifier_binds_to_outer_variable()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var k;
            var o = {x: 1, y: 2};
            var out = '';
            for (k in o) { out = out + k; }
        ");
        Assert.Equal("xy", eng.Globals["out"]);
        Assert.Equal("y", eng.Globals["k"]); // last assigned
    }

    // -------- switch --------

    [Fact]
    public void Switch_runs_matching_case_then_breaks()
    {
        Assert.Equal(
            "two",
            Eval(@"
                var x = 2;
                var out = '';
                switch (x) {
                    case 1: out = 'one'; break;
                    case 2: out = 'two'; break;
                    case 3: out = 'three'; break;
                }
                out;
            "));
    }

    [Fact]
    public void Switch_runs_default_when_no_case_matches()
    {
        Assert.Equal(
            "other",
            Eval(@"
                var x = 99;
                var out = '';
                switch (x) {
                    case 1: out = 'one'; break;
                    default: out = 'other'; break;
                }
                out;
            "));
    }

    [Fact]
    public void Switch_falls_through_without_break()
    {
        Assert.Equal(
            "ab",
            Eval(@"
                var out = '';
                switch (1) {
                    case 1: out = out + 'a';
                    case 2: out = out + 'b'; break;
                    case 3: out = out + 'c';
                }
                out;
            "));
    }

    [Fact]
    public void Switch_with_empty_cases_all_fall_through_to_body()
    {
        // case 1 and case 2 share the same body.
        Assert.Equal(
            "shared",
            Eval(@"
                var out = '';
                switch (2) {
                    case 1:
                    case 2:
                        out = 'shared';
                        break;
                }
                out;
            "));
    }

    [Fact]
    public void Switch_uses_strict_equality()
    {
        // '1' !== 1 — no match.
        Assert.Equal(
            "none",
            Eval(@"
                var out = 'none';
                switch (1) {
                    case '1': out = 'loose'; break;
                }
                out;
            "));
    }

    [Fact]
    public void Switch_without_default_and_no_match_is_noop()
    {
        Assert.Equal(
            "initial",
            Eval(@"
                var out = 'initial';
                switch (42) {
                    case 1: out = 'a'; break;
                    case 2: out = 'b'; break;
                }
                out;
            "));
    }

    [Fact]
    public void Default_in_the_middle_of_cases_works()
    {
        // default can appear anywhere — fall-through order is
        // source order.
        Assert.Equal(
            "defaulttwo",
            Eval(@"
                var out = '';
                switch (99) {
                    case 1: out = 'one'; break;
                    default: out = out + 'default';
                    case 2: out = out + 'two'; break;
                }
                out;
            "));
    }

    [Fact]
    public void Nested_switch_inner_break_only_exits_inner()
    {
        Assert.Equal(
            "outer",
            Eval(@"
                var out = '';
                switch (1) {
                    case 1:
                        switch (10) {
                            case 10: break;
                        }
                        out = 'outer';
                        break;
                }
                out;
            "));
    }

    // -------- labeled break / continue --------

    [Fact]
    public void Labeled_break_exits_outer_loop()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var found = null;
            outer:
            for (var i = 0; i < 5; i++) {
                for (var j = 0; j < 5; j++) {
                    if (i === 2 && j === 3) { found = i * 10 + j; break outer; }
                }
            }
        ");
        Assert.Equal(23.0, eng.Globals["found"]);
        // Outer loop was broken, so i should be 2 (not 4).
        Assert.Equal(2.0, eng.Globals["i"]);
    }

    [Fact]
    public void Labeled_continue_skips_outer_iteration()
    {
        // continue outer; should skip to the next iteration of
        // outer, not inner.
        var eng = new JsEngine();
        eng.Evaluate(@"
            var visited = 0;
            outer:
            for (var i = 0; i < 3; i++) {
                for (var j = 0; j < 3; j++) {
                    if (j === 1) { continue outer; }
                    visited = visited + 1;
                }
            }
        ");
        // Inner runs j=0 only (1 iteration) three times = 3 visits.
        Assert.Equal(3.0, eng.Globals["visited"]);
    }

    [Fact]
    public void Unlabeled_break_inside_switch_inside_loop_only_exits_switch()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var iterations = 0;
            for (var i = 0; i < 3; i++) {
                switch (i) {
                    case 1: break;
                }
                iterations = iterations + 1;
            }
        ");
        Assert.Equal(3.0, eng.Globals["iterations"]);
    }

    [Fact]
    public void Labeled_break_inside_switch_escapes_the_outer_loop()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var iterations = 0;
            outer:
            for (var i = 0; i < 5; i++) {
                switch (i) {
                    case 2: break outer;
                }
                iterations = iterations + 1;
            }
        ");
        Assert.Equal(2.0, eng.Globals["iterations"]);
    }

    [Fact]
    public void Continue_in_switch_targets_outer_loop()
    {
        // A bare `continue` inside a switch inside a loop
        // continues the loop, not the switch.
        var eng = new JsEngine();
        eng.Evaluate(@"
            var out = '';
            for (var i = 0; i < 3; i++) {
                switch (i) {
                    case 1: continue;
                }
                out = out + i;
            }
        ");
        Assert.Equal("02", eng.Globals["out"]);
    }

    [Fact]
    public void Labeled_break_from_labeled_block()
    {
        // A non-loop labeled block also accepts break.
        Assert.Equal(
            "before",
            Eval(@"
                var out = 'before';
                block: {
                    break block;
                    out = 'after';
                }
                out;
            "));
    }

    [Fact]
    public void Break_to_non_existent_label_is_a_compile_error()
    {
        Assert.Throws<JsCompileException>(() => Eval("for (var i = 0; i < 1; i++) { break nope; }"));
    }

    [Fact]
    public void Continue_to_non_loop_label_is_a_compile_error()
    {
        // Labeled block with only break semantics — continue
        // cannot target it.
        Assert.Throws<JsCompileException>(() =>
            Eval("block: { for (var i = 0; i < 1; i++) { continue block; } }"));
    }
}
