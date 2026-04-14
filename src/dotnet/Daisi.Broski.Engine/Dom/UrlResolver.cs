namespace Daisi.Broski.Engine.Dom;

/// <summary>
/// Single source of truth for resolving a relative URL in
/// document context. Honors the
/// HTML spec's <c>&lt;base href&gt;</c> element — a feature
/// Blazor apps rely on (they emit <c>&lt;base href="/"&gt;</c>
/// so the importmap + relative script srcs resolve
/// against the site root rather than the current page's
/// directory). Without this, a Blazor app loaded at
/// <c>/account/register</c> fetches its
/// <c>_framework/blazor.web.*.js</c> from
/// <c>/account/_framework/…</c>, which doesn't exist —
/// the server returns the SPA shell HTML and every JS
/// parser on the page dies with "Unexpected token
/// LessThan".
///
/// <para>
/// Resolution order per HTML living-standard §4.2.4:
/// </para>
/// <list type="number">
/// <item>If there's a <c>&lt;base href&gt;</c> in the
///   document, its <c>href</c> value is resolved against
///   the document URL to form the effective base.</item>
/// <item>All other relative URLs in the document resolve
///   against that effective base.</item>
/// </list>
/// </summary>
public static class UrlResolver
{
    /// <summary>Resolve <paramref name="rawHref"/> against
    /// the document's effective base URL (the
    /// <c>&lt;base href&gt;</c> element's value combined
    /// with <paramref name="pageUrl"/>, or just
    /// <paramref name="pageUrl"/> when no <c>&lt;base&gt;</c>
    /// is present). Returns <c>null</c> when the resolution
    /// fails — callers should treat that the same way
    /// they'd treat a missing attribute.</summary>
    public static Uri? Resolve(Document? document, Uri pageUrl, string? rawHref)
    {
        if (string.IsNullOrEmpty(rawHref)) return null;
        var effectiveBase = EffectiveBase(document, pageUrl);
        return Uri.TryCreate(effectiveBase, rawHref, out var result) ? result : null;
    }

    /// <summary>Convenience overload for call sites that
    /// want the result as a <see cref="Uri"/> but have a
    /// guaranteed-absolute fallback if resolution fails —
    /// returns <paramref name="pageUrl"/> on failure
    /// instead of <c>null</c>.</summary>
    public static Uri ResolveOrPage(Document? document, Uri pageUrl, string? rawHref)
    {
        return Resolve(document, pageUrl, rawHref) ?? pageUrl;
    }

    /// <summary>The effective base URL for resolving
    /// relatives in <paramref name="document"/>. Looks up
    /// the first <c>&lt;base href="…"&gt;</c> in document
    /// order; uses <paramref name="pageUrl"/> alone when
    /// there isn't one (the common case for non-SPA
    /// pages).</summary>
    public static Uri EffectiveBase(Document? document, Uri pageUrl)
    {
        if (document is null) return pageUrl;
        var baseHref = FindBaseHref(document);
        if (string.IsNullOrWhiteSpace(baseHref)) return pageUrl;
        // Resolve the base href itself against the page URL
        // — a <base href="/"> means the site root; a
        // <base href="https://cdn…"> pins everything to an
        // external origin; a <base href="subdir/"> shifts
        // by one directory.
        return Uri.TryCreate(pageUrl, baseHref, out var eb) ? eb : pageUrl;
    }

    private static string? FindBaseHref(Document document)
    {
        // The <base> element must be in <head>, so we only
        // scan head children — avoids the O(document)
        // tree walk a generic QuerySelector would do.
        var root = document.DocumentElement;
        if (root is null) return null;
        foreach (var child in root.Children)
        {
            if (child.TagName != "head") continue;
            foreach (var h in child.Children)
            {
                if (h.TagName != "base") continue;
                var href = h.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href)) return href;
            }
            break;
        }
        return null;
    }
}
