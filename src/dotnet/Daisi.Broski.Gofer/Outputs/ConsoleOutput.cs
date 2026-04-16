namespace Daisi.Broski.Gofer.Outputs;

/// <summary>
/// Writes each result to <see cref="Console.Out"/> as a short
/// header line (URL · status · depth · duration) followed by the
/// extracted markdown and a trailing separator. Locked internally
/// so concurrent workers don't interleave mid-page.
/// </summary>
public sealed class ConsoleOutput : IGoferOutput
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    public ConsoleOutput() : this(Console.Out) { }
    public ConsoleOutput(TextWriter writer) { _writer = writer; }

    public Task WriteAsync(GoferResult result, CancellationToken ct)
    {
        lock (_gate)
        {
            _writer.WriteLine($"━━ [{result.Status}] {result.Url} · depth={result.Depth} · {result.DurationMs}ms");
            if (!string.IsNullOrEmpty(result.Title))
                _writer.WriteLine($"   title: {result.Title}");
            if (!string.IsNullOrEmpty(result.Error))
                _writer.WriteLine($"   error: {result.Error}");
            _writer.WriteLine();
            _writer.WriteLine(result.Markdown);
            _writer.WriteLine();
        }
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct)
    {
        lock (_gate) _writer.Flush();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
