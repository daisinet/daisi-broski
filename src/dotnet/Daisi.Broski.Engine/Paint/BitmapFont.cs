namespace Daisi.Broski.Engine.Paint;

/// <summary>
/// Tiny ASCII-only bitmap font for the painter. 5×7 monospace
/// glyphs hand-coded as multi-line string literals at the
/// bottom of this file — readable in source so a missing
/// glyph or a wrong dot is easy to spot.
///
/// <para>
/// Why a bundled bitmap and not a real TrueType reader: the
/// product code constraint is BCL-only (no NuGet). The BCL
/// ships no font-rasterization API on .NET 6+ that's
/// cross-platform; <c>System.Drawing.Common</c> is Windows-
/// only and deprecated, and writing a full TTF parser is
/// thousands of lines. A bundled bitmap covers the
/// "screenshot has readable text" use case in a few hundred
/// bytes of data + ~100 lines of code, with the tradeoff
/// that everything is one fixed font in one fixed size.
/// </para>
///
/// <para>
/// Coverage: ASCII 32-126 — every printable character. Glyphs
/// outside this range render as the missing-glyph rectangle.
/// Lowercase letters are rendered as their uppercase
/// counterparts where authoring time was tight; readability
/// over fidelity for a debug-render font.
/// </para>
/// </summary>
public static class BitmapFont
{
    /// <summary>Width of one glyph cell in pixels (5 lit
    /// columns + 1 trailing pixel of intercharacter spacing).</summary>
    public const int CellWidth = 6;

    /// <summary>Height of one glyph in pixels.</summary>
    public const int GlyphHeight = 7;

    /// <summary>Recommended line height — glyph height plus a
    /// 1px leading on each side.</summary>
    public const int LineHeight = 9;

    // Lazy-initialized so the static field for GlyphArt
    // (declared further down in this file) is non-null by
    // the time BuildGlyphs() reads it. C# field initializers
    // run in declaration order, so a direct
    // `= BuildGlyphs()` here would see a null GlyphArt.
    private static byte[][]? _glyphsCache;
    private static byte[][] _glyphs => _glyphsCache ??= BuildGlyphs();

    /// <summary>Pixel width of a one-line text run (no
    /// wrapping). Glyph cells are fixed-width, so the total
    /// is just <c>length * CellWidth</c>.</summary>
    public static int MeasureText(string text) =>
        text.Length * CellWidth;

    /// <summary>Render a single line of text into the buffer
    /// starting at <paramref name="x"/> / <paramref name="y"/>
    /// (top-left of the first glyph). Each lit pixel is
    /// painted as a 1×1 solid rect of <paramref name="color"/>.
    /// Out-of-buffer pixels are clipped by
    /// <see cref="RasterBuffer.FillRect"/>.</summary>
    public static void DrawText(RasterBuffer buffer, int x, int y, string text, PaintColor color)
    {
        if (string.IsNullOrEmpty(text) || color.IsTransparent) return;
        for (int i = 0; i < text.Length; i++)
        {
            DrawGlyph(buffer, x + i * CellWidth, y, text[i], color);
        }
    }

    /// <summary>Draw one glyph. Honors uppercase fallback for
    /// lowercase letters where the bitmap table only carries
    /// the uppercase form.</summary>
    private static void DrawGlyph(RasterBuffer buffer, int x, int y, char ch, PaintColor color)
    {
        // Lowercase fallback: many lowercase letters share a
        // recognizable shape with their uppercase, and
        // writing two complete alphabets doubles the data
        // budget. We render lowercase as uppercase a row
        // shorter at the top, simulating an x-height drop.
        bool lowerFallback = ch is >= 'a' and <= 'z';
        char effective = lowerFallback ? char.ToUpperInvariant(ch) : ch;

        if (effective < 32 || effective > 126)
        {
            DrawMissingGlyph(buffer, x, y, color);
            return;
        }
        var bits = _glyphs[effective - 32];
        int yOffset = lowerFallback ? 2 : 0; // shift down so x-height is visible
        int rowsToDraw = lowerFallback ? GlyphHeight - 2 : GlyphHeight;
        for (int r = 0; r < rowsToDraw; r++)
        {
            byte row = bits[r];
            for (int c = 0; c < 5; c++)
            {
                if ((row & (1 << (4 - c))) != 0)
                {
                    buffer.FillRect(x + c, y + r + yOffset, 1, 1, color);
                }
            }
        }
    }

    private static void DrawMissingGlyph(RasterBuffer buffer, int x, int y, PaintColor color)
    {
        // 5×7 outlined box — visually distinct so missing
        // characters are obvious.
        buffer.FillRect(x, y, 5, 1, color);
        buffer.FillRect(x, y + GlyphHeight - 1, 5, 1, color);
        buffer.FillRect(x, y, 1, GlyphHeight, color);
        buffer.FillRect(x + 4, y, 1, GlyphHeight, color);
    }

