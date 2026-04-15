using System.IO.Compression;
using System.Text;

namespace Daisi.Broski.Docs;

/// <summary>
/// Format detection helpers — pure reads over the response bytes.
/// Kept separate from <see cref="DocDispatcher"/> so the routing logic
/// stays readable and each detection step is independently testable.
/// </summary>
internal static class DocDetection
{
    internal enum DocKind { None, Pdf, Docx, Xlsx }

    /// <summary>Reduce a raw Content-Type header
    /// (<c>"application/pdf; charset=binary"</c>) to its media-type
    /// portion (<c>"application/pdf"</c>), lowercase. Returns an
    /// empty string when the input is null or empty.</summary>
    internal static string NormalizeContentType(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        int semi = raw.IndexOf(';');
        var media = (semi < 0 ? raw : raw[..semi]).Trim();
        return media.ToLowerInvariant();
    }

    /// <summary>Map a normalized Content-Type to a DocKind, or
    /// <c>DocKind.None</c> if the media type isn't one we route.</summary>
    internal static DocKind FromContentType(string normalized) => normalized switch
    {
        "application/pdf" => DocKind.Pdf,
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => DocKind.Docx,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => DocKind.Xlsx,
        _ => DocKind.None,
    };

    /// <summary>Look at the first few body bytes. PDFs have the
    /// <c>%PDF-</c> signature; OOXML packages are ZIPs
    /// (<c>PK\x03\x04</c>) that need a secondary check to
    /// distinguish docx / xlsx / something-else-zipped.</summary>
    internal static DocKind FromMagic(ReadOnlySpan<byte> body)
    {
        if (body.Length >= 5 &&
            body[0] == 0x25 && body[1] == 0x50 &&
            body[2] == 0x44 && body[3] == 0x46 && body[4] == 0x2D)
        {
            return DocKind.Pdf;
        }
        if (body.Length >= 4 &&
            body[0] == 0x50 && body[1] == 0x4B &&
            body[2] == 0x03 && body[3] == 0x04)
        {
            return PeekOoxmlKind(body);
        }
        return DocKind.None;
    }

    /// <summary>Open the bytes as a ZIP, read <c>[Content_Types].xml</c>,
    /// and decide whether the package is wordprocessingml, spreadsheetml,
    /// or something else. A ZIP without a recognizable content-types
    /// entry falls through to <c>None</c> so we don't claim generic
    /// .zip responses (for which the caller would otherwise get our
    /// converter's error shell rather than the real HTML).</summary>
    internal static DocKind PeekOoxmlKind(ReadOnlySpan<byte> body)
    {
        // ZipArchive needs a seekable stream; build a fresh one over a
        // copy so we don't hold a reference into the caller's buffer.
        byte[] copy = body.ToArray();
        try
        {
            using var ms = new MemoryStream(copy, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry("[Content_Types].xml");
            if (entry is null) return DocKind.None;
            using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            string contents = reader.ReadToEnd();
            if (contents.Contains("wordprocessingml", StringComparison.Ordinal))
                return DocKind.Docx;
            if (contents.Contains("spreadsheetml", StringComparison.Ordinal))
                return DocKind.Xlsx;
            return DocKind.None;
        }
        catch (InvalidDataException)
        {
            // Not actually a ZIP despite the magic bytes — rare but
            // possible for truncated downloads. Punt on detection.
            return DocKind.None;
        }
    }

    /// <summary>Fall-back hint based on the URL path. Only used when
    /// neither Content-Type nor magic bytes nailed down a kind — in
    /// practice this matters for SharePoint and similar servers that
    /// strip Content-Type on redirect.</summary>
    internal static DocKind FromUrlSuffix(Uri url)
    {
        string path = url.AbsolutePath;
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return DocKind.Pdf;
        if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) return DocKind.Docx;
        if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return DocKind.Xlsx;
        return DocKind.None;
    }
}
