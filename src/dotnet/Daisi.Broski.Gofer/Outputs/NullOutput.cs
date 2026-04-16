namespace Daisi.Broski.Gofer.Outputs;

/// <summary>
/// Discards every result. The default when no sink is configured —
/// consumers who wire only the <c>PageScraped</c> event don't need
/// a sink, so the crawler can still run cleanly without one.
/// </summary>
public sealed class NullOutput : IGoferOutput
{
    public static readonly NullOutput Instance = new();
    public Task WriteAsync(GoferResult result, CancellationToken ct) => Task.CompletedTask;
    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