    /// <summary>Parse the multi-line glyph art at the bottom
    /// of this file into 7-byte-per-glyph bitmaps. Each row
    /// is a 5-character string of <c>X</c> (lit) or anything
    /// else (off). Authored as text so a typo in a glyph is
    /// visible at a glance.</summary>
    private static byte[][] BuildGlyphs()
    {
        var glyphs = new byte[95][];
        for (int i = 0; i < 95; i++) glyphs[i] = new byte[GlyphHeight];
        for (int i = 0; i < GlyphArt.Length && i < 95; i++)
        {
            var rows = GlyphArt[i];
            for (int r = 0; r < GlyphHeight && r < rows.Length; r++)
            {
                byte v = 0;
                var row = rows[r];
                for (int c = 0; c < 5 && c < row.Length; c++)
                {
                    if (row[c] == 'X') v |= (byte)(1 << (4 - c));
                }
                glyphs[i][r] = v;
            }
        }
        return glyphs;
    }

    // ---------------------------------------------------------
    // Glyph art — ASCII 32 (space) through 126 (~).
    // 95 entries, each 7 rows of 5 chars. 'X' = lit pixel.
    // Authored for readability over fidelity; a font snob
    // will hate it, but it's enough for "I can read what
    // the page says" debug screenshots.
    // ---------------------------------------------------------

