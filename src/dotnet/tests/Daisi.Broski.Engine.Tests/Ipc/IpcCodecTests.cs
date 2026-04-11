using System.Buffers.Binary;
using Daisi.Broski.Ipc;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Ipc;

public class IpcCodecTests
{
    // -------- round-trip --------

    [Fact]
    public async Task Request_round_trips_through_a_memory_stream()
    {
        var outbound = IpcMessage.Request(
            id: 42,
            method: Methods.Navigate,
            @params: new NavigateRequest { Url = "https://example.com/", MaxRedirects = 10 });

        var stream = new MemoryStream();
        await IpcCodec.WriteAsync(stream, outbound, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var inbound = await IpcCodec.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.NotNull(inbound);
        Assert.Equal(MessageKind.Request, inbound!.Kind);
        Assert.Equal(42, inbound.Id);
        Assert.Equal(Methods.Navigate, inbound.Method);

        var payload = inbound.ParamsAs<NavigateRequest>();
        Assert.NotNull(payload);
        Assert.Equal("https://example.com/", payload!.Url);
        Assert.Equal(10, payload.MaxRedirects);
    }

    [Fact]
    public async Task Response_with_result_round_trips()
    {
        var outbound = IpcMessage.Response(
            id: 7,
            result: new NavigateResponse
            {
                FinalUrl = "https://example.com/final",
                Status = 200,
                Encoding = "utf-8",
                RedirectChain = ["https://example.com/", "https://example.com/final"],
                ByteCount = 1234,
                Title = "hello",
            });

        var (_, inbound) = await RoundTrip(outbound);

        Assert.Equal(MessageKind.Response, inbound.Kind);
        Assert.Equal(7, inbound.Id);
        Assert.Null(inbound.Error);

        var payload = inbound.ResultAs<NavigateResponse>();
        Assert.NotNull(payload);
        Assert.Equal(200, payload!.Status);
        Assert.Equal(2, payload.RedirectChain.Count);
        Assert.Equal("hello", payload.Title);
    }

    [Fact]
    public async Task Response_with_error_round_trips()
    {
        var outbound = IpcMessage.ResponseError(id: 99, code: "bad_url", message: "not absolute");

        var (_, inbound) = await RoundTrip(outbound);

        Assert.Equal(MessageKind.Response, inbound.Kind);
        Assert.Equal(99, inbound.Id);
        Assert.NotNull(inbound.Error);
        Assert.Equal("bad_url", inbound.Error!.Code);
        Assert.Equal("not absolute", inbound.Error.Message);
    }

    [Fact]
    public async Task Notification_round_trips_with_zero_id()
    {
        var outbound = IpcMessage.Notification(
            Methods.NavigationCompleted,
            new NavigationCompletedNotification { FinalUrl = "https://x/", Status = 200 });

        var (_, inbound) = await RoundTrip(outbound);

        Assert.Equal(MessageKind.Notification, inbound.Kind);
        Assert.Equal(0, inbound.Id);
        Assert.Equal(Methods.NavigationCompleted, inbound.Method);
        Assert.Null(inbound.Error);
    }

    [Fact]
    public async Task Multiple_frames_in_a_stream_are_read_in_order()
    {
        var stream = new MemoryStream();
        var ct = TestContext.Current.CancellationToken;
        await IpcCodec.WriteAsync(stream, IpcMessage.Request(1, "a", null), ct);
        await IpcCodec.WriteAsync(stream, IpcMessage.Request(2, "b", null), ct);
        await IpcCodec.WriteAsync(stream, IpcMessage.Request(3, "c", null), ct);

        stream.Position = 0;
        var first = await IpcCodec.ReadAsync(stream, ct);
        var second = await IpcCodec.ReadAsync(stream, ct);
        var third = await IpcCodec.ReadAsync(stream, ct);
        var eof = await IpcCodec.ReadAsync(stream, ct);

        Assert.Equal(1, first!.Id);
        Assert.Equal(2, second!.Id);
        Assert.Equal(3, third!.Id);
        Assert.Null(eof); // clean EOF
    }

    // -------- DTO round-trips --------

    [Fact]
    public async Task QueryAllResponse_with_serialized_elements_round_trips()
    {
        var outbound = IpcMessage.Response(
            id: 1,
            result: new QueryAllResponse
            {
                Matches =
                [
                    new SerializedElement
                    {
                        TagName = "a",
                        Attributes = new Dictionary<string, string>
                        {
                            ["href"] = "/x",
                            ["class"] = "storylink",
                        },
                        TextContent = "First",
                    },
                    new SerializedElement
                    {
                        TagName = "a",
                        Attributes = new Dictionary<string, string> { ["href"] = "/y" },
                        TextContent = "Second",
                    },
                ],
            });

        var (_, inbound) = await RoundTrip(outbound);
        var payload = inbound.ResultAs<QueryAllResponse>();

        Assert.NotNull(payload);
        Assert.Equal(2, payload!.Matches.Count);
        Assert.Equal("a", payload.Matches[0].TagName);
        Assert.Equal("/x", payload.Matches[0].Attributes["href"]);
        Assert.Equal("First", payload.Matches[0].TextContent);
        Assert.Equal("storylink", payload.Matches[0].Attributes["class"]);
    }

    // -------- framing + malformed input --------

    [Fact]
    public async Task ReadAsync_returns_null_on_empty_stream_at_frame_boundary()
    {
        var stream = new MemoryStream();
        var inbound = await IpcCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Null(inbound);
    }

    [Fact]
    public async Task ReadAsync_throws_on_oversize_length_prefix()
    {
        // Craft a frame header claiming a body just over the cap.
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)IpcCodec.MaxMessageBytes + 1);
        stream.Write(header, 0, 4);
        stream.Position = 0;

