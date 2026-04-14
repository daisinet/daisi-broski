using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Regex literal support: context-aware lexing of
/// <c>/pattern/flags</c>, <c>new RegExp(...)</c>
/// construction, <c>RegExp.prototype.test</c> /
/// <c>exec</c>, and the regex-aware methods on
/// <c>String.prototype</c> (<c>match</c> /
/// <c>matchAll</c> / <c>replace</c> /
/// <c>replaceAll</c> / <c>search</c> / <c>split</c>).
/// </summary>
public class JsRegExpTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // Literal syntax + disambiguation
    // ========================================================

    [Fact]
    public void Regex_literal_basic()
    {
        Assert.Equal("object", Eval("typeof /abc/;"));
    }

    [Fact]
    public void Regex_literal_flags_exposed()
    {
        Assert.Equal("gi", Eval("/abc/gi.flags;"));
    }

    [Fact]
    public void Regex_literal_source_exposed()
    {
        Assert.Equal("a\\d+", Eval(@"/a\d+/.source;"));
    }

    [Fact]
    public void Division_context_still_works()
    {
        // `a / b / c` — both slashes are division. Our
        // context tracker must correctly see `b` and `c`
        // as preceding identifiers.
        Assert.Equal(1.0, Eval("var a = 20, b = 4, c = 5; a / b / c;"));
    }

    [Fact]
    public void Regex_after_return()
    {
        // `return /abc/` — classic disambiguation case.
        Assert.Equal(
            true,
            Eval("function f() { return /^ab/.test('abc'); } f();"));
    }

    [Fact]
    public void Regex_after_equals()
    {
        Assert.Equal(
            true,
            Eval("var r = /foo/; r instanceof RegExp;"));
    }

    [Fact]
    public void Regex_inside_character_class_escapes_slash()
    {
        // `[/]` is a class containing `/` — the slash
        // there does NOT terminate the regex.
        Assert.Equal(
            true,
            Eval(@"/[/]/.test('a/b');"));
    }

    [Fact]
    public void Regex_with_escaped_slash()
    {
        Assert.Equal(
            true,
            Eval(@"/a\/b/.test('a/b');"));
    }

    // ========================================================
    // RegExp constructor
    // ========================================================

    [Fact]
    public void RegExp_constructor_from_string()
    {
        Assert.Equal(
            true,
            Eval("new RegExp('abc').test('xabcx');"));
    }

    [Fact]
    public void RegExp_constructor_with_flags()
    {
        Assert.Equal(
            true,
            Eval("new RegExp('abc', 'i').test('ABC');"));
    }

    [Fact]
    public void RegExp_constructor_from_existing_regex()
    {
        Assert.Equal(
            "abc",
            Eval("new RegExp(/abc/i).source;"));
    }

    // ========================================================
    // RegExp.prototype.test / exec
    // ========================================================

    [Fact]
    public void Test_returns_true_on_match()
    {
        Assert.Equal(true, Eval(@"/^hello/.test('hello world');"));
    }

    [Fact]
    public void Test_returns_false_on_no_match()
    {
        Assert.Equal(false, Eval(@"/^bye/.test('hello');"));
    }

    [Fact]
    public void Exec_returns_match_array()
    {
        Assert.Equal(
            "abc",
            Eval(@"/abc/.exec('xabcy')[0];"));
    }

    [Fact]
    public void Exec_captures_groups()
    {
        Assert.Equal(
            "world",
            Eval(@"/hello (\w+)/.exec('hello world!')[1];"));
    }

    [Fact]
    public void Exec_index_property()
    {
        Assert.Equal(
            1.0,
            Eval(@"/abc/.exec('xabcy').index;"));
    }

    [Fact]
    public void Exec_returns_null_on_no_match()
    {
        Assert.Equal(
            JsValue.Null,
            Eval(@"/xyz/.exec('abc');"));
    }

    // ========================================================
    // String.prototype.match / matchAll
    // ========================================================

    [Fact]
    public void String_match_no_global_returns_first_match_with_groups()
    {
        Assert.Equal(
            "42",
            Eval(@"'count is 42 here'.match(/\d+/)[0];"));
    }

    [Fact]
    public void String_match_global_returns_all_matches()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"'a1b2c3'.match(/\d/g).join(',');"));
    }

    [Fact]
    public void String_match_no_match_returns_null()
    {
        Assert.Equal(
            JsValue.Null,
            Eval(@"'abc'.match(/\d/);"));
    }

    [Fact]
    public void String_matchAll_requires_global_flag()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { 'abc'.matchAll(/./); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    [Fact]
    public void String_matchAll_returns_array_of_match_arrays()
    {
        Assert.Equal(
            "a:1,b:2",
            Eval(@"
                var out = [];
                var matches = 'a1b2'.matchAll(/([a-z])(\d)/g);
                for (var m of matches) {
                    out.push(m[1] + ':' + m[2]);
                }
                out.join(',');
            "));
    }

    // ========================================================
    // String.prototype.replace / replaceAll
    // ========================================================

    [Fact]
    public void String_replace_first_match_only()
    {
        // Non-global replace substitutes only the first
        // match, leaving the '2' in place.
        Assert.Equal(
            "Xabc2def",
            Eval(@"'1abc2def'.replace(/\d/, 'X');"));
    }

    [Fact]
    public void String_replace_global()
    {
        Assert.Equal(
            "XabcXdef",
            Eval(@"'1abc2def'.replace(/\d/g, 'X');"));
    }

    [Fact]
    public void String_replace_with_function()
    {
        Assert.Equal(
            "(1)abc(2)def",
            Eval(@"
                '1abc2def'.replace(/\d/g, function (m) {
                    return '(' + m + ')';
                });
            "));
    }

    [Fact]
    public void String_replace_with_capture_group_reference()
    {
        Assert.Equal(
            "foo-bar",
            Eval(@"'bar-foo'.replace(/(\w+)-(\w+)/, '$2-$1');"));
    }

    [Fact]
    public void String_replaceAll_requires_global_flag()
    {
        Assert.Equal(
            true,
            Eval(@"
                try { 'abc'.replaceAll(/./, 'x'); false; }
                catch (e) { e instanceof TypeError; }
            "));
    }

    [Fact]
    public void String_replaceAll_string_needle()
    {
        Assert.Equal(
            "XbXbX",
            Eval(@"'ababa'.replaceAll('a', 'X');"));
    }

    // ========================================================
    // String.prototype.search / split
    // ========================================================

    [Fact]
    public void String_search_returns_first_match_index()
    {
        Assert.Equal(
            3.0,
            Eval(@"'abc123'.search(/\d/);"));
    }

    [Fact]
    public void String_search_returns_negative_one_on_no_match()
    {
        Assert.Equal(
            -1.0,
            Eval(@"'abcdef'.search(/\d/);"));
    }

    [Fact]
    public void String_split_with_regex()
    {
        Assert.Equal(
            "one,two,three",
            Eval(@"'one  two   three'.split(/\s+/).join(',');"));
    }

    [Fact]
    public void String_split_preserves_string_separator_behavior()
    {
        // The string-split path should still work for a
        // plain string separator.
        Assert.Equal(
            "a,b,c",
            Eval(@"'a,b,c'.split(',').join(',');"));
    }

    // ========================================================
    // Flags + ignoreCase behavior
    // ========================================================

    [Fact]
    public void IgnoreCase_flag()
    {
        Assert.Equal(true, Eval(@"/hello/i.test('HELLO');"));
    }

    [Fact]
    public void Multiline_flag()
    {
        Assert.Equal(
            true,
            Eval(@"/^second/m.test('first\nsecond');"));
    }

    // ========================================================
    // Global state via lastIndex
    // ========================================================

    [Fact]
    public void Global_regex_advances_lastIndex()
    {
        // Spec: lastIndex is set to `match.index + match.length`
        // after each successful exec. For `/\d/g` on "a1b2c3":
        //   before any match: 0
        //   after '1' (at index 1): 1 + 1 = 2
        //   after '2' (at index 3): 3 + 1 = 4
        //   after '3' (at index 5): 5 + 1 = 6
        //   after the 4th call: no more matches → reset to 0
        Assert.Equal(
            "0,2,4,6,reset",
            Eval(@"
                var r = /\d/g;
                var input = 'a1b2c3';
                var indices = [];
                indices.push(r.lastIndex);
                r.exec(input);
                indices.push(r.lastIndex);
                r.exec(input);
                indices.push(r.lastIndex);
                r.exec(input);
                indices.push(r.lastIndex);
                r.exec(input); // no more matches → resets to 0
                indices.push(r.lastIndex === 0 ? 'reset' : String(r.lastIndex));
                indices.join(',');
            "));
    }

    // -------- Named capture groups (ES2018) --------
    // Regression guard for the Blazor-breaker: Blazor Web's
    // comment-marker discovery parses
    //   /^\s*Blazor:[^{]*(?<descriptor>.*)$/
    // and reads match.groups.descriptor. Before this fix,
    // BuildMatchArray set match[1] but never populated the
    // `groups` object, so Blazor silently saw no markers
    // and never started the server circuit.

    [Fact]
    public void Named_group_populates_groups_object_on_exec_match()
    {
        Assert.Equal("hello", Eval(@"
            var r = /^(?<greeting>\w+)/;
            var m = r.exec('hello world');
            m.groups.greeting;
        "));
    }

    [Fact]
    public void Named_group_blazor_descriptor_pattern()
    {
        Assert.Equal(@"{""type"":""server""}", Eval(@"
            var Mt = /^\s*Blazor:[^{]*(?<descriptor>.*)$/;
            var m = Mt.exec('Blazor:{""type"":""server""}');
            m.groups.descriptor;
        "));
    }

    [Fact]
    public void Multiple_named_groups_all_populate()
    {
        Assert.Equal("2026-04-14", Eval(@"
            var r = /(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})/;
            var m = r.exec('date is 2026-04-14 today');
            m.groups.year + '-' + m.groups.month + '-' + m.groups.day;
        "));
    }

    [Fact]
    public void No_named_groups_leaves_groups_undefined()
    {
        // Per ES2018 spec: match.groups is undefined when
        // the pattern has no named groups (not {} or null).
        Assert.Equal("undefined", Eval(@"
            var r = /(\w+)/;
            var m = r.exec('abc');
            typeof m.groups;
        "));
    }

    [Fact]
    public void Named_group_via_string_match()
    {
        // str.match(regex) returns the same match-array
        // shape as regex.exec — groups should populate
        // there too.
        Assert.Equal("abc", Eval(@"
            var m = 'hello abc world'.match(/(?<word>abc)/);
            m.groups.word;
        "));
    }
}