    private static readonly string[][] GlyphArt = new[]
    {
        // 32  (space)
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        // 33  !
        new[] { "..X..", "..X..", "..X..", "..X..", ".....", "..X..", "....." },
        // 34  "
        new[] { ".X.X.", ".X.X.", ".....", ".....", ".....", ".....", "....." },
        // 35  #
        new[] { ".X.X.", ".X.X.", "XXXXX", ".X.X.", "XXXXX", ".X.X.", ".X.X." },
        // 36  $
        new[] { "..X..", ".XXXX", "X.X..", ".XXX.", "..X.X", "XXXX.", "..X.." },
        // 37  %
        new[] { "XX..X", "XX.X.", "..X..", ".X.XX", "X.XXX", ".....", "....." },
        // 38  &
        new[] { ".XX..", "X..X.", ".XX..", ".X.X.", "X.X.X", "X..X.", ".XX.X" },
        // 39  '
        new[] { "..X..", "..X..", ".....", ".....", ".....", ".....", "....." },
        // 40  (
        new[] { "...X.", "..X..", ".X...", ".X...", ".X...", "..X..", "...X." },
        // 41  )
        new[] { ".X...", "..X..", "...X.", "...X.", "...X.", "..X..", ".X..." },
        // 42  *
        new[] { ".....", "..X..", "X.X.X", ".XXX.", "X.X.X", "..X..", "....." },
        // 43  +
        new[] { ".....", "..X..", "..X..", "XXXXX", "..X..", "..X..", "....." },
        // 44  ,
        new[] { ".....", ".....", ".....", ".....", "..X..", "..X..", ".X..." },
        // 45  -
        new[] { ".....", ".....", ".....", "XXXXX", ".....", ".....", "....." },
        // 46  .
        new[] { ".....", ".....", ".....", ".....", ".....", "..X..", "....." },
        // 47  /
        new[] { "....X", "...X.", "..X..", ".X...", "X....", ".....", "....." },
        // 48  0
        new[] { ".XXX.", "X...X", "X..XX", "X.X.X", "XX..X", "X...X", ".XXX." },
        // 49  1
        new[] { "..X..", ".XX..", "..X..", "..X..", "..X..", "..X..", ".XXX." },
        // 50  2
        new[] { ".XXX.", "X...X", "....X", "...X.", "..X..", ".X...", "XXXXX" },
        // 51  3
        new[] { ".XXX.", "X...X", "....X", "..XX.", "....X", "X...X", ".XXX." },
        // 52  4
        new[] { "...X.", "..XX.", ".X.X.", "X..X.", "XXXXX", "...X.", "...X." },
        // 53  5
        new[] { "XXXXX", "X....", "XXXX.", "....X", "....X", "X...X", ".XXX." },
        // 54  6
        new[] { ".XXX.", "X....", "X....", "XXXX.", "X...X", "X...X", ".XXX." },
        // 55  7
        new[] { "XXXXX", "....X", "...X.", "..X..", ".X...", ".X...", ".X..." },
        // 56  8
        new[] { ".XXX.", "X...X", "X...X", ".XXX.", "X...X", "X...X", ".XXX." },
        // 57  9
        new[] { ".XXX.", "X...X", "X...X", ".XXXX", "....X", "....X", ".XXX." },
        // 58  :
        new[] { ".....", "..X..", ".....", ".....", ".....", "..X..", "....." },
        // 59  ;
        new[] { ".....", "..X..", ".....", ".....", "..X..", "..X..", ".X..." },
        // 60  <
        new[] { "...X.", "..X..", ".X...", "X....", ".X...", "..X..", "...X." },
        // 61  =
        new[] { ".....", ".....", "XXXXX", ".....", "XXXXX", ".....", "....." },
        // 62  >
        new[] { ".X...", "..X..", "...X.", "....X", "...X.", "..X..", ".X..." },
        // 63  ?
        new[] { ".XXX.", "X...X", "....X", "...X.", "..X..", ".....", "..X.." },
        // 64  @
        new[] { ".XXX.", "X...X", "X.XXX", "X.X.X", "X.XXX", "X....", ".XXX." },
        // 65  A
        new[] { ".XXX.", "X...X", "X...X", "XXXXX", "X...X", "X...X", "X...X" },
        // 66  B
        new[] { "XXXX.", "X...X", "X...X", "XXXX.", "X...X", "X...X", "XXXX." },
        // 67  C
        new[] { ".XXX.", "X...X", "X....", "X....", "X....", "X...X", ".XXX." },
        // 68  D
        new[] { "XXXX.", "X...X", "X...X", "X...X", "X...X", "X...X", "XXXX." },
        // 69  E
        new[] { "XXXXX", "X....", "X....", "XXXX.", "X....", "X....", "XXXXX" },
        // 70  F
        new[] { "XXXXX", "X....", "X....", "XXXX.", "X....", "X....", "X...." },
        // 71  G
        new[] { ".XXX.", "X...X", "X....", "X.XXX", "X...X", "X...X", ".XXX." },
        // 72  H
        new[] { "X...X", "X...X", "X...X", "XXXXX", "X...X", "X...X", "X...X" },
        // 73  I
        new[] { ".XXX.", "..X..", "..X..", "..X..", "..X..", "..X..", ".XXX." },
        // 74  J
        new[] { "..XXX", "...X.", "...X.", "...X.", "...X.", "X..X.", ".XX.." },
        // 75  K
        new[] { "X...X", "X..X.", "X.X..", "XX...", "X.X..", "X..X.", "X...X" },
        // 76  L
        new[] { "X....", "X....", "X....", "X....", "X....", "X....", "XXXXX" },
        // 77  M
        new[] { "X...X", "XX.XX", "X.X.X", "X.X.X", "X...X", "X...X", "X...X" },
        // 78  N
        new[] { "X...X", "X...X", "XX..X", "X.X.X", "X..XX", "X...X", "X...X" },
        // 79  O
        new[] { ".XXX.", "X...X", "X...X", "X...X", "X...X", "X...X", ".XXX." },
        // 80  P
        new[] { "XXXX.", "X...X", "X...X", "XXXX.", "X....", "X....", "X...." },
        // 81  Q
        new[] { ".XXX.", "X...X", "X...X", "X...X", "X.X.X", "X..X.", ".XX.X" },
        // 82  R
        new[] { "XXXX.", "X...X", "X...X", "XXXX.", "X.X..", "X..X.", "X...X" },
        // 83  S
        new[] { ".XXX.", "X...X", "X....", ".XXX.", "....X", "X...X", ".XXX." },
        // 84  T
        new[] { "XXXXX", "..X..", "..X..", "..X..", "..X..", "..X..", "..X.." },
        // 85  U
        new[] { "X...X", "X...X", "X...X", "X...X", "X...X", "X...X", ".XXX." },
        // 86  V
        new[] { "X...X", "X...X", "X...X", "X...X", "X...X", ".X.X.", "..X.." },
        // 87  W
        new[] { "X...X", "X...X", "X...X", "X.X.X", "X.X.X", "XX.XX", "X...X" },
        // 88  X
        new[] { "X...X", "X...X", ".X.X.", "..X..", ".X.X.", "X...X", "X...X" },
        // 89  Y
        new[] { "X...X", "X...X", ".X.X.", "..X..", "..X..", "..X..", "..X.." },
        // 90  Z
        new[] { "XXXXX", "....X", "...X.", "..X..", ".X...", "X....", "XXXXX" },
        // 91  [
        new[] { ".XXX.", ".X...", ".X...", ".X...", ".X...", ".X...", ".XXX." },
        // 92  \
        new[] { "X....", ".X...", "..X..", "...X.", "....X", ".....", "....." },
        // 93  ]
        new[] { ".XXX.", "...X.", "...X.", "...X.", "...X.", "...X.", ".XXX." },
        // 94  ^
        new[] { "..X..", ".X.X.", "X...X", ".....", ".....", ".....", "....." },
        // 95  _
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "XXXXX" },
        // 96  `
        new[] { ".X...", "..X..", ".....", ".....", ".....", ".....", "....." },
        // 97-122 (a-z) — fall back to uppercase via the
        // x-height drop in DrawGlyph; these slots are unused
        // but kept as identity entries to keep the table at
        // 95 entries.
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        // 123  {
        new[] { "..XX.", ".X...", ".X...", "X....", ".X...", ".X...", "..XX." },
        // 124  |
        new[] { "..X..", "..X..", "..X..", "..X..", "..X..", "..X..", "..X.." },
        // 125  }
        new[] { ".XX..", "...X.", "...X.", "....X", "...X.", "...X.", ".XX.." },
        // 126  ~
        new[] { ".....", ".XX.X", "X.XX.", ".....", ".....", ".....", "....." },
    };
}
