namespace Daisi.Broski.Docs.Pdf;

/// <summary>
/// A single extracted image from a PDF page, ready to be surfaced
/// in the synthetic HTML as an <c>&lt;img&gt;</c> element. Content
/// is the filter-decoded bytes; <see cref="MimeType"/> identifies
/// the format so the caller can build a <c>data:</c> URI.
///
/// <para>Scope limited to JPEG in this milestone — the <c>/DCTDecode</c>
/// filter's raw stream bytes are directly usable as a JPEG file,
/// so we surface them without needing a PNG encoder. Other image
/// formats (FlateDecode-compressed raw pixels, JPEG2000,
/// CCITTFaxDecode) would require additional encoding work to
/// produce a browser-renderable bytestream; deferred.</para>
/// </summary>
internal sealed record PdfImage(
    byte[] Content,
    string MimeType,
    int Width,
    int Height)
{
    /// <summary>Build a <c>data:</c> URI suitable for the
    /// <c>src</c> attribute of an <c>&lt;img&gt;</c>.</summary>
    public string ToDataUri()
        => "data:" + MimeType + ";base64," + Convert.ToBase64String(Content);
}
