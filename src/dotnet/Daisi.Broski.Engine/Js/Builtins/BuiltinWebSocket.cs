using System.Net.WebSockets;
using System.Text;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>WebSocket</c> — duplex messaging via the WHATWG WebSocket
/// API. The on-wire transport is decoupled from the JS-visible
/// shape via <see cref="IWebSocketChannel"/>: production code
/// gets a <see cref="ClientWebSocketChannel"/> wrapping the BCL
/// <c>System.Net.WebSockets.ClientWebSocket</c>; tests install
/// their own channel via
/// <see cref="JsEngine.WebSocketHandler"/> for offline,
/// deterministic round-tripping.
///
/// <para>
/// Threading: the channel may fire callbacks from background
/// threads (real ClientWebSocket has its receive loop on the
/// thread pool). Every channel-side callback marshals back to
/// the engine thread via
/// <see cref="JsEventLoop.PostFromBackground"/> before touching
/// any JS-visible state — the VM is single-threaded by
/// construction.
/// </para>
///
/// <para>
/// Long-lived WebSocket usage requires the host to keep
/// calling <see cref="JsEngine.DrainEventLoop"/>; the
/// one-shot drain returns as soon as it has nothing
/// immediately runnable. Headless scrape callers typically
/// drain once per script tick, then close — the connection
/// dies cleanly when the engine disposes.
/// </para>
/// </summary>
internal static class BuiltinWebSocket
{
    private const double Connecting = 0.0;
    private const double Open = 1.0;
    private const double Closing = 2.0;
    private const double Closed = 3.0;

