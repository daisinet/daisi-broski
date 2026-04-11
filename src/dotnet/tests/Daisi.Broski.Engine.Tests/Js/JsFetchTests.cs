using System.Text;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Slice 3c-6: <c>fetch</c> + <c>Headers</c> + <c>Request</c>
/// + <c>Response</c>. Tests install a stub
/// <see cref="JsEngine.FetchHandler"/> so the suite stays
/// fully offline — no test reaches real DNS or HTTPS.
/// </summary>
public class JsFetchTests
{
    /// <summary>
    /// Build a fresh engine wired with a stub handler
    /// returning the given status + body. The stub also
    /// records the incoming request so tests can assert
    /// on URL / method / headers.
    /// </summary>
    private static JsEngine EngineWithStub(int status, string body, string contentType = "text/plain")
    {
        var engine = new JsEngine();
        engine.FetchHandler = req =>
        {
            var headers = new JsHeaders
            {
                Prototype = (engine.Globals["Headers"] as JsFunction)?.Get("prototype") as JsObject,
            };
            headers.Append("content-type", contentType);
            headers.Append("x-stub-method", req.Method);
            return new JsFetchResponse
            {
                Status = status,
                StatusText = status == 200 ? "OK" : "Not OK",
                Url = req.Url,
                Headers = headers,
                Body = Encoding.UTF8.GetBytes(body),
            };
        };
        return engine;
    }

    // ========================================================
    // Headers
    // ========================================================

    [Fact]
    public void Headers_constructor_from_object_literal()
    {
        var eng = new JsEngine();
        Assert.Equal(
            "text/html",
            eng.Evaluate("new Headers({ 'Content-Type': 'text/html' }).get('content-type');"));
    }

