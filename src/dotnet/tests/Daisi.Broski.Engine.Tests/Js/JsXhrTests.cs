using System.Text;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Phase 5c — real <c>XMLHttpRequest</c> backed by the engine's
/// <see cref="JsEngine.FetchHandler"/>. Tests install a stub
/// handler so the suite stays offline. Coverage: open / send,
/// readyState transitions, response decoding for the four
/// responseType modes, request header propagation, error
/// dispatch, abort, and synchronous mode.
/// </summary>
public class JsXhrTests
{
    /// <summary>Construct an engine wired with a stub handler
    /// that records the incoming request and returns the given
    /// canned response.</summary>
    private static (JsEngine engine, List<JsFetchRequest> recorded)
        EngineWithStub(int status, string body, string contentType = "text/plain")
    {
        var engine = new JsEngine();
        var recorded = new List<JsFetchRequest>();
        engine.FetchHandler = req =>
        {
            recorded.Add(req);
            var headers = new JsHeaders();
            headers.Append("content-type", contentType);
            headers.Append("x-echo-method", req.Method);
            return new JsFetchResponse
            {
                Status = status,
                StatusText = status == 200 ? "OK" : "Other",
                Url = req.Url,
                Headers = headers,
                Body = Encoding.UTF8.GetBytes(body),
            };
        };
        return (engine, recorded);
    }

    [Fact]
    public void Xhr_constructor_exists_and_starts_unsent()
    {
        var (eng, _) = EngineWithStub(200, "");
        Assert.Equal(0.0, eng.Evaluate("new XMLHttpRequest().readyState;"));
    }

