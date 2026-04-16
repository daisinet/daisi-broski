namespace Daisi.Broski.Gofer;

/// <summary>
/// Receives crawl results as they're produced. Implementations
/// must be thread-safe: the crawler writes from N workers
/// concurrently. See <see cref="Outputs.FileOutput"/>,
/// <see cref="Outputs.ConsoleOutput"/>, and
/// <see cref="Outputs.NullOutput"/> for the built-in sinks.
/// </summary>
public interface IGoferOutput : IAsyncDisposable
{
    Task WriteAsync(GoferResult result, CancellationToken ct);

    /// <summary>Flush buffered output to its destination. Called
    /// once when the crawl finishes; a no-op for sinks that don't
    /// buffer.</summary>
    Task FlushAsync(CancellationToken ct);
}
