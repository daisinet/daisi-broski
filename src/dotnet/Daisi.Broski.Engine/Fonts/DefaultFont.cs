using System.Reflection;

namespace Daisi.Broski.Engine.Fonts;

/// <summary>
/// Lazy-loaded fallback font, parsed once per process.
/// Used by <see cref="FontResolver"/> when no fetched
/// <c>@font-face</c> matches the cascade's <c>font-family</c>.
/// Without this fallback, sites that rely on system fonts
/// (Verdana, Helvetica, Segoe UI, the entire <c>sans-serif</c>
/// generic family) would render with the bundled 5×7
/// bitmap glyphs — readable but clearly debug-grade.
/// Roboto Regular (Apache 2.0) ships as an embedded
/// resource in this assembly.
/// </summary>
internal static class DefaultFont
{
    private static TtfReader? _reader;
    private static bool _loaded;
    private static readonly object _gate = new();

    public static TtfReader? Get()
    {
        if (_loaded) return _reader;
        lock (_gate)
        {
            if (_loaded) return _reader;
            _loaded = true;
            try
            {
                var asm = typeof(DefaultFont).Assembly;
                using var stream = asm.GetManifestResourceStream(
                    "Daisi.Broski.Engine.Fonts.EmbeddedFont.ttf");
                if (stream is null) return null;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                _reader = TtfReader.TryParse(ms.ToArray());
            }
            catch
            {
                // Best-effort — falling back to the bitmap
                // font is always still available.
            }
            return _reader;
        }
    }
}
