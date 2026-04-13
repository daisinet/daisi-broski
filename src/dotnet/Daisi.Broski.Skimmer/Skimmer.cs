using Daisi.Broski.Engine.Net;
// Aliased import — the namespace `Daisi.Broski` collides with this
// project's parent namespace, so referencing the engine's facade
// type as `Engine.Broski` would be ambiguous. The alias keeps
// callsites short while pointing at the right type.
using EngineBroski = Daisi.Broski.Engine.Broski;
using EngineBroskiOptions = Daisi.Broski.Engine.BroskiOptions;

namespace Daisi.Broski.Skimmer;

/// <summary>
/// One-call high-level entry point for the Skimmer: fetch + parse
/// + (optionally) run scripts + extract main content. Library
/// consumers that don't want to wire up <see cref="Broski.LoadAsync"/>
/// + <see cref="ContentExtractor.Extract"/> themselves go through
/// here.
///
/// <code>
///   var article = await Skimmer.SkimAsync(
///       new Uri("https://daisi.ai/learn"),
///       new SkimmerOptions { ScriptingEnabled = true });
///   Console.WriteLine(article.Title);
///   Console.WriteLine(MarkdownFormatter.Format(article));
/// </code>
/// </summary>
public static class Skimmer
{
    /// <summary>Skim <paramref name="url"/> end-to-end. Returns a
    /// fully-populated <see cref="ArticleContent"/>; the underlying
    /// page can be re-rendered through any of the formatters
    /// (<see cref="JsonFormatter"/>, <see cref="MarkdownFormatter"/>,
    /// <see cref="HtmlFormatter"/>) without re-fetching.</summary>
    public static async Task<ArticleContent> SkimAsync(
        Uri url,
        SkimmerOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        options ??= new SkimmerOptions();

        var page = await EngineBroski.LoadAsync(
            url,
            new EngineBroskiOptions
            {
                ScriptingEnabled = options.ScriptingEnabled,
                Fetcher = options.Fetcher,
            },
            ct).ConfigureAwait(false);

        return ContentExtractor.Extract(page.Document, page.FinalUrl);
    }
}

/// <summary>Configuration for <see cref="Skimmer.SkimAsync"/>.
/// Mirrors <see cref="BroskiOptions"/> with the same defaults — the
/// extra layer exists so future Skimmer-only knobs (heuristic
/// thresholds, formatter choice, etc.) have a place to land
/// without polluting the engine's option surface.</summary>
public sealed class SkimmerOptions
{
    /// <summary>When <c>true</c> (default), run the page's scripts
    /// before extracting. Required for SPAs (Next.js, Nuxt, the
    /// daisi.ai stack) where the interesting content is rendered
    /// client-side. Set to <c>false</c> for static-HTML pages
    /// where the SSR shell already has everything you want — the
    /// skim is then both faster and more deterministic.</summary>
    public bool ScriptingEnabled { get; init; } = true;

    /// <summary>HTTP fetcher options forwarded to
    /// <see cref="BroskiOptions.Fetcher"/>.</summary>
    public HttpFetcherOptions? Fetcher { get; init; }
}
