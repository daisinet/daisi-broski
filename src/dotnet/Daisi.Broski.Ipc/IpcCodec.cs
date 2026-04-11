using System.Buffers.Binary;
using System.Text.Json;

namespace Daisi.Broski.Ipc;

/// <summary>
/// Reads and writes <see cref="IpcMessage"/> instances over a
/// <see cref="Stream"/> using a length-prefixed UTF-8 JSON wire format.
///
/// Frame layout:
///
/// <code>
///   ┌────────────┬──────────────────────────────────────┐
///   │ u32 length │ UTF-8 JSON body (length bytes)       │
///   └────────────┴──────────────────────────────────────┘
/// </code>
///
/// The length prefix is big-endian because big-endian is the default
/// for every network protocol we'd ever want to interop with, and
/// <see cref="BinaryPrimitives"/> has symmetric helpers for both.
///
/// Max frame size is enforced at <see cref="MaxMessageBytes"/> (64 MiB)
/// — larger messages are rejected with <see cref="IpcProtocolException"/>
/// rather than allocated. This is important because the sandbox child
/// is the thing on the other end of this pipe, and we do not trust its
/// output to be well-formed when the Job Object budget starts biting.
/// </summary>
public static class IpcCodec
{
    /// <summary>Maximum allowed message body size in bytes. 64 MiB —
    /// generous enough for serialized HTML documents and DOM snapshots,
    /// small enough to reject runaway allocations.</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    /// <summary>Write one message frame to <paramref name="stream"/>.
    /// Flushes the stream before returning so the recipient can see
    /// the full frame immediately (important over pipes where the
    /// kernel may buffer partial writes otherwise).</summary>
    public static async Task WriteAsync(
        Stream stream, IpcMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (body.Length > MaxMessageBytes)
        {
            throw new IpcProtocolException(
                $"Outgoing message is {body.Length} bytes, exceeds cap of {MaxMessageBytes}.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Read one message frame from <paramref name="stream"/>.
    /// Returns <c>null</c> if the stream is cleanly at EOF before the
    /// next frame starts (the peer closed the pipe). Throws
    /// <see cref="IpcProtocolException"/> for malformed or oversize
    /// frames.</summary>
    public static async Task<IpcMessage?> ReadAsync(
        Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[4];
        int headerRead = await ReadAtLeastAsync(stream, header, allowZero: true, ct)
            .ConfigureAwait(false);
        if (headerRead == 0) return null; // clean EOF at frame boundary
        if (headerRead != 4)
        {
            throw new IpcProtocolException(
                $"Truncated frame header ({headerRead}/4 bytes).");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxMessageBytes)
        {
            throw new IpcProtocolException(
                $"Incoming message is {length} bytes, exceeds cap of {MaxMessageBytes}.");
        }

        var body = new byte[length];
        int bodyRead = await ReadAtLeastAsync(stream, body, allowZero: false, ct)
            .ConfigureAwait(false);
        if (bodyRead != length)
        {
            throw new IpcProtocolException(
                $"Truncated frame body ({bodyRead}/{length} bytes).");
        }

        try
        {
            return JsonSerializer.Deserialize<IpcMessage>(body)
                ?? throw new IpcProtocolException("Message deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new IpcProtocolException($"Malformed message JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Fill <paramref name="buffer"/> from <paramref name="stream"/>.
    /// Returns 0 if the first read returns 0 and <paramref name="allowZero"/>
    /// is true — caller interprets that as clean EOF. Returns a
    /// partial-byte count if the stream ended mid-buffer; caller
    /// decides whether that's an error.
    /// </summary>
    private static async Task<int> ReadAtLeastAsync(
        Stream stream, byte[] buffer, bool allowZero, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (n == 0)
            {
                if (total == 0 && allowZero) return 0;
                return total;
            }
            total += n;
        }
        return total;
    }
}

/// <summary>Thrown when the IPC stream produces an un-parseable or
/// over-sized frame. Callers should consider the stream unusable and
/// tear down the sandbox.</summary>
public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException(string message) : base(message) { }
    public IpcProtocolException(string message, Exception inner) : base(message, inner) { }
}