    [Fact]
    public void Headers_set_is_case_insensitive()
    {
        var eng = new JsEngine();
        Assert.Equal(
            "v",
            eng.Evaluate(@"
                var h = new Headers();
                h.set('X-Custom', 'v');
                h.get('x-custom');
            "));
    }

    [Fact]
    public void Headers_has_returns_true_after_set()
    {
        var eng = new JsEngine();
        Assert.Equal(
            true,
            eng.Evaluate(@"
                var h = new Headers();
                h.set('k', 'v');
                h.has('K');
            "));
    }

    [Fact]
    public void Headers_append_joins_multiple_values()
    {
        var eng = new JsEngine();
        Assert.Equal(
            "a, b",
            eng.Evaluate(@"
                var h = new Headers();
                h.append('x', 'a');
                h.append('x', 'b');
                h.get('x');
            "));
    }

    [Fact]
    public void Headers_delete_removes_all_matching()
    {
        var eng = new JsEngine();
        Assert.Equal(
            false,
            eng.Evaluate(@"
                var h = new Headers();
                h.append('x', 'a');
                h.append('x', 'b');
                h.delete('x');
                h.has('x');
            "));
    }

    [Fact]
    public void Headers_is_iterable()
    {
        var eng = new JsEngine();
        Assert.Equal(
            "a:1|b:2",
            eng.Evaluate(@"
                var h = new Headers();
                h.set('a', '1');
                h.set('b', '2');
                var out = [];
                for (var p of h) out.push(p[0] + ':' + p[1]);
                out.join('|');
            "));
    }

    [Fact]
    public void Headers_forEach_visits_each_pair()
    {
        var eng = new JsEngine();
        Assert.Equal(
            "a=1,b=2",
            eng.Evaluate(@"
                var h = new Headers({ a: '1', b: '2' });
                var out = [];
                h.forEach(function (v, k) { out.push(k + '=' + v); });
                out.join(',');
            "));
    }

    // ========================================================
    // Response (synthesized)
    // ========================================================

    [Fact]
    public void Response_default_status_is_200()
    {
        var eng = new JsEngine();
        Assert.Equal(200.0, eng.Evaluate("new Response('hi').status;"));
    }

    [Fact]
    public void Response_ok_true_for_200()
    {
        var eng = new JsEngine();
        Assert.Equal(true, eng.Evaluate("new Response('hi').ok;"));
    }

    [Fact]
    public void Response_ok_false_for_404()
    {
        var eng = new JsEngine();
        Assert.Equal(
            false,
            eng.Evaluate("new Response('', { status: 404 }).ok;"));
    }

    [Fact]
    public void Response_text_returns_body_as_string()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            var log = '';
            new Response('hello').text().then(function (t) { log = t; });
        ");
        Assert.Equal("hello", eng.Globals["log"]);
    }

    [Fact]
    public void Response_json_parses_body()
    {
        var eng = new JsEngine();
        eng.RunScript(@"
            var n;
            new Response('{""count"":7}').json().then(function (j) { n = j.count; });
        ");
        Assert.Equal(7.0, eng.Globals["n"]);
    }

    // ========================================================
    // fetch with stub handler
    // ========================================================

    [Fact]
    public void Fetch_returns_resolved_promise()
    {
        var eng = EngineWithStub(200, "hi");
        eng.RunScript(@"
            var status = 0;
            fetch('https://example.com/').then(function (r) { status = r.status; });
        ");
        Assert.Equal(200.0, eng.Globals["status"]);
    }

    [Fact]
    public void Fetch_response_text_chains()
    {
        var eng = EngineWithStub(200, "hello world");
        eng.RunScript(@"
            var log = '';
            fetch('https://example.com/')
              .then(function (r) { return r.text(); })
              .then(function (t) { log = t; });
        ");
        Assert.Equal("hello world", eng.Globals["log"]);
    }

    [Fact]
    public void Fetch_response_json_chains()
    {
        var eng = EngineWithStub(200, "{\"message\":\"hi\",\"count\":3}", "application/json");
        eng.RunScript(@"
            var msg = '';
            var count = 0;
            fetch('https://example.com/api')
              .then(function (r) { return r.json(); })
              .then(function (j) { msg = j.message; count = j.count; });
        ");
        Assert.Equal("hi", eng.Globals["msg"]);
        Assert.Equal(3.0, eng.Globals["count"]);
    }

    [Fact]
    public void Fetch_response_headers_are_readable()
    {
        var eng = EngineWithStub(200, "", "text/html");
        eng.RunScript(@"
            var ct = '';
            fetch('https://example.com/').then(function (r) {
                ct = r.headers.get('content-type');
            });
        ");
        Assert.Equal("text/html", eng.Globals["ct"]);
    }

    [Fact]
    public void Fetch_honors_init_method()
    {
        var eng = EngineWithStub(200, "");
        eng.RunScript(@"
            var seenMethod = '';
            fetch('https://example.com/', { method: 'POST' })
              .then(function (r) { seenMethod = r.headers.get('x-stub-method'); });
        ");
        Assert.Equal("POST", eng.Globals["seenMethod"]);
    }

    [Fact]
    public void Fetch_error_rejects_promise()
    {
        var eng = new JsEngine();
        eng.FetchHandler = req => throw new InvalidOperationException("network down");
        eng.RunScript(@"
            var err;
            fetch('https://example.com/')
              .then(function (r) {}, function (e) { err = e.message; });
        ");
        var errMsg = eng.Globals["err"] as string;
        Assert.NotNull(errMsg);
        Assert.Contains("network down", errMsg!);
    }

    [Fact]
    public void Fetch_response_status_reflects_stub()
    {
        var eng = EngineWithStub(404, "not found");
        eng.RunScript(@"
            var status = 0;
            var okVal;
            fetch('https://example.com/missing').then(function (r) {
                status = r.status;
                okVal = r.ok;
            });
        ");
        Assert.Equal(404.0, eng.Globals["status"]);
        Assert.Equal(false, eng.Globals["okVal"]);
    }

    [Fact]
    public void Fetch_passes_url_through_to_response()
    {
        var eng = EngineWithStub(200, "");
        eng.RunScript(@"
            var url = '';
            fetch('https://example.com/path').then(function (r) { url = r.url; });
        ");
        Assert.Equal("https://example.com/path", eng.Globals["url"]);
    }

    [Fact]
    public void Fetch_response_arrayBuffer_returns_bytes()
    {
        var eng = EngineWithStub(200, "abc");
        eng.RunScript(@"
            var len = 0;
            fetch('https://example.com/')
              .then(function (r) { return r.arrayBuffer(); })
              .then(function (buf) { len = buf.byteLength; });
        ");
        Assert.Equal(3.0, eng.Globals["len"]);
    }

    [Fact]
    public void Fetch_request_object_as_input()
    {
        var eng = EngineWithStub(200, "");
        eng.RunScript(@"
            var method = '';
            var req = new Request('https://example.com/', { method: 'PUT' });
            fetch(req).then(function (r) {
                method = r.headers.get('x-stub-method');
            });
        ");
        Assert.Equal("PUT", eng.Globals["method"]);
    }

    [Fact]
    public void Fetch_null_handler_rejects()
    {
        var eng = new JsEngine();
        eng.FetchHandler = null;
        eng.RunScript(@"
            var errName = '';
            fetch('https://example.com/').then(
                function (r) {},
                function (e) { errName = e.name; });
        ");
        Assert.Equal("TypeError", eng.Globals["errName"]);
    }

    [Fact]
    public void Fetch_handler_sees_body_from_init()
    {
        var engine = new JsEngine();
        string? seenBody = null;
        engine.FetchHandler = req =>
        {
            seenBody = Encoding.UTF8.GetString(req.Body);
            return new JsFetchResponse
            {
                Status = 200,
                Url = req.Url,
                Body = Array.Empty<byte>(),
                Headers = new JsHeaders(),
            };
        };
        engine.RunScript(@"
            fetch('https://example.com/submit', {
                method: 'POST',
                body: 'hello=world'
            });
        ");
        Assert.Equal("hello=world", seenBody);
    }
}