        await Assert.ThrowsAsync<IpcProtocolException>(() =>
            IpcCodec.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_throws_on_truncated_header()
    {
        var stream = new MemoryStream(new byte[] { 0x00, 0x00 }); // only 2 bytes, need 4
        await Assert.ThrowsAsync<IpcProtocolException>(() =>
            IpcCodec.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_throws_on_truncated_body()
    {
        // Header claims 100 bytes, only 10 actually present.
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, 100);
        stream.Write(header, 0, 4);
        stream.Write(new byte[10], 0, 10);
        stream.Position = 0;

        await Assert.ThrowsAsync<IpcProtocolException>(() =>
            IpcCodec.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_throws_on_malformed_json_body()
    {
        var stream = new MemoryStream();
        var body = "{{{ not json"u8.ToArray();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
        stream.Write(header, 0, 4);
        stream.Write(body, 0, body.Length);
        stream.Position = 0;

        await Assert.ThrowsAsync<IpcProtocolException>(() =>
            IpcCodec.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_throws_if_serialized_message_exceeds_cap()
    {
        // Synthesize a message with a giant inline string so the serialized
        // form overflows the cap. This is a sanity check — we don't want to
        // silently truncate or let an outbound runaway eat the pipe.
        var big = new string('x', IpcCodec.MaxMessageBytes);
        var outbound = IpcMessage.Notification("huge", new { data = big });

        var stream = new MemoryStream();
        await Assert.ThrowsAsync<IpcProtocolException>(() =>
            IpcCodec.WriteAsync(stream, outbound, TestContext.Current.CancellationToken));
    }

    // -------- helper --------

    private static async Task<(byte[] bytes, IpcMessage inbound)> RoundTrip(IpcMessage outbound)
    {
        var stream = new MemoryStream();
        await IpcCodec.WriteAsync(stream, outbound, TestContext.Current.CancellationToken);
        var bytes = stream.ToArray();
        stream.Position = 0;
        var inbound = await IpcCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.NotNull(inbound);
        return (bytes, inbound!);
    }
}