    public static void Install(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.Set("CONNECTING", Connecting);
        proto.Set("OPEN", Open);
        proto.Set("CLOSING", Closing);
        proto.Set("CLOSED", Closed);

        proto.SetNonEnumerable("send", new JsFunction("send", (vm, thisVal, args) =>
        {
            var ws = RequireWs(thisVal, "WebSocket.prototype.send");
            if (args.Count == 0) return JsValue.Undefined;
            ws.Send(args[0]);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("close", new JsFunction("close", (thisVal, args) =>
        {
            var ws = RequireWs(thisVal, "WebSocket.prototype.close");
            int code = args.Count > 0 && args[0] is double cd ? (int)cd : 1000;
            string reason = args.Count > 1 ? JsValue.ToJsString(args[1]) : "";
            ws.Close(code, reason);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("addEventListener", new JsFunction("addEventListener", (thisVal, args) =>
        {
            var ws = RequireWs(thisVal, "WebSocket.prototype.addEventListener");
            if (args.Count < 2 || args[1] is not JsFunction cb) return JsValue.Undefined;
            ws.AddListener(JsValue.ToJsString(args[0]), cb);
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("removeEventListener", new JsFunction("removeEventListener", (thisVal, args) =>
        {
            var ws = RequireWs(thisVal, "WebSocket.prototype.removeEventListener");
            if (args.Count < 2 || args[1] is not JsFunction cb) return JsValue.Undefined;
            ws.RemoveListener(JsValue.ToJsString(args[0]), cb);
            return JsValue.Undefined;
        }));

        var ctor = new JsFunction("WebSocket", (thisVal, args) =>
        {
            if (args.Count < 1) JsThrow.TypeError("WebSocket: url is required");
            var url = JsValue.ToJsString(args[0]);
            var protocols = ExtractProtocols(args.Count > 1 ? args[1] : null);
            var handler = engine.WebSocketHandler ?? ClientWebSocketChannel.DefaultHandler;
            var channel = handler(url, protocols);
            return new JsWebSocket(engine, url, protocols, channel) { Prototype = proto };
        });
        ctor.SetNonEnumerable("prototype", proto);
        ctor.Set("CONNECTING", Connecting);
        ctor.Set("OPEN", Open);
        ctor.Set("CLOSING", Closing);
        ctor.Set("CLOSED", Closed);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["WebSocket"] = ctor;
    }

    private static JsWebSocket RequireWs(object? thisVal, string name)
    {
        if (thisVal is not JsWebSocket ws)
        {
            JsThrow.TypeError($"{name} called on non-WebSocket");
        }
        return (JsWebSocket)thisVal!;
    }

    private static IReadOnlyList<string> ExtractProtocols(object? arg)
    {
        if (arg is null or JsUndefined or JsNull) return Array.Empty<string>();
        if (arg is string s) return new[] { s };
        if (arg is JsArray arr)
        {
            var list = new List<string>(arr.Elements.Count);
            foreach (var e in arr.Elements) list.Add(JsValue.ToJsString(e));
            return list;
        }
        return new[] { JsValue.ToJsString(arg) };
    }
}

/// <summary>
/// Pluggable transport for <see cref="JsWebSocket"/>. The
/// channel owns the connection lifecycle (connect, receive,
/// send, close) and surfaces them back via events. Tests
/// install a stub implementation for offline round-tripping;
/// production code uses <see cref="ClientWebSocketChannel"/>.
///
/// <para>
/// Implementations may invoke the events from any thread;
/// the consumer (<see cref="JsWebSocket"/>) is responsible
/// for marshalling back to the engine thread before
/// touching VM state.
/// </para>
/// </summary>
public interface IWebSocketChannel
{
    /// <summary>Connection succeeded. Negotiated subprotocol
    /// (or empty string).</summary>
    event Action<string> Opened;
    /// <summary>Text frame received.</summary>
    event Action<string> TextReceived;
    /// <summary>Binary frame received.</summary>
    event Action<byte[]> BinaryReceived;
    /// <summary>Connection closed by either party. Code +
    /// reason follow the WebSocket close-code spec.</summary>
    event Action<int, string> ClosedEvent;
    /// <summary>Transport error before / during the
    /// connection lifecycle.</summary>
    event Action<string> ErrorEvent;

    /// <summary>Begin the connection. Implementations should
    /// fire <see cref="Opened"/> on success or
    /// <see cref="ErrorEvent"/> + <see cref="ClosedEvent"/>
    /// on failure.</summary>
    void Connect();

    void SendText(string data);
    void SendBinary(byte[] data);

    /// <summary>Initiate a close handshake. Idempotent — calls
    /// after the first should no-op.</summary>
    void Close(int code, string reason);
}

/// <summary>
/// Production WebSocket transport built on
/// <see cref="System.Net.WebSockets.ClientWebSocket"/>. Receives
/// run on a thread-pool task; sends queue through a SemaphoreSlim
/// so concurrent JS-side <c>ws.send(...)</c> calls don't
/// trample each other on the wire.
/// </summary>
public sealed class ClientWebSocketChannel : IWebSocketChannel
{
    public static readonly Func<string, IReadOnlyList<string>, IWebSocketChannel> DefaultHandler =
        (url, protocols) => new ClientWebSocketChannel(url, protocols);

    private readonly string _url;
    private readonly IReadOnlyList<string> _protocols;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private bool _closed;

    public event Action<string>? Opened;
    public event Action<string>? TextReceived;
    public event Action<byte[]>? BinaryReceived;
    public event Action<int, string>? ClosedEvent;
    public event Action<string>? ErrorEvent;

    public ClientWebSocketChannel(string url, IReadOnlyList<string> protocols)
    {
        _url = url;
        _protocols = protocols;
        foreach (var p in protocols)
        {
            _socket.Options.AddSubProtocol(p);
        }
    }

    public void Connect()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await _socket.ConnectAsync(new Uri(_url), _cts.Token).ConfigureAwait(false);
                Opened?.Invoke(_socket.SubProtocol ?? "");
                await ReceiveLoopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorEvent?.Invoke(ex.Message);
                ClosedEvent?.Invoke(1006, ex.Message);
            }
        });
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var segment = new ArraySegment<byte>(buffer);
        using var ms = new MemoryStream();
        while (_socket.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await _socket.ReceiveAsync(segment, _cts.Token).ConfigureAwait(false);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                ErrorEvent?.Invoke(ex.Message);
                ClosedEvent?.Invoke(1006, ex.Message);
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                int code = result.CloseStatus is { } cs ? (int)cs : 1005;
                ClosedEvent?.Invoke(code, result.CloseStatusDescription ?? "");
                return;
            }
            if (result.MessageType == WebSocketMessageType.Text)
            {
                TextReceived?.Invoke(Encoding.UTF8.GetString(ms.ToArray()));
            }
            else
            {
                BinaryReceived?.Invoke(ms.ToArray());
            }
        }
    }

    public void SendText(string data) =>
        Send(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(data));

    public void SendBinary(byte[] data) =>
        Send(WebSocketMessageType.Binary, data);

    private void Send(WebSocketMessageType type, byte[] payload)
    {
        if (_socket.State != WebSocketState.Open) return;
        _ = Task.Run(async () =>
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(
                    new ArraySegment<byte>(payload), type, true, _cts!.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorEvent?.Invoke(ex.Message);
            }
            finally
            {
                _sendLock.Release();
            }
        });
    }

    public void Close(int code, string reason)
    {
        if (_closed) return;
        _closed = true;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(
                        (WebSocketCloseStatus)code, reason, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch { /* best effort */ }
            finally
            {
                _cts?.Cancel();
                ClosedEvent?.Invoke(code, reason);
            }
        });
    }
}

/// <summary>
/// Instance state for a JS <c>WebSocket</c>. Wires the
/// underlying <see cref="IWebSocketChannel"/> events through
/// <see cref="JsEventLoop.PostFromBackground"/> so callbacks
/// run on the engine thread, then dispatches them to the
/// script-installed handlers.
/// </summary>
public sealed class JsWebSocket : JsObject
{
    private readonly JsEngine _engine;
    private readonly IWebSocketChannel _channel;
    private readonly Dictionary<string, List<JsFunction>> _listeners = new();
    private readonly string _url;
    private readonly IReadOnlyList<string> _protocols;
    private double _readyState = 0.0;
    private string _subProtocol = "";
    private string _binaryType = "blob";

    public JsWebSocket(
        JsEngine engine, string url,
        IReadOnlyList<string> protocols, IWebSocketChannel channel)
    {
        _engine = engine;
        _channel = channel;
        _url = url;
        _protocols = protocols;
        _channel.Opened += OnOpenedFromAnyThread;
        _channel.TextReceived += OnTextFromAnyThread;
        _channel.BinaryReceived += OnBinaryFromAnyThread;
        _channel.ClosedEvent += OnClosedFromAnyThread;
        _channel.ErrorEvent += OnErrorFromAnyThread;
        _channel.Connect();
    }

    /// <inheritdoc />
    public override object? Get(string key) => key switch
    {
        "readyState" => _readyState,
        "url" => _url,
        "protocol" => _subProtocol,
        "binaryType" => _binaryType,
        "bufferedAmount" => 0.0,
        "extensions" => "",
        _ => base.Get(key),
    };

    /// <inheritdoc />
    public override bool Has(string key) =>
        key is "readyState" or "url" or "protocol" or "binaryType"
            or "bufferedAmount" or "extensions"
        || base.Has(key);

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        if (key == "binaryType")
        {
            // Only "blob" and "arraybuffer" are spec-valid.
            // Anything else is a no-op per spec.
            var v = JsValue.ToJsString(value);
            if (v is "blob" or "arraybuffer") _binaryType = v;
            return;
        }
        base.Set(key, value);
    }

    public void Send(object? data)
    {
        if (_readyState == 0.0)
        {
            JsThrow.Raise(MakeError("InvalidStateError",
                "WebSocket: still in CONNECTING state"));
        }
        if (_readyState != 1.0) return; // CLOSING / CLOSED — drop on the floor

        switch (data)
        {
            case string s:
                _channel.SendText(s);
                return;
            case JsArrayBuffer ab:
                {
                    var copy = new byte[ab.Data.Length];
                    Array.Copy(ab.Data, copy, ab.Data.Length);
                    _channel.SendBinary(copy);
                    return;
                }
            case JsTypedArray ta:
                {
                    var copy = new byte[ta.ByteLength];
                    Array.Copy(ta.Buffer.Data, ta.ByteOffset, copy, 0, ta.ByteLength);
                    _channel.SendBinary(copy);
                    return;
                }
            case JsBlob blob:
                {
                    var copy = new byte[blob.Data.Length];
                    Array.Copy(blob.Data, copy, blob.Data.Length);
                    _channel.SendBinary(copy);
                    return;
                }
            default:
                _channel.SendText(JsValue.ToJsString(data));
                return;
        }
    }

    public void Close(int code, string reason)
    {
        if (_readyState == 2.0 || _readyState == 3.0) return;
        _readyState = 2.0; // CLOSING
        _channel.Close(code, reason);
    }

    public void AddListener(string type, JsFunction cb)
    {
        if (!_listeners.TryGetValue(type, out var list))
        {
            list = new List<JsFunction>();
            _listeners[type] = list;
        }
        list.Add(cb);
    }

    public void RemoveListener(string type, JsFunction cb)
    {
        if (_listeners.TryGetValue(type, out var list)) list.Remove(cb);
    }

    // -------------------------------------------------------------
    // Channel callbacks — may arrive on a background thread. Marshal
    // back through PostFromBackground so the actual VM work runs on
    // the engine thread.
    // -------------------------------------------------------------

    private void OnOpenedFromAnyThread(string subProtocol) =>
        _engine.EventLoop.PostFromBackground(() =>
        {
            _subProtocol = subProtocol;
            _readyState = 1.0; // OPEN
            Fire("open", BuildEvent("open"));
        });

    private void OnTextFromAnyThread(string text) =>
        _engine.EventLoop.PostFromBackground(() =>
        {
            var evt = BuildEvent("message");
            evt.Set("data", text);
            evt.Set("origin", _url);
            evt.Set("lastEventId", "");
            Fire("message", evt);
        });

    private void OnBinaryFromAnyThread(byte[] data) =>
        _engine.EventLoop.PostFromBackground(() =>
        {
            var evt = BuildEvent("message");
            evt.Set("data", DecodeBinary(data));
            evt.Set("origin", _url);
            evt.Set("lastEventId", "");
            Fire("message", evt);
        });

    private void OnClosedFromAnyThread(int code, string reason) =>
        _engine.EventLoop.PostFromBackground(() =>
        {
            _readyState = 3.0; // CLOSED
            var evt = BuildEvent("close");
            evt.Set("code", (double)code);
            evt.Set("reason", reason);
            evt.Set("wasClean", code == 1000);
            Fire("close", evt);
        });

    private void OnErrorFromAnyThread(string message) =>
        _engine.EventLoop.PostFromBackground(() =>
        {
            var evt = BuildEvent("error");
            evt.Set("message", message);
            Fire("error", evt);
        });

    private object DecodeBinary(byte[] data)
    {
        if (_binaryType == "arraybuffer")
        {
            var ab = new JsArrayBuffer(data.Length);
            Array.Copy(data, ab.Data, data.Length);
            return ab;
        }
        // Default "blob" — use the engine's installed Blob
        // prototype so the script-visible result is `instanceof
        // Blob`.
        var blobProto = (_engine.Globals["Blob"] as JsFunction)?.Get("prototype") as JsObject;
        return new JsBlob(data, "") { Prototype = blobProto };
    }

    private JsObject BuildEvent(string type)
    {
        var evt = new JsObject { Prototype = _engine.ObjectPrototype };
        evt.Set("type", type);
        evt.Set("target", this);
        evt.Set("currentTarget", this);
        evt.Set("bubbles", false);
        return evt;
    }

    private void Fire(string type, JsObject evt)
    {
        if (base.Get("on" + type) is JsFunction onCb)
        {
            _engine.Vm.InvokeJsFunction(onCb, this, new object?[] { evt });
        }
        if (_listeners.TryGetValue(type, out var list))
        {
            foreach (var cb in list.ToArray())
            {
                _engine.Vm.InvokeJsFunction(cb, this, new object?[] { evt });
            }
        }
    }

    private JsObject MakeError(string name, string message)
    {
        var err = new JsObject { Prototype = _engine.ErrorPrototype };
        err.Set("name", name);
        err.Set("message", message);
        return err;
    }
}
