using Daisi.Broski.Docs.Docx;
using Daisi.Broski.Docs.Pdf;
using Daisi.Broski.Docs.Xlsx;

namespace Daisi.Broski.Docs;

/// <summary>
/// Routes an HTTP response to a doc converter based on Content-Type,
/// magic bytes, or URL suffix. Called once per page load by the
/// Engine, right after the fetch and before the HTML decode path —
/// a match short-circuits HTML parsing and substitutes a synthetic
/// article document that the rest of the pipeline (extractor,
/// formatters) handles unchanged.
///
/// <para>Priority: Content-Type → magic bytes → URL suffix. A
/// fetched response that matches none of the three is left alone
/// (the caller's HTML path runs as normal).</para>
/// </summary>
public static class DocDispatcher
{
    /// <summary>Try to route <paramref name="body"/> to a known
    /// converter. On success, returns <c>true</c> and sets
    /// <paramref name="html"/> to a complete
    /// <c>&lt;!doctype html&gt;…&lt;/html&gt;</c> string. On no-match,
    /// returns <c>false</c> and leaves <paramref name="html"/> null —
    /// the caller should continue with its normal HTML parsing path.</summary>
    /// <param name="body">Raw response body bytes. May be empty.</param>
    /// <param name="contentType">Raw Content-Type header, parameters
    /// allowed (we strip them). Pass null when the server didn't
    /// send one.</param>
    /// <param name="url">The fetched URL after redirects. Used for
    /// suffix fall-back and for metadata defaults (title fallback,
    /// base for relative link resolution).</param>
    public static bool TryConvert(
        byte[] body, string? contentType, Uri url, out string? html)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(url);

        var kind = DetectKind(body, contentType, url);
        if (kind == DocDetection.DocKind.None)
        {
            html = null;
            return false;
        }

        IDocConverter converter = kind switch
        {
            DocDetection.DocKind.Pdf => new PdfConverter(),
            DocDetection.DocKind.Docx => new DocxConverter(),
            DocDetection.DocKind.Xlsx => new XlsxConverter(),
            _ => throw new InvalidOperationException($"No converter for {kind}"),
        };
        html = converter.Convert(body, url);
        return true;
    }

    /// <summary>Three-tier detection. Content-Type wins when it names
    /// a doc media type. Otherwise, magic bytes can still identify
    /// the format (common when a server ships docx as
    /// application/octet-stream). As a last resort, the URL's suffix
    /// disambiguates — only useful for PDF, since docx / xlsx without
    /// a ZIP header is unrecognizable anyway.</summary>
    private static DocDetection.DocKind DetectKind(
        byte[] body, string? contentType, Uri url)
    {
        var ct = DocDetection.NormalizeContentType(contentType);
        var byCt = DocDetection.FromContentType(ct);
        if (byCt != DocDetection.DocKind.None) return byCt;

        // Skip the magic-bytes peek for unambiguously HTML / text
        // Content-Types. Saves a ZipArchive open on every HTML
        // page load — the hot path — without changing behavior.
        if (!IsBinaryCandidate(ct))
        {
            return DocDetection.FromUrlSuffix(url);
        }

        var byMagic = DocDetection.FromMagic(body);
        if (byMagic != DocDetection.DocKind.None) return byMagic;

        return DocDetection.FromUrlSuffix(url);
    }

    /// <summary>Is it worth probing the body as binary? Only when
    /// Content-Type is missing, generic, or otherwise binary-ish.
    /// Known text types (text/*, application/xhtml+xml, JSON, XML)
    /// are left alone — a .docx misdelivered as text/html is so rare
    /// we accept the miss rather than slow down every real HTML load.</summary>
    private static bool IsBinaryCandidate(string normalizedCt)
    {
        if (string.IsNullOrEmpty(normalizedCt)) return true;
        if (normalizedCt.StartsWith("text/", StringComparison.Ordinal)) return false;
        if (normalizedCt == "application/xhtml+xml") return false;
        if (normalizedCt == "application/xml") return false;
        if (normalizedCt == "application/json") return false;
        if (normalizedCt == "application/javascript") return false;
        // application/octet-stream, application/zip, anything else —
        // worth a magic-bytes peek.
        return true;
    }
}
