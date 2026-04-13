using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Phase 5b — <c>FileReader</c>. Verifies that readAsX
/// operations deliver results via the event loop, fire the
/// on-style handlers and <c>addEventListener</c> callbacks
/// in spec order, expose <c>readyState</c> transitions, and
/// support <c>abort</c>.
/// </summary>
public class JsFileReaderTests
{
    private static JsEngine RunToCompletion(string src)
    {
        var eng = new JsEngine();
        eng.Evaluate(src);
        eng.DrainEventLoop();
        return eng;
    }

    [Fact]
    public void FileReader_constructor_exists()
    {
        Assert.Equal("function", RunToCompletion("").Evaluate("typeof FileReader;"));
    }

    [Fact]
    public void Initial_readyState_is_empty()
    {
        Assert.Equal(0.0, RunToCompletion("").Evaluate("new FileReader().readyState;"));
    }

    [Fact]
    public void ReadAsText_delivers_the_decoded_string()
    {
        var eng = RunToCompletion(@"
            var r = new FileReader();
            var out;
            r.onload = function () { out = r.result; };
            r.readAsText(new Blob(['hi there']));
        ");
        Assert.Equal("hi there", eng.Evaluate("out;"));
        Assert.Equal(2.0, eng.Evaluate("r.readyState;"));
    }

    [Fact]
    public void ReadAsDataURL_produces_data_url()
    {
        var eng = RunToCompletion(@"
            var r = new FileReader();
            var out;
            r.onload = function () { out = r.result; };
            r.readAsDataURL(new Blob(['hi'], { type: 'text/plain' }));
        ");
        // 'hi' → 'aGk=' in base64.
        Assert.Equal("data:text/plain;base64,aGk=", eng.Evaluate("out;"));
    }

    [Fact]
    public void ReadAsDataURL_defaults_to_octet_stream()
    {
        var eng = RunToCompletion(@"
            var r = new FileReader();
            var out;
            r.onload = function () { out = r.result; };
            r.readAsDataURL(new Blob(['x']));
        ");
        Assert.Equal(
            "data:application/octet-stream;base64,eA==",
            eng.Evaluate("out;"));
    }

    [Fact]
    public void ReadAsArrayBuffer_delivers_an_ArrayBuffer()
    {
        var eng = RunToCompletion(@"
            var r = new FileReader();
            var bytes;
            r.onload = function () {
                var view = new Uint8Array(r.result);
                bytes = view[0] + ',' + view[1] + ',' + view.length;
            };
            r.readAsArrayBuffer(new Blob(['ab']));
        ");
        Assert.Equal("97,98,2", eng.Evaluate("bytes;"));
    }

    [Fact]
    public void LoadEvents_fire_in_spec_order()
    {
        var eng = RunToCompletion(@"
            var r = new FileReader();
            var order = [];
            r.onloadstart = function () { order.push('loadstart'); };
            r.onprogress = function () { order.push('progress'); };
            r.onload = function () { order.push('load'); };
            r.onloadend = function () { order.push('loadend'); };
            r.readAsText(new Blob(['x']));
        ");
        Assert.Equal(
            "loadstart,progress,load,loadend",
            eng.Evaluate("order.join(',');"));
    }

    [Fact]
    public void AddEventListener_listeners_fire_alongside_on_handlers()
    {
        var eng = RunToCompletion(@"
            var r = new FileReader();
            var order = [];
            r.onload = function () { order.push('on-load'); };
            r.addEventListener('load', function () { order.push('listener-1'); });
            r.addEventListener('load', function () { order.push('listener-2'); });
            r.readAsText(new Blob(['x']));
        ");
        Assert.Equal(
            "on-load,listener-1,listener-2",
            eng.Evaluate("order.join(',');"));
    }

    [Fact]
    public void RemoveEventListener_unregisters()
    {
        var eng = RunToCompletion(@"
            var r = new FileReader();
            var count = 0;
            var cb = function () { count++; };
            r.addEventListener('load', cb);
            r.removeEventListener('load', cb);
            r.readAsText(new Blob(['x']));
        ");
        Assert.Equal(0.0, eng.Evaluate("count;"));
    }

    [Fact]
    public void Non_blob_argument_delivers_an_error_event()
    {
        var eng = RunToCompletion(@"
            var r = new FileReader();
            var err;
            r.onerror = function () { err = r.error.name; };
            r.readAsText('not a blob');
        ");
        Assert.Equal("NotFoundError", eng.Evaluate("err;"));
    }

    [Fact]
    public void Abort_cancels_an_in_flight_read()
    {
        var eng = new JsEngine();
        eng.Evaluate(@"
            var r = new FileReader();
            var events = [];
            r.onload = function () { events.push('load'); };
            r.onabort = function () { events.push('abort'); };
            r.onloadend = function () { events.push('loadend'); };
            r.readAsText(new Blob(['x']));
            r.abort();  // before the microtask can fire
        ");
        // abort() is synchronous → fires abort + loadend right
        // away, the pending load microtask becomes a no-op.
        eng.DrainEventLoop();
        Assert.Equal("abort,loadend", eng.Evaluate("events.join(',');"));
    }

    [Fact]
    public void Event_target_points_at_the_reader()
    {
        var eng = RunToCompletion(@"
            var r = new FileReader();
            var same;
            r.onload = function (evt) {
                same = (evt.target === r) && (evt.type === 'load');
            };
            r.readAsText(new Blob(['x']));
        ");
        Assert.Equal(JsValue.True, eng.Evaluate("same;"));
    }

    [Fact]
    public void FileReader_has_EMPTY_LOADING_DONE_constants()
    {
        Assert.Equal(0.0, RunToCompletion("").Evaluate("FileReader.EMPTY;"));
        Assert.Equal(1.0, RunToCompletion("").Evaluate("FileReader.LOADING;"));
        Assert.Equal(2.0, RunToCompletion("").Evaluate("FileReader.DONE;"));
    }
}
