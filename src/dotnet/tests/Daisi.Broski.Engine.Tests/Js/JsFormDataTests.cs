using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Phase 5b — <c>FormData</c> construction, mutation (append /
/// set / delete), lookup (get / getAll / has), and iteration
/// via <c>entries</c> / <c>keys</c> / <c>values</c> /
/// <c>[Symbol.iterator]</c>.
/// </summary>
public class JsFormDataTests
{
    private static object? Eval(string src)
    {
        var eng = new JsEngine();
        return eng.Evaluate(src);
    }

    [Fact]
    public void FormData_constructor_is_callable()
    {
        Assert.Equal("object", Eval("typeof new FormData();"));
    }

    [Fact]
    public void Append_and_get_round_trip()
    {
        Assert.Equal(
            "alice",
            Eval(@"
                var f = new FormData();
                f.append('name', 'alice');
                f.get('name');
            "));
    }

    [Fact]
    public void Append_keeps_multiple_values()
    {
        Assert.Equal(
            "a,b,c",
            Eval(@"
                var f = new FormData();
                f.append('k', 'a');
                f.append('k', 'b');
                f.append('k', 'c');
                f.getAll('k').join(',');
            "));
    }

    [Fact]
    public void Get_returns_null_for_missing_key()
    {
        Assert.Equal(
            JsValue.Null,
            Eval(@"
                var f = new FormData();
                f.get('nope');
            "));
    }

    [Fact]
    public void Has_reports_presence()
    {
        Assert.Equal(
            true,
            Eval(@"
                var f = new FormData();
                f.append('k', 'v');
                f.has('k');
            "));
        Assert.Equal(
            false,
            Eval(@"
                var f = new FormData();
                f.has('k');
            "));
    }

    [Fact]
    public void Delete_removes_every_entry_with_the_name()
    {
        Assert.Equal(
            0.0,
            Eval(@"
                var f = new FormData();
                f.append('k', 'a');
                f.append('k', 'b');
                f.delete('k');
                f.getAll('k').length;
            "));
    }

    [Fact]
    public void Set_replaces_existing_values_in_place()
    {
        Assert.Equal(
            "x,y",
            Eval(@"
                var f = new FormData();
                f.append('a', 'old1');
                f.append('b', 'y');
                f.append('a', 'old2');
                f.set('a', 'x');
                var out = [];
                f.forEach(function (v, k) { out.push(v); });
                out.join(',');
            "));
    }

    [Fact]
    public void Iteration_via_Symbol_iterator_yields_pairs()
    {
        Assert.Equal(
            "a:1,b:2",
            Eval(@"
                var f = new FormData();
                f.append('a', '1');
                f.append('b', '2');
                var out = [];
                for (var pair of f) { out.push(pair[0] + ':' + pair[1]); }
                out.join(',');
            "));
    }

    [Fact]
    public void Keys_and_values_iterators()
    {
        Assert.Equal(
            "a,b|1,2",
            Eval(@"
                var f = new FormData();
                f.append('a', '1');
                f.append('b', '2');
                var ks = [];
                var vs = [];
                var ki = f.keys();
                var step;
                while ((step = ki.next()).done === false) ks.push(step.value);
                var vi = f.values();
                while ((step = vi.next()).done === false) vs.push(step.value);
                ks.join(',') + '|' + vs.join(',');
            "));
    }

    [Fact]
    public void Blob_value_round_trips()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var f = new FormData();
            var b = new Blob(['hi'], { type: 'text/plain' });
            f.append('doc', b);
        ");
        Assert.Equal(
            "object",
            eng.Evaluate("typeof f.get('doc');"));
        Assert.Equal(
            2.0,
            eng.Evaluate("f.get('doc').size;"));
    }

    [Fact]
    public void Append_with_filename_rewraps_blob_as_file()
    {
        Assert.Equal(
            "upload.txt",
            Eval(@"
                var f = new FormData();
                f.append('doc', new Blob(['x']), 'upload.txt');
                f.get('doc').name;
            "));
    }

    [Fact]
    public void Numeric_values_coerce_to_strings()
    {
        Assert.Equal(
            "42",
            Eval(@"
                var f = new FormData();
                f.append('n', 42);
                f.get('n');
            "));
    }
}
