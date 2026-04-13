using System.Text;
using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

/// <summary>
/// Phase 5e — <c>WebSocket</c>. Tests install a stub
/// <see cref="IWebSocketChannel"/> (the
/// <see cref="StubChannel"/> below) so the suite stays
/// offline. Each test drives the channel directly to simulate
/// open / message / close / error and asserts the JS-side
/// observable behavior.
/// </summary>
public class JsWebSocketTests
{
    /// <summary>In-memory channel that lets tests fire transport
    /// events on demand and inspect what the JS-side sent.</summary>
    private sealed class StubChannel : IWebSocketChannel
    {
        public List<string> SentText { get; } = new();
        public List<byte[]> SentBinary { get; } = new();
        public bool Connected { get; private set; }
        public int? CloseCode { get; private set; }
        public string? CloseReason { get; private set; }

        public event Action<string>? Opened;
        public event Action<string>? TextReceived;
        public event Action<byte[]>? BinaryReceived;
        public event Action<int, string>? ClosedEvent;
        public event Action<string>? ErrorEvent;

        public void Connect() { Connected = true; }
        public void SendText(string data) => SentText.Add(data);
        public void SendBinary(byte[] data) => SentBinary.Add(data);

        public void Close(int code, string reason)
        {
            CloseCode = code;
            CloseReason = reason;
        }

        // Test helpers — fire the events as if the wire produced them.
        public void FireOpen(string subProtocol = "") => Opened?.Invoke(subProtocol);
        public void FireText(string data) => TextReceived?.Invoke(data);
        public void FireBinary(byte[] data) => BinaryReceived?.Invoke(data);
        public void FireClose(int code, string reason) => ClosedEvent?.Invoke(code, reason);
        public void FireError(string message) => ErrorEvent?.Invoke(message);
    }

    private static (JsEngine engine, StubChannel channel) NewEngine()
    {
        var stub = new StubChannel();
        var eng = new JsEngine();
        eng.WebSocketHandler = (url, protocols) => stub;
        return (eng, stub);
    }

    [Fact]
    public void Constructor_starts_in_CONNECTING_state()
    {
        var (eng, _) = NewEngine();
        eng.Evaluate(@"
            var ws = new WebSocket('ws://example.com');
        ");
        Assert.Equal(0.0, eng.Evaluate("ws.readyState;"));
        Assert.Equal("ws://example.com", eng.Evaluate("ws.url;"));
    }

    [Fact]
    public void Open_event_fires_after_channel_connects()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var opened = false;
            var ws = new WebSocket('ws://example.com');
            ws.onopen = function () { opened = true; };
        ");
        stub.FireOpen();
        eng.DrainEventLoop();
        Assert.Equal(true, eng.Evaluate("opened;"));
        Assert.Equal(1.0, eng.Evaluate("ws.readyState;"));
    }

