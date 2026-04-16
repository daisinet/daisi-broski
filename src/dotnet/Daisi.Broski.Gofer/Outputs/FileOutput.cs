using System.Text.Json;

namespace Daisi.Broski.Gofer.Outputs;

/// <summary>
/// Streams results to a file as newline-delimited JSON (JSONL).
/// JSONL is the shape every LLM-prep pipeline wants: one record
/// per line, append-friendly, streaming-parseable, trivially
/// chunkable. Writes are serialized on a single async lock so
/// concurrent workers can't interleave bytes mid-record.
/// </summary>
public sealed class FileOutput : IGoferOutput
{
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json;

    public FileOutput(string path)
        : this(path, append: false) { }

    public FileOutput(string path, bool append)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _stream = new FileStream(
            path,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024);
        _writer = new StreamWriter(_stream) { AutoFlush = false };
        _json = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public async Task WriteAsync(GoferResult result, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(result, _json);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await _writer.FlushAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
