using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Phase 5b — <c>Blob</c> + <c>File</c> constructors, prototype
/// methods (text / arrayBuffer / bytes / slice), size/type
/// getters, and integration with <c>Response.prototype.blob</c>.
/// </summary>
public class JsBlobFileTests
{
    private static object? Eval(string src)
    {
        var eng = new JsEngine();
        var r = eng.Evaluate(src);
        eng.DrainEventLoop();
        return r;
    }

    [Fact]
    public void Blob_constructor_exists()
    {
        Assert.Equal("function", Eval("typeof Blob;"));
    }

    [Fact]
    public void Blob_size_defaults_to_zero_and_type_to_empty()
    {
        Assert.Equal(0.0, Eval("new Blob().size;"));
        Assert.Equal("", Eval("new Blob().type;"));
    }

    [Fact]
    public void Blob_from_string_parts_measures_utf8_bytes()
    {
        Assert.Equal(
            11.0,
            Eval("new Blob(['hello', ' ', 'world']).size;"));
    }

    [Fact]
    public void Blob_carries_declared_type()
    {
        Assert.Equal(
            "text/plain",
            Eval("new Blob(['x'], { type: 'text/plain' }).type;"));
    }

    [Fact]
    public void Blob_text_decodes_utf8()
    {
        var src = @"
            var b = new Blob(['hi there']);
            var out;
            b.text().then(function (v) { out = v; });
            out;
        ";
        var eng = new JsEngine();
        eng.Evaluate(src);
        eng.DrainEventLoop();
        Assert.Equal("hi there", eng.Evaluate("out;"));
    }

    [Fact]
    public void Blob_arrayBuffer_round_trips_bytes()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var b = new Blob(['ab']);
            var out;
            b.arrayBuffer().then(function (buf) {
                var view = new Uint8Array(buf);
                out = view[0] + ',' + view[1] + ',' + view.length;
            });
        ");
        eng.DrainEventLoop();
        Assert.Equal("97,98,2", eng.Evaluate("out;"));
    }

    [Fact]
    public void Blob_slice_produces_a_new_blob_of_the_right_size()
    {
        Assert.Equal(
            5.0,
            Eval("new Blob(['hello world']).slice(0, 5).size;"));
    }

    [Fact]
    public void Blob_slice_supports_negative_indices()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var b = new Blob(['hello world']);
            var s = b.slice(-5);
            var out;
            s.text().then(function (v) { out = v; });
        ");
        eng.DrainEventLoop();
        Assert.Equal("world", eng.Evaluate("out;"));
    }

    [Fact]
    public void Blob_slice_attaches_custom_type()
    {
        Assert.Equal(
            "application/json",
            Eval("new Blob(['{}']).slice(0, 2, 'application/json').type;"));
    }

    [Fact]
    public void Blob_accepts_arraybuffer_parts()
    {
        Assert.Equal(
            4.0,
            Eval(@"
                var buf = new ArrayBuffer(4);
                new Blob([buf]).size;
            "));
    }

    [Fact]
    public void File_has_name_and_lastModified()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var f = new File(['abc'], 'hello.txt',
                { type: 'text/plain', lastModified: 1234567890 });
        ");
        Assert.Equal("hello.txt", eng.Evaluate("f.name;"));
        Assert.Equal(1234567890.0, eng.Evaluate("f.lastModified;"));
        Assert.Equal("text/plain", eng.Evaluate("f.type;"));
        Assert.Equal(3.0, eng.Evaluate("f.size;"));
    }

    [Fact]
    public void File_inherits_from_Blob()
    {
        Assert.Equal(
            JsValue.True,
            Eval("new File(['x'], 'n') instanceof Blob;"));
    }

    [Fact]
    public void File_requires_name_argument()
    {
        var eng = new JsEngine();
        Assert.Throws<JsRuntimeException>(() => eng.Evaluate("new File(['x']);"));
    }

    [Fact]
    public void Response_blob_rehydrates_body_as_a_blob()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var out;
            new Response('hi there').blob().then(function (b) { out = b; });
        ");
        eng.DrainEventLoop();
        Assert.Equal(
            JsValue.True,
            eng.Evaluate("out instanceof Blob;"));
        Assert.Equal(
            8.0,
            eng.Evaluate("out.size;"));
    }
}
