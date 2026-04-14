using System.Buffers.Binary;
using System.Text;

namespace Daisi.Broski.Engine.Fonts;

/// <summary>
/// Minimum-viable TrueType / OpenType reader. Parses the
/// subset of tables we need to extract glyph outlines + advance
/// widths for rasterization:
/// <list type="bullet">
/// <item><c>cmap</c> — character code → glyph index (subtable
///   format 4 only; handles every modern font's BMP mapping).</item>
/// <item><c>head</c> — unitsPerEm + indexToLocFormat (controls
///   whether <c>loca</c> entries are u16 or u32).</item>
/// <item><c>hhea</c> — numberOfHMetrics.</item>
/// <item><c>maxp</c> — numGlyphs.</item>
/// <item><c>hmtx</c> — per-glyph advance width + lsb.</item>
/// <item><c>loca</c> + <c>glyf</c> — glyph outlines as quadratic
///   Béziers (simple glyphs only; composite glyphs skipped for
///   v1 since they're mostly fancy typographic features like
///   f-ligatures that renders-as-empty is OK for a screenshot
///   engine).</item>
/// </list>
///
/// <para>
/// Not implemented (deliberately): CFF / CFF2 (OpenType's cubic
/// Bézier variant, used by many Adobe fonts), composite glyph
/// resolution, hinting, kerning, GSUB / GPOS shaping, bitmap
/// glyph strikes. When a font we can't parse is requested, the
/// painter falls back to the built-in bitmap font.
/// </para>
/// </summary>
public sealed class TtfReader
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new();

    /// <summary>Design grid units per em square — the
    /// coordinate system glyph outlines are authored in. Scale
    /// a CSS <c>font-size</c> (in px) to the font's units by
    /// multiplying by <c>pixelSize / UnitsPerEm</c>.</summary>
    public int UnitsPerEm { get; private set; }

    /// <summary>Number of glyphs in the font. Indexes into
    /// <c>loca</c> / <c>hmtx</c> are bounded by this.</summary>
    public int NumGlyphs { get; private set; }

    /// <summary>Number of entries in <c>hmtx</c> that carry
    /// an advance width. Glyphs past this index share the
    /// last advance (per spec).</summary>
    public int NumberOfHMetrics { get; private set; }

    /// <summary>0 = short (u16 × 2) <c>loca</c> offsets,
    /// 1 = long (u32). Needs the glyph reader to know the
    /// index format.</summary>
    public int IndexToLocFormat { get; private set; }

    /// <summary>cmap format 4 subtable data. Look up a
    /// character code by binary-searching <c>EndCode</c>.</summary>
    private int[] _cmapEndCode = Array.Empty<int>();
    private int[] _cmapStartCode = Array.Empty<int>();
    private int[] _cmapIdDelta = Array.Empty<int>();
    private int[] _cmapIdRangeOffset = Array.Empty<int>();
    private int _cmapGlyphIdArrayOffset; // file-absolute
    private int _cmapSegCount;

    private int _locaOffset;
    private int _glyfOffset;
    private int _hmtxOffset;

    private TtfReader(byte[] data) { _data = data; }

    /// <summary>Try to parse the font file. Returns null when
    /// the file isn't a TTF/OTF we can read (OTF with CFF
    /// outlines, WOFF/WOFF2 compressed wrappers, invalid
    /// tables). The painter falls back to the bitmap font in
    /// that case — we'd rather ship readable pixels than
    /// throw.</summary>
    public static TtfReader? TryParse(byte[] data)
    {
        if (data.Length < 12) return null;
        try
        {
            var reader = new TtfReader(data);
            uint sfntVersion = ReadU32(data, 0);
            // 0x00010000 = TrueType outlines, 'OTTO' = CFF,
            // 'true' = legacy Apple TrueType, 'typ1' = Type 1.
            // We only handle TrueType outlines — reject
            // CFF-flavored OpenType early so the painter
            // doesn't try to render nothing.
            if (sfntVersion != 0x00010000
                && sfntVersion != 0x74727565 /* 'true' */)
            {
                return null;
            }
            int numTables = ReadU16(data, 4);
            int recordOffset = 12;
            for (int i = 0; i < numTables; i++)
            {
                if (recordOffset + 16 > data.Length) return null;
                string tag = Encoding.ASCII.GetString(data, recordOffset, 4);
                int offset = (int)ReadU32(data, recordOffset + 8);
                int length = (int)ReadU32(data, recordOffset + 12);
                reader._tables[tag] = (offset, length);
                recordOffset += 16;
            }
            if (!reader.ParseHead()) return null;
            if (!reader.ParseMaxp()) return null;
            if (!reader.ParseHhea()) return null;
            if (!reader.ParseHmtx()) return null;
            if (!reader.ParseCmap()) return null;
            if (!reader.ResolveLocaAndGlyf()) return null;
            return reader;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Table parsers
    // ------------------------------------------------------------------

    private bool ParseHead()
    {
        if (!_tables.TryGetValue("head", out var t)) return false;
        if (t.Length < 54) return false;
        UnitsPerEm = ReadU16(_data, t.Offset + 18);
        // 50: indexToLocFormat
        IndexToLocFormat = (short)ReadU16(_data, t.Offset + 50);
        return UnitsPerEm > 0;
    }

    private bool ParseMaxp()
    {
        if (!_tables.TryGetValue("maxp", out var t)) return false;
        if (t.Length < 6) return false;
        NumGlyphs = ReadU16(_data, t.Offset + 4);
        return NumGlyphs > 0;
    }

    private bool ParseHhea()
    {
        if (!_tables.TryGetValue("hhea", out var t)) return false;
        if (t.Length < 36) return false;
        NumberOfHMetrics = ReadU16(_data, t.Offset + 34);
        return true;
    }

    private bool ParseHmtx()
    {
        if (!_tables.TryGetValue("hmtx", out var t)) return false;
        _hmtxOffset = t.Offset;
        return true;
    }

    private bool ResolveLocaAndGlyf()
    {
        if (!_tables.TryGetValue("loca", out var loca)) return false;
        if (!_tables.TryGetValue("glyf", out var glyf)) return false;
        _locaOffset = loca.Offset;
        _glyfOffset = glyf.Offset;
        return true;
    }

    private bool ParseCmap()
    {
        if (!_tables.TryGetValue("cmap", out var t)) return false;
        int numTables = ReadU16(_data, t.Offset + 2);
        int best = -1;
        int bestOff = 0;
        for (int i = 0; i < numTables; i++)
        {
            int rec = t.Offset + 4 + i * 8;
            int platform = ReadU16(_data, rec);
            int encoding = ReadU16(_data, rec + 2);
            int off = t.Offset + (int)ReadU32(_data, rec + 4);
            // Prefer Windows Unicode BMP (3, 1) — every modern
            // font has it and format-4 subtables are the
            // common encoding there. Fall back to any format-4
            // subtable we find.
            int score = (platform == 3 && encoding == 1) ? 10 : 1;
            int format = ReadU16(_data, off);
            if (format != 4) continue;
            if (score > best) { best = score; bestOff = off; }
        }
        if (best < 0) return false;
        // cmap format 4: header + 4 parallel arrays + a single
        // glyphIdArray following (referenced via idRangeOffset
        // magic from inside the idRangeOffset array itself).
        _cmapSegCount = ReadU16(_data, bestOff + 6) / 2;
        int endCodeOff = bestOff + 14;
        int startCodeOff = endCodeOff + _cmapSegCount * 2 + 2; // +2 reservedPad
        int idDeltaOff = startCodeOff + _cmapSegCount * 2;
        int idRangeOffsetOff = idDeltaOff + _cmapSegCount * 2;
        _cmapEndCode = new int[_cmapSegCount];
        _cmapStartCode = new int[_cmapSegCount];
        _cmapIdDelta = new int[_cmapSegCount];
        _cmapIdRangeOffset = new int[_cmapSegCount];
        for (int i = 0; i < _cmapSegCount; i++)
        {
            _cmapEndCode[i] = ReadU16(_data, endCodeOff + i * 2);
            _cmapStartCode[i] = ReadU16(_data, startCodeOff + i * 2);
            _cmapIdDelta[i] = (short)ReadU16(_data, idDeltaOff + i * 2);
            _cmapIdRangeOffset[i] = ReadU16(_data, idRangeOffsetOff + i * 2);
        }
        // glyphIdArray begins right after idRangeOffset; we
        // also record the segment origin so the
        // "idRangeOffset is relative to itself" trick works.
        _cmapGlyphIdArrayOffset = idRangeOffsetOff;
        return true;
    }

    // ------------------------------------------------------------------
    // Public lookups
    // ------------------------------------------------------------------

    /// <summary>Resolve a character code to a glyph index.
    /// Returns 0 (the .notdef glyph) for characters the font
    /// doesn't cover — matching the spec-prescribed fallback.</summary>
    public int GlyphIndex(int charCode)
    {
        for (int i = 0; i < _cmapSegCount; i++)
        {
            if (_cmapEndCode[i] < charCode) continue;
            if (_cmapStartCode[i] > charCode) return 0;
            int ro = _cmapIdRangeOffset[i];
            if (ro == 0)
            {
                return (charCode + _cmapIdDelta[i]) & 0xFFFF;
            }
            // Spec: glyphIdOffset = idRangeOffsetOff[i] + ro +
            //   2 * (charCode - startCode[i])
            int idx = _cmapGlyphIdArrayOffset + i * 2
                + ro + 2 * (charCode - _cmapStartCode[i]);
            if (idx + 2 > _data.Length) return 0;
            int raw = ReadU16(_data, idx);
            if (raw == 0) return 0;
            return (raw + _cmapIdDelta[i]) & 0xFFFF;
        }
        return 0;
    }

    /// <summary>Advance width of a glyph in font units. Glyph
    /// indices past <see cref="NumberOfHMetrics"/> share the
    /// last explicit advance per spec.</summary>
    public int AdvanceWidth(int glyphIndex)
    {
        if (glyphIndex < 0 || NumberOfHMetrics == 0) return 0;
        int lookup = glyphIndex < NumberOfHMetrics
            ? glyphIndex : NumberOfHMetrics - 1;
        return ReadU16(_data, _hmtxOffset + lookup * 4);
    }

    /// <summary>Glyph outline for the given index, flattened
    /// into a list of subpaths (each a list of x,y vertices in
    /// font-unit space). Returns an empty list for space-only
    /// glyphs and composite glyphs we skip. Callers transform
    /// the vertices into screen coordinates and hand them to
    /// the scanline filler.</summary>
    public List<List<(double X, double Y)>> GlyphOutline(int glyphIndex)
    {
        var result = new List<List<(double, double)>>();
        if (glyphIndex < 0 || glyphIndex >= NumGlyphs) return result;
        int loc = ReadLoca(glyphIndex);
        int nextLoc = ReadLoca(glyphIndex + 1);
        if (nextLoc <= loc) return result; // empty glyph
        int g = _glyfOffset + loc;
        if (g + 10 > _data.Length) return result;
        short numContours = (short)ReadU16(_data, g);
        if (numContours < 0) return result; // composite glyph — deferred
        int pos = g + 10;

        // End-of-contour point indices (count = numContours).
        var endPts = new int[numContours];
        int maxPt = 0;
        for (int i = 0; i < numContours; i++)
        {
            endPts[i] = ReadU16(_data, pos);
            pos += 2;
            if (endPts[i] > maxPt) maxPt = endPts[i];
        }
        int numPoints = maxPt + 1;

        int instrLen = ReadU16(_data, pos);
        pos += 2 + instrLen; // skip TrueType bytecode

        // Flags are run-length encoded via the REPEAT bit.
        var flags = new byte[numPoints];
        int p = 0;
        while (p < numPoints)
        {
            byte flag = _data[pos++];
            flags[p++] = flag;
            if ((flag & 0x08) != 0)
            {
                byte repeat = _data[pos++];
                for (int r = 0; r < repeat && p < numPoints; r++) flags[p++] = flag;
            }
        }

        // X coordinates — flag bit 1 = 1-byte, bit 4 is sign
        // (short form) or same-as-previous (long form).
        var xs = new int[numPoints];
        int cur = 0;
        for (int i = 0; i < numPoints; i++)
        {
            byte f = flags[i];
            if ((f & 0x02) != 0)
            {
                int dx = _data[pos++];
                if ((f & 0x10) == 0) dx = -dx;
                cur += dx;
            }
            else if ((f & 0x10) == 0)
            {
                cur += (short)ReadU16(_data, pos);
                pos += 2;
            }
            xs[i] = cur;
        }
        // Y coordinates — same scheme, flag bits 2/5.
        var ys = new int[numPoints];
        cur = 0;
        for (int i = 0; i < numPoints; i++)
        {
            byte f = flags[i];
            if ((f & 0x04) != 0)
            {
                int dy = _data[pos++];
                if ((f & 0x20) == 0) dy = -dy;
                cur += dy;
            }
            else if ((f & 0x20) == 0)
            {
                cur += (short)ReadU16(_data, pos);
                pos += 2;
            }
            ys[i] = cur;
        }

        // Walk each contour. TrueType contours are sequences
        // of on-curve and off-curve points: two off-curve in a
        // row implies an implicit on-curve at their midpoint
        // (the "implicit TrueType point" trick). We flatten
        // each quadratic Bézier segment into line segments
        // via fixed-step subdivision.
        int start = 0;
        foreach (var end in endPts)
        {
            var path = BuildContour(xs, ys, flags, start, end);
            if (path.Count >= 3) result.Add(path);
            start = end + 1;
        }
        return result;
    }

    private int ReadLoca(int glyphIndex)
    {
        if (IndexToLocFormat == 0)
        {
            // short: stored ÷ 2 per spec
            return ReadU16(_data, _locaOffset + glyphIndex * 2) * 2;
        }
        return (int)ReadU32(_data, _locaOffset + glyphIndex * 4);
    }

    private static List<(double X, double Y)> BuildContour(
        int[] xs, int[] ys, byte[] flags, int start, int end)
    {
        // Normalize: if first point is off-curve, it's
        // either a midpoint with the last point, or the
        // start needs a virtual on-curve before the Bézier
        // segment. Handle both by pre-inserting the virtual
        // start point when needed.
        var path = new List<(double, double)>();
        bool[] onCurve = new bool[end - start + 1];
        var pts = new (double X, double Y)[end - start + 1];
        for (int i = start; i <= end; i++)
        {
            pts[i - start] = (xs[i], ys[i]);
            onCurve[i - start] = (flags[i] & 0x01) != 0;
        }
        int n = pts.Length;
        if (n == 0) return path;

        // Find first on-curve point to anchor the contour.
        int anchor = -1;
        for (int i = 0; i < n; i++) if (onCurve[i]) { anchor = i; break; }
        if (anchor < 0)
        {
            // All off-curve — synthesize an on-curve at the
            // midpoint of first and last control points.
            var mid = ((pts[0].X + pts[n - 1].X) / 2, (pts[0].Y + pts[n - 1].Y) / 2);
            path.Add(mid);
            for (int step = 0; step < n; step++)
            {
                var ctrl = pts[step];
                var next = pts[(step + 1) % n];
                var endP = ((ctrl.X + next.X) / 2, (ctrl.Y + next.Y) / 2);
                FlattenQuadratic(path, ctrl, endP);
            }
            path.Add(mid);
            return path;
        }

        // Walk from the first on-curve point around the ring.
        var startPoint = pts[anchor];
        path.Add(startPoint);
        for (int offset = 1; offset <= n; offset++)
        {
            int idx = (anchor + offset) % n;
            if (onCurve[idx])
            {
                // Direct line segment.
                path.Add(pts[idx]);
            }
            else
            {
                // Off-curve = quadratic control point. The
                // segment's end is either the next on-curve
                // point or the midpoint to another off-curve.
                int nextIdx = (anchor + offset + 1) % n;
                (double X, double Y) endP;
                if (onCurve[nextIdx])
                {
                    endP = pts[nextIdx];
                    offset++; // consume the on-curve end
                }
                else
                {
                    endP = ((pts[idx].X + pts[nextIdx].X) / 2,
                            (pts[idx].Y + pts[nextIdx].Y) / 2);
                }
                FlattenQuadratic(path, pts[idx], endP);
            }
        }
        return path;
    }

    private static void FlattenQuadratic(
        List<(double X, double Y)> path,
        (double X, double Y) ctrl, (double X, double Y) end)
    {
        // 8 steps gives visually smooth curves at typical
        // screen sizes without per-glyph overdraw; we reuse
        // the same count SvgRenderer uses for quadratic
        // Béziers.
        const int steps = 8;
        var start = path[^1];
        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            double mt = 1 - t;
            double x = mt * mt * start.X + 2 * mt * t * ctrl.X + t * t * end.X;
            double y = mt * mt * start.Y + 2 * mt * t * ctrl.Y + t * t * end.Y;
            path.Add((x, y));
        }
    }

    private static ushort ReadU16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static uint ReadU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}
