using Daisi.Broski.Engine.Net;
using Daisi.Broski.Skimmer;
using SkimmerApi = Daisi.Broski.Skimmer.Skimmer;

namespace Daisi.Broski.Web.Services;

/// <summary>
/// Thin wrapper around <see cref="SkimmerApi.SkimAsync"/> so
/// both the HTTP API (<c>/api/v1/skim</c>) and the admin
/// preview UI share a single entry point. Keeps the Skimmer's
/// public surface from leaking into every consumer.
/// </summary>
public sealed class SkimService
{
    /// <summary>Skim <paramref name="url"/> end-to-end. Returns a
    /// fully-populated <see cref="ArticleContent"/> the caller
    /// can render through <see cref="JsonFormatter"/> or
    /// <see cref="MarkdownFormatter"/>.</summary>
    public Task<ArticleContent> SkimAsync(
        Uri url, bool scripting = true, CancellationToken ct = default)
    {
        return SkimmerApi.SkimAsync(url, new SkimmerOptions
        {
            ScriptingEnabled = scripting,
            Fetcher = new HttpFetcherOptions(),
        }, ct);
    }
}