    [Fact]
    public void Subprotocol_propagates_through_open()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var ws = new WebSocket('ws://example.com', ['chat', 'v1']);
        ");
        stub.FireOpen("chat");
        eng.DrainEventLoop();
        Assert.Equal("chat", eng.Evaluate("ws.protocol;"));
    }

    [Fact]
    public void Send_in_CONNECTING_state_throws()
    {
        var (eng, _) = NewEngine();
        Assert.Equal(
            "InvalidStateError",
            eng.Evaluate(@"
                var ws = new WebSocket('ws://example.com');
                var name;
                try { ws.send('nope'); } catch (e) { name = e.name; }
                name;
            "));
    }

    [Fact]
    public void Send_string_after_open_reaches_the_channel()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"var ws = new WebSocket('ws://example.com');");
        stub.FireOpen();
        eng.DrainEventLoop();
        eng.Evaluate("ws.send('hello');");
        Assert.Equal(new[] { "hello" }, stub.SentText.ToArray());
    }

    [Fact]
    public void Send_arraybuffer_routes_through_binary_path()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"var ws = new WebSocket('ws://example.com');");
        stub.FireOpen();
        eng.DrainEventLoop();
        eng.Evaluate(@"
            var buf = new ArrayBuffer(3);
            var view = new Uint8Array(buf);
            view[0] = 1; view[1] = 2; view[2] = 3;
            ws.send(buf);
        ");
        Assert.Single(stub.SentBinary);
        Assert.Equal(new byte[] { 1, 2, 3 }, stub.SentBinary[0]);
    }

    [Fact]
    public void Send_blob_routes_through_binary_path()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"var ws = new WebSocket('ws://example.com');");
        stub.FireOpen();
        eng.DrainEventLoop();
        eng.Evaluate(@"
            var b = new Blob(['ab']);
            ws.send(b);
        ");
        Assert.Single(stub.SentBinary);
        Assert.Equal(new byte[] { 0x61, 0x62 }, stub.SentBinary[0]);
    }

    [Fact]
    public void Inbound_text_message_fires_message_event()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var got;
            var ws = new WebSocket('ws://example.com');
            ws.onmessage = function (evt) { got = evt.data; };
        ");
        stub.FireOpen();
        stub.FireText("ping");
        eng.DrainEventLoop();
        Assert.Equal("ping", eng.Evaluate("got;"));
    }

    [Fact]
    public void Inbound_binary_message_with_blob_binaryType()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var got;
            var ws = new WebSocket('ws://example.com');
            ws.binaryType = 'blob';
            ws.onmessage = function (evt) { got = evt.data; };
        ");
        stub.FireOpen();
        stub.FireBinary(Encoding.UTF8.GetBytes("hi"));
        eng.DrainEventLoop();
        Assert.Equal(JsValue.True, eng.Evaluate("got instanceof Blob;"));
        Assert.Equal(2.0, eng.Evaluate("got.size;"));
    }

    [Fact]
    public void Inbound_binary_message_with_arraybuffer_binaryType()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var got;
            var ws = new WebSocket('ws://example.com');
            ws.binaryType = 'arraybuffer';
            ws.onmessage = function (evt) {
                var view = new Uint8Array(evt.data);
                got = view[0] + ',' + view[1];
            };
        ");
        stub.FireOpen();
        stub.FireBinary(new byte[] { 9, 11 });
        eng.DrainEventLoop();
        Assert.Equal("9,11", eng.Evaluate("got;"));
    }

    [Fact]
    public void Close_event_carries_code_and_reason()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var info;
            var ws = new WebSocket('ws://example.com');
            ws.onclose = function (evt) {
                info = evt.code + ':' + evt.reason + ':' + evt.wasClean;
            };
        ");
        stub.FireOpen();
        stub.FireClose(1000, "bye");
        eng.DrainEventLoop();
        Assert.Equal("1000:bye:true", eng.Evaluate("info;"));
        Assert.Equal(3.0, eng.Evaluate("ws.readyState;"));
    }

    [Fact]
    public void Error_event_fires_with_message()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var msg;
            var ws = new WebSocket('ws://example.com');
            ws.onerror = function (evt) { msg = evt.message; };
        ");
        stub.FireError("connection refused");
        eng.DrainEventLoop();
        Assert.Equal("connection refused", eng.Evaluate("msg;"));
    }

    [Fact]
    public void Script_initiated_close_propagates_to_channel()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"var ws = new WebSocket('ws://example.com');");
        stub.FireOpen();
        eng.DrainEventLoop();
        eng.Evaluate("ws.close(1001, 'going away');");
        Assert.Equal(1001, stub.CloseCode);
        Assert.Equal("going away", stub.CloseReason);
        Assert.Equal(2.0, eng.Evaluate("ws.readyState;"));
    }

    [Fact]
    public void Close_default_code_is_1000()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var ws = new WebSocket('ws://example.com');
            ws.close();
        ");
        Assert.Equal(1000, stub.CloseCode);
    }

    [Fact]
    public void AddEventListener_works_alongside_on_handlers()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var seen = [];
            var ws = new WebSocket('ws://example.com');
            ws.onmessage = function () { seen.push('on'); };
            ws.addEventListener('message', function () { seen.push('listener'); });
        ");
        stub.FireOpen();
        stub.FireText("hi");
        eng.DrainEventLoop();
        Assert.Equal("on,listener", eng.Evaluate("seen.join(',');"));
    }

    [Fact]
    public void RemoveEventListener_unregisters()
    {
        var (eng, stub) = NewEngine();
        eng.Evaluate(@"
            var count = 0;
            var ws = new WebSocket('ws://example.com');
            var cb = function () { count++; };
            ws.addEventListener('message', cb);
            ws.removeEventListener('message', cb);
        ");
        stub.FireOpen();
        stub.FireText("hi");
        eng.DrainEventLoop();
        Assert.Equal(0.0, eng.Evaluate("count;"));
    }

    [Fact]
    public void State_constants_exposed_on_constructor_and_instance()
    {
        var (eng, _) = NewEngine();
        eng.Evaluate(@"var ws = new WebSocket('ws://example.com');");
        Assert.Equal(0.0, eng.Evaluate("WebSocket.CONNECTING;"));
        Assert.Equal(1.0, eng.Evaluate("WebSocket.OPEN;"));
        Assert.Equal(2.0, eng.Evaluate("WebSocket.CLOSING;"));
        Assert.Equal(3.0, eng.Evaluate("WebSocket.CLOSED;"));
        Assert.Equal(0.0, eng.Evaluate("ws.CONNECTING;"));
        Assert.Equal(1.0, eng.Evaluate("ws.OPEN;"));
    }
}