    [Fact]
    public void Open_transitions_to_OPENED()
    {
        var (eng, _) = EngineWithStub(200, "");
        Assert.Equal(
            1.0,
            eng.Evaluate(@"
                var x = new XMLHttpRequest();
                x.open('GET', 'http://example.com/');
                x.readyState;
            "));
    }

    [Fact]
    public void Send_walks_through_all_states_and_delivers_responseText()
    {
        var (eng, _) = EngineWithStub(200, "hi there");
        eng.Evaluate(@"
            var x = new XMLHttpRequest();
            var states = [];
            x.onreadystatechange = function () { states.push(x.readyState); };
            x.open('GET', 'http://example.com/');
            x.send();
        ");
        eng.DrainEventLoop();
        // OPENED fires from send(), then HEADERS_RECEIVED, LOADING, DONE.
        Assert.Equal("1,2,3,4", eng.Evaluate("states.join(',');"));
        Assert.Equal("hi there", eng.Evaluate("x.responseText;"));
        Assert.Equal(200.0, eng.Evaluate("x.status;"));
    }

    [Fact]
    public void Load_event_fires_after_DONE()
    {
        var (eng, _) = EngineWithStub(200, "ok");
        eng.Evaluate(@"
            var fired = false;
            var stateAtLoad = -1;
            var x = new XMLHttpRequest();
            x.onload = function () { fired = true; stateAtLoad = x.readyState; };
            x.open('GET', 'http://example.com/');
            x.send();
        ");
        eng.DrainEventLoop();
        Assert.Equal(true, eng.Evaluate("fired;"));
        Assert.Equal(4.0, eng.Evaluate("stateAtLoad;"));
    }

    [Fact]
    public void SetRequestHeader_appends_to_outgoing_request()
    {
        var (eng, recorded) = EngineWithStub(200, "");
        eng.Evaluate(@"
            var x = new XMLHttpRequest();
            x.open('GET', 'http://example.com/');
            x.setRequestHeader('X-Custom', 'a');
            x.setRequestHeader('X-Custom', 'b');
            x.send();
        ");
        eng.DrainEventLoop();
        Assert.Single(recorded);
        Assert.Equal("a, b", recorded[0].Headers.GetHeader("x-custom"));
    }

    [Fact]
    public void GetResponseHeader_reads_a_single_header()
    {
        var (eng, _) = EngineWithStub(200, "ok", "application/json");
        eng.Evaluate(@"
            var x = new XMLHttpRequest();
            var ct;
            x.onload = function () { ct = x.getResponseHeader('content-type'); };
            x.open('GET', 'http://example.com/');
            x.send();
        ");
        eng.DrainEventLoop();
        Assert.Equal("application/json", eng.Evaluate("ct;"));
    }

    [Fact]
    public void GetAllResponseHeaders_returns_each_header_on_its_own_line()
    {
        var (eng, _) = EngineWithStub(200, "ok");
        eng.Evaluate(@"
            var x = new XMLHttpRequest();
            var raw;
            x.onload = function () { raw = x.getAllResponseHeaders(); };
            x.open('GET', 'http://example.com/');
            x.send();
        ");
        eng.DrainEventLoop();
        var raw = (string)eng.Evaluate("raw;")!;
        Assert.Contains("content-type: text/plain", raw);
        Assert.Contains("x-echo-method: GET", raw);
        Assert.EndsWith("\r\n", raw);
    }

    [Fact]
    public void ResponseType_json_parses_response_into_an_object()
    {
        var (eng, _) = EngineWithStub(200, "{\"count\":7}", "application/json");
        eng.Evaluate(@"
            var x = new XMLHttpRequest();
            var n;
            x.responseType = 'json';
            x.onload = function () { n = x.response.count; };
            x.open('GET', 'http://example.com/');
            x.send();
        ");
        eng.DrainEventLoop();
        Assert.Equal(7.0, eng.Evaluate("n;"));
    }

    [Fact]
    public void ResponseType_arraybuffer_returns_an_arraybuffer()
    {
        var (eng, _) = EngineWithStub(200, "ab");
        eng.Evaluate(@"
            var x = new XMLHttpRequest();
            var bytes;
            x.responseType = 'arraybuffer';
            x.onload = function () {
                var v = new Uint8Array(x.response);
                bytes = v[0] + ',' + v[1];
            };
            x.open('GET', 'http://example.com/');
            x.send();
        ");
        eng.DrainEventLoop();
        Assert.Equal("97,98", eng.Evaluate("bytes;"));
    }

    [Fact]
    public void ResponseType_blob_returns_a_blob()
    {
        var (eng, _) = EngineWithStub(200, "hi", "image/png");
        eng.Evaluate(@"
            var x = new XMLHttpRequest();
            var t, sz;
            x.responseType = 'blob';
            x.onload = function () { t = x.response.type; sz = x.response.size; };
            x.open('GET', 'http://example.com/');
            x.send();
        ");
        eng.DrainEventLoop();
        Assert.Equal("image/png", eng.Evaluate("t;"));
        Assert.Equal(2.0, eng.Evaluate("sz;"));
    }

    [Fact]
    public void Send_with_string_body_sets_content_type_and_propagates()
    {
        var (eng, recorded) = EngineWithStub(200, "");
        eng.Evaluate(@"
            var x = new XMLHttpRequest();
            x.open('POST', 'http://example.com/');
            x.send('hello');
        ");
        eng.DrainEventLoop();
        Assert.Single(recorded);
        Assert.Equal("hello", Encoding.UTF8.GetString(recorded[0].Body));
        Assert.Equal("text/plain;charset=UTF-8",
            recorded[0].Headers.GetHeader("content-type"));
    }

    [Fact]
    public void Send_with_FormData_emits_multipart_body()
    {
        var (eng, recorded) = EngineWithStub(200, "");
        eng.Evaluate(@"
            var f = new FormData();
            f.append('a', '1');
            f.append('b', '2');
            var x = new XMLHttpRequest();
            x.open('POST', 'http://example.com/');
            x.send(f);
        ");
        eng.DrainEventLoop();
        Assert.Single(recorded);
        var ct = recorded[0].Headers.GetHeader("content-type")!;
        Assert.StartsWith("multipart/form-data; boundary=", ct);
        var body = Encoding.UTF8.GetString(recorded[0].Body);
        Assert.Contains("name=\"a\"", body);
        Assert.Contains("\r\n1\r\n", body);
        Assert.Contains("name=\"b\"", body);
    }

    [Fact]
    public void Network_failure_fires_error_and_loadend()
    {
        var engine = new JsEngine();
        engine.FetchHandler = req => throw new InvalidOperationException("boom");
        engine.Evaluate(@"
            var events = [];
            var x = new XMLHttpRequest();
            x.onload = function () { events.push('load'); };
            x.onerror = function () { events.push('error:' + x.status); };
            x.onloadend = function () { events.push('loadend'); };
            x.open('GET', 'http://example.com/');
            x.send();
        ");
        engine.DrainEventLoop();
        Assert.Equal("error:0,loadend", engine.Evaluate("events.join(',');"));
        Assert.Equal(4.0, engine.Evaluate("x.readyState;"));
    }

    [Fact]
    public void Abort_during_pending_send_fires_abort_and_loadend()
    {
        // Sequence: open → send → abort (before microtask runs)
        // → drain. The pending fetch is dropped; abort + loadend
        // fire synchronously in abort(); the queued microtask
        // becomes a no-op when generation has bumped.
        var engine = new JsEngine();
        engine.FetchHandler = req => new JsFetchResponse { Status = 200 };
        engine.Evaluate(@"
            var events = [];
            var x = new XMLHttpRequest();
            x.onload = function () { events.push('load'); };
            x.onabort = function () { events.push('abort'); };
            x.onloadend = function () { events.push('loadend'); };
            x.open('GET', 'http://example.com/');
            x.send();
            x.abort();
        ");
        engine.DrainEventLoop();
        Assert.Equal("abort,loadend", engine.Evaluate("events.join(',');"));
    }

    [Fact]
    public void SetRequestHeader_before_open_throws()
    {
        // Catch the throw inside JS so we can inspect the JS-side
        // `error.name` rather than the engine's stringified
        // wrapper exception text.
        var (eng, _) = EngineWithStub(200, "");
        Assert.Equal(
            "InvalidStateError",
            eng.Evaluate(@"
                var name;
                try {
                    var x = new XMLHttpRequest();
                    x.setRequestHeader('X-Custom', 'v');
                } catch (e) {
                    name = e.name;
                }
                name;
            "));
    }

    [Fact]
    public void AddEventListener_load_works_alongside_onload()
    {
        var (eng, _) = EngineWithStub(200, "");
        eng.Evaluate(@"
            var seen = [];
            var x = new XMLHttpRequest();
            x.onload = function () { seen.push('on'); };
            x.addEventListener('load', function () { seen.push('listener'); });
            x.open('GET', 'http://example.com/');
            x.send();
        ");
        eng.DrainEventLoop();
        Assert.Equal("on,listener", eng.Evaluate("seen.join(',');"));
    }

    [Fact]
    public void Sync_mode_runs_inline_without_drain()
    {
        var (eng, _) = EngineWithStub(200, "synced");
        // open(method, url, false) → sync. send() blocks; readyState
        // is DONE before send() returns; no event-loop drain needed.
        var got = (string)eng.Evaluate(@"
            var x = new XMLHttpRequest();
            x.open('GET', 'http://example.com/', false);
            x.send();
            x.responseText;
        ")!;
        Assert.Equal("synced", got);
    }

    [Fact]
    public void Status_text_constants_exposed()
    {
        var (eng, _) = EngineWithStub(200, "");
        Assert.Equal(0.0, eng.Evaluate("XMLHttpRequest.UNSENT;"));
        Assert.Equal(1.0, eng.Evaluate("XMLHttpRequest.OPENED;"));
        Assert.Equal(2.0, eng.Evaluate("XMLHttpRequest.HEADERS_RECEIVED;"));
        Assert.Equal(3.0, eng.Evaluate("XMLHttpRequest.LOADING;"));
        Assert.Equal(4.0, eng.Evaluate("XMLHttpRequest.DONE;"));
    }
}
