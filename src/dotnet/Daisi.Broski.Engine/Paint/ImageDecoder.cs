namespace Daisi.Broski.Engine.Paint;

/// <summary>
/// Unified entry point for decoding raster image bytes into
/// a <see cref="RasterBuffer"/>. Sniffs the magic bytes at
/// the head of the buffer and dispatches to the right
/// format-specific decoder — currently PNG and JPEG. SVG is
/// handled upstream in <see cref="PageLoader"/> because it
/// parses into a DOM subtree rather than pixels. Formats we
/// don't decode yet (WebP, AVIF, GIF, BMP) return
/// <c>null</c> and let the painter fall back to the
/// placeholder rect.
/// </summary>
public static class ImageDecoder
{
    public static RasterBuffer? TryDecode(byte[] data)
    {
        if (data is null || data.Length < 4) return null;

        // PNG: 89 50 4E 47 ...
        if (data[0] == 0x89 && data[1] == 0x50 &&
            data[2] == 0x4E && data[3] == 0x47)
        {
            return PngDecoder.TryDecode(data);
        }

        // JPEG: FF D8 FF (SOI followed by first marker
        // prefix; the classic JFIF and EXIF variants both
        // share this three-byte opener).
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return JpegDecoder.TryDecode(data);
        }

        return null;
    }
}
