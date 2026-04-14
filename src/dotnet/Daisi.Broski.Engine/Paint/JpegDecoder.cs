namespace Daisi.Broski.Engine.Paint;

/// <summary>
/// Hand-rolled JPEG decoder, BCL-only. Companion to
/// <see cref="PngDecoder"/>. Returns a <see cref="RasterBuffer"/>
/// of decoded RGBA pixels, or <c>null</c> if the bytes are
/// not a JPEG we can handle — caller falls back to the
/// placeholder rect the painter already draws for missing
/// images.
///
/// <para>
/// Minimum-viable subset — the common JFIF baseline that
/// covers ~99% of web JPEGs:
/// </para>
/// <list type="bullet">
/// <item>Baseline sequential DCT (SOF0). Progressive
///   (SOF2) and lossless / arithmetic / hierarchical modes
///   are not supported — progressive in particular would
///   need multi-pass coefficient accumulation across several
///   SOS segments.</item>
/// <item>8-bit sample precision. 12-bit extended isn't seen
///   on the web.</item>
/// <item>1 component (grayscale) or 3 components
///   (Y, Cb, Cr). Chroma subsampling via per-component H/V
///   sampling factors — handles 4:4:4, 4:2:2, 4:2:0, and
///   the 4:1:1 variant.</item>
/// <item>Restart markers (DRI / RSTn) — we reset DC
///   predictors on the interval and let the bit reader
///   silently skip the marker bytes embedded in the entropy
///   stream.</item>
/// <item>EXIF orientation (APP1) is ignored — we display
///   pixels as encoded. Most web images either have no
///   orientation tag or are already upright.</item>
/// </list>
///
/// <para>
/// The IDCT is a straightforward separable 1D transform
/// with a precomputed cosine table — correct, readable,
/// and fast enough for typical viewport-sized images. A
/// fixed-point AAN-scaled IDCT would be a couple of times
/// faster but adds code we don't yet need.
/// </para>
/// </summary>
public static class JpegDecoder
{
    // JPEG Annex A zigzag order — coefficients arrive in this
    // order in the entropy stream and we un-zigzag back to
    // natural 8×8 row-major on the fly.
    private static readonly int[] Zigzag =
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    };

    private static readonly double[,] IdctCos = PrecomputeCos();
    private static readonly double InvSqrt2 = 1.0 / Math.Sqrt(2);

    private static double[,] PrecomputeCos()
    {
        var c = new double[8, 8];
        for (int u = 0; u < 8; u++)
        for (int x = 0; x < 8; x++)
        {
            c[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
        }
        return c;
    }

    public static RasterBuffer? TryDecode(byte[] data)
    {
        if (data is null || data.Length < 4) return null;
        // SOI marker
        if (data[0] != 0xFF || data[1] != 0xD8) return null;

        try
        {
            var s = new State();
            int p = 2;

            while (p < data.Length)
            {
                // Skip any 0xFF fill bytes to get to the
                // marker. Per spec a marker may be preceded
                // by any number of 0xFF bytes.
                while (p < data.Length && data[p] == 0xFF) p++;
                if (p >= data.Length) break;
                byte marker = data[p++];

                if (marker == 0xD9) break; // EOI
                if (marker == 0xD8) continue; // spurious SOI

                // Standalone markers without length/payload:
                // RST0..RST7 and TEM.
                if ((marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
                    continue;

                if (p + 2 > data.Length) return null;
                int segLen = (data[p] << 8) | data[p + 1];
                if (segLen < 2 || p + segLen > data.Length) return null;
                int segStart = p + 2;
                int segEnd = p + segLen;

                switch (marker)
                {
                    case 0xC0: // SOF0 — baseline
                        if (!ParseSof0(data, segStart, segEnd, s)) return null;
                        break;

                    // Any other SOFn is a mode we don't
                    // support (progressive, lossless,
                    // differential, arithmetic).
                    case 0xC1: case 0xC2: case 0xC3:
                    case 0xC5: case 0xC6: case 0xC7:
                    case 0xC9: case 0xCA: case 0xCB:
                    case 0xCD: case 0xCE: case 0xCF:
                        return null;

                    case 0xDB: // DQT
                        if (!ParseDqt(data, segStart, segEnd, s)) return null;
                        break;

                    case 0xC4: // DHT
                        if (!ParseDht(data, segStart, segEnd, s)) return null;
                        break;

                    case 0xDD: // DRI
                        if (segEnd - segStart < 2) return null;
                        s.RestartInterval = (data[segStart] << 8) | data[segStart + 1];
                        break;

                    case 0xDA: // SOS — start of scan
                    {
                        if (!ParseSos(data, segStart, segEnd, s)) return null;
                        p = segEnd;

                        // Entropy-coded segment runs from
                        // here until the next non-RST
                        // marker. We forward-scan once to
                        // find the boundary, then hand the
                        // slice to the bit reader (which
                        // knows how to skip any embedded
                        // RSTn markers).
                        int scanStart = p;
                        while (p < data.Length - 1)
                        {
                            if (data[p] == 0xFF && data[p + 1] != 0x00 &&
                                !(data[p + 1] >= 0xD0 && data[p + 1] <= 0xD7))
                            {
                                break;
                            }
                            p++;
                        }
                        int scanLen = p - scanStart;
                        var ecs = new byte[scanLen];
                        Buffer.BlockCopy(data, scanStart, ecs, 0, scanLen);
                        if (!DecodeScan(ecs, s)) return null;
                        // p already at next marker's 0xFF
                        continue;
                    }

                    default:
                        // APP0..APPF, COM, DNL, etc. —
                        // nothing we need for correct pixel
                        // decode.
                        break;
                }

                p = segEnd;
            }

            if (s.Width <= 0 || s.Height <= 0) return null;
            if (s.Components is null || s.ComponentSamples is null) return null;
            return AssembleRgba(s);
        }
        catch
        {
            return null;
        }
    }

    // ── segment parsers ──────────────────────────────────

    private static bool ParseSof0(byte[] data, int start, int end, State s)
    {
        // [precision:1][height:2][width:2][Nf:1]
        // then Nf × [id:1][HV:1][Tq:1]
        if (end - start < 6) return false;
        byte precision = data[start];
        if (precision != 8) return false;
        int height = (data[start + 1] << 8) | data[start + 2];
        int width = (data[start + 3] << 8) | data[start + 4];
        int nf = data[start + 5];
        if (nf != 1 && nf != 3) return false;
        if (end - start < 6 + nf * 3) return false;

        var comps = new Component[nf];
        int maxH = 0, maxV = 0;
        for (int i = 0; i < nf; i++)
        {
            int o = start + 6 + i * 3;
            var c = new Component
            {
                Id = data[o],
                H = (data[o + 1] >> 4) & 0xF,
                V = data[o + 1] & 0xF,
                QtIndex = data[o + 2],
            };
            if (c.H < 1 || c.H > 4 || c.V < 1 || c.V > 4) return false;
            if (c.QtIndex >= 4) return false;
            comps[i] = c;
            if (c.H > maxH) maxH = c.H;
            if (c.V > maxV) maxV = c.V;
        }

        s.Width = width;
        s.Height = height;
        s.Components = comps;
        s.MaxH = maxH;
        s.MaxV = maxV;
        return true;
    }

    private static bool ParseDqt(byte[] data, int start, int end, State s)
    {
        // Segment may hold multiple tables back-to-back.
        // [PqTq:1][values:64 or 128]
        int p = start;
        while (p < end)
        {
            if (p + 1 > end) return false;
            byte pqtq = data[p++];
            int precision = (pqtq >> 4) & 0xF; // 0 = 8-bit, 1 = 16-bit
            int id = pqtq & 0xF;
            if (id >= 4) return false;
            int size = precision == 0 ? 64 : 128;
            if (p + size > end) return false;
            var table = new int[64];
            for (int i = 0; i < 64; i++)
            {
                int v;
                if (precision == 0)
                {
                    v = data[p++];
                }
                else
                {
                    v = (data[p] << 8) | data[p + 1];
                    p += 2;
                }
                // Table entries arrive in zigzag order; we
                // store in natural order so dequantization
                // matches the un-zigzagged coefficient block.
                table[Zigzag[i]] = v;
            }
            s.QTables[id] = table;
        }
        return true;
    }

    private static bool ParseDht(byte[] data, int start, int end, State s)
    {
        // Multiple tables allowed per segment:
        // [TcTh:1][L1..L16:16][values:sum(L)]
        int p = start;
        while (p < end)
        {
            if (p + 17 > end) return false;
            byte tcth = data[p++];
            int klass = (tcth >> 4) & 0xF; // 0 = DC, 1 = AC
            int id = tcth & 0xF;
            if (klass > 1 || id >= 4) return false;

            int[] counts = new int[17]; // 1-indexed; counts[0] unused
            int total = 0;
            for (int i = 1; i <= 16; i++)
            {
                counts[i] = data[p++];
                total += counts[i];
            }
            if (p + total > end) return false;
            var values = new byte[total];
            Buffer.BlockCopy(data, p, values, 0, total);
            p += total;

            var t = BuildHuffmanTable(counts, values);
            if (klass == 0) s.DcTables[id] = t;
            else s.AcTables[id] = t;
        }
        return true;
    }

    private static bool ParseSos(byte[] data, int start, int end, State s)
    {
        // [Ns:1] then Ns × [Cs:1][TdTa:1] then [Ss:1][Se:1][AhAl:1]
        if (end - start < 1) return false;
        int ns = data[start];
        if (ns < 1 || ns > 4) return false;
        if (end - start < 1 + ns * 2 + 3) return false;

        for (int i = 0; i < ns; i++)
        {
            int o = start + 1 + i * 2;
            byte cs = data[o];
            byte tdta = data[o + 1];
            int td = (tdta >> 4) & 0xF;
            int ta = tdta & 0xF;

            // Find component with id == cs and tag its
            // Huffman table indexes for the scan.
            if (s.Components is null) return false;
            Component? target = null;
            foreach (var c in s.Components)
                if (c.Id == cs) { target = c; break; }
            if (target is null) return false;
            target.DcIndex = td;
            target.AcIndex = ta;
            target.InScan = true;
        }

        // We only support single-scan baseline — verify
        // Ss=0, Se=63, Ah=Al=0.
        int ssOff = start + 1 + ns * 2;
        byte ss = data[ssOff];
        byte se = data[ssOff + 1];
        byte ahAl = data[ssOff + 2];
        if (ss != 0 || se != 63 || ahAl != 0) return false;

        return true;
    }

    // ── Huffman table build ──────────────────────────────

    private static HuffmanTable BuildHuffmanTable(int[] counts, byte[] values)
    {
        // JPEG Annex C.2 — generate code lengths and
        // MinCode/MaxCode/ValPtr tables used for the
        // straightforward per-bit decode loop below.
        var t = new HuffmanTable
        {
            Values = values,
            MinCode = new int[17],
            MaxCode = new int[17],
            ValPtr = new int[17],
        };

        int code = 0;
        int k = 0;
        for (int i = 1; i <= 16; i++)
        {
            if (counts[i] == 0)
            {
                t.MaxCode[i] = -1;
            }
            else
            {
                t.ValPtr[i] = k;
                t.MinCode[i] = code;
                code += counts[i] - 1;
                t.MaxCode[i] = code;
                code++;
                k += counts[i];
            }
            // `code` must always shift left for the next
            // length, even when this length had no codes —
            // per JPEG Figure F.14's "Generate codes"
            // algorithm. Forgetting this shift makes every
            // AC table with a gap in its size distribution
            // decode noise.
            code <<= 1;
        }
        return t;
    }

    // ── scan decode ──────────────────────────────────────

    private static bool DecodeScan(byte[] ecs, State s)
    {
        if (s.Components is null) return false;

        int mcusWide = (s.Width + s.MaxH * 8 - 1) / (s.MaxH * 8);
        int mcusTall = (s.Height + s.MaxV * 8 - 1) / (s.MaxV * 8);

        // Allocate sample planes sized to cover whole MCU
        // grid — trimming to (Width, Height) happens at
        // assembly time.
        var samples = new byte[s.Components.Length][];
        var strides = new int[s.Components.Length];
        var compWidths = new int[s.Components.Length];
        var compHeights = new int[s.Components.Length];
        for (int i = 0; i < s.Components.Length; i++)
        {
            var c = s.Components[i];
            strides[i] = mcusWide * c.H * 8;
            int height = mcusTall * c.V * 8;
            compWidths[i] = strides[i];
            compHeights[i] = height;
            samples[i] = new byte[strides[i] * height];
        }

        var reader = new BitReader(ecs);
        int mcuInInterval = 0;

        for (int my = 0; my < mcusTall; my++)
        {
            for (int mx = 0; mx < mcusWide; mx++)
            {
                for (int ci = 0; ci < s.Components.Length; ci++)
                {
                    var c = s.Components[ci];
                    var qt = s.QTables[c.QtIndex];
                    var dcTab = s.DcTables[c.DcIndex];
                    var acTab = s.AcTables[c.AcIndex];
                    if (qt is null || dcTab is null || acTab is null)
                        return false;

                    for (int by = 0; by < c.V; by++)
                    {
                        for (int bx = 0; bx < c.H; bx++)
                        {
                            var block = new int[64];
                            if (!DecodeBlock(reader, dcTab, acTab, qt, c, block))
                                return false;

                            int ox = (mx * c.H + bx) * 8;
                            int oy = (my * c.V + by) * 8;
                            Idct8x8(block, samples[ci], strides[ci], ox, oy);
                        }
                    }
                }

                // Restart interval handling — every N MCUs
                // the encoder flushed the Huffman state,
                // zeroed DC predictors and emitted RSTn.
                // Our BitReader has already swallowed the
                // marker; we just mirror the state reset.
                if (s.RestartInterval > 0)
                {
                    mcuInInterval++;
                    if (mcuInInterval == s.RestartInterval)
                    {
                        reader.AlignToByte();
                        reader.ResetAfterRestart();
                        foreach (var cc in s.Components) cc.PrevDc = 0;
                        mcuInInterval = 0;
                    }
                }
            }
        }

        s.ComponentSamples = samples;
        s.ComponentStrides = strides;
        s.ComponentWidths = compWidths;
        s.ComponentHeights = compHeights;
        return true;
    }

    private static bool DecodeBlock(
        BitReader r, HuffmanTable dc, HuffmanTable ac,
        int[] qt, Component comp, int[] block)
    {
        // DC coefficient: decoded as a differential from
        // the previous same-component DC. Category T says
        // how many bits follow for the magnitude.
        int t = DecodeSymbol(dc, r);
        if (t < 0) return false;
        int diff = t == 0 ? 0 : Extend(r.ReadBits(t), t);
        int dcVal = comp.PrevDc + diff;
        comp.PrevDc = dcVal;
        block[0] = dcVal * qt[0];

        // AC run-length / size pairs. RS byte packs run
        // length of zeros (upper 4) and magnitude size
        // (lower 4). RS==0 is EOB; RS==0xF0 is ZRL (16
        // zeros).
        int k = 1;
        while (k < 64)
        {
            int rs = DecodeSymbol(ac, r);
            if (rs < 0) return false;
            int run = (rs >> 4) & 0xF;
            int size = rs & 0xF;
            if (size == 0)
            {
                if (run != 15) break; // EOB
                k += 16;
                continue;
            }
            k += run;
            if (k >= 64) return false;
            int val = Extend(r.ReadBits(size), size);
            block[Zigzag[k]] = val * qt[Zigzag[k]];
            k++;
        }
        return true;
    }

    private static int DecodeSymbol(HuffmanTable t, BitReader r)
    {
        int code = r.ReadBit();
        for (int i = 1; i <= 16; i++)
        {
            if (code <= t.MaxCode[i] && t.MaxCode[i] >= 0)
            {
                int j = t.ValPtr[i] + (code - t.MinCode[i]);
                if (j < 0 || j >= t.Values.Length) return -1;
                return t.Values[j];
            }
            code = (code << 1) | r.ReadBit();
        }
        return -1;
    }

    /// <summary>Sign-extend a <paramref name="size"/>-bit
    /// category value. For v ≥ 2^(size-1) the value is its
    /// own positive self; otherwise it represents the
    /// negative complement — JPEG spec Annex F.</summary>
    private static int Extend(int v, int size)
    {
        if (size == 0) return 0;
        int vt = 1 << (size - 1);
        if (v < vt) return v + (-1 << size) + 1;
        return v;
    }

    // ── IDCT ─────────────────────────────────────────────

    private static void Idct8x8(int[] block, byte[] output, int stride, int ox, int oy)
    {
        // Separable 1D IDCT: row pass then column pass.
        // Cu/Cv scaling applied during their respective
        // passes; final 1/4 scaling + level shift (+128)
        // at store time.
        var tmp = new double[64];

        // Row pass — for each row (v), produce G(x, v).
        for (int v = 0; v < 8; v++)
        {
            int rowBase = v * 8;
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                for (int u = 0; u < 8; u++)
                {
                    double cu = u == 0 ? InvSqrt2 : 1.0;
                    sum += cu * block[rowBase + u] * IdctCos[u, x];
                }
                tmp[rowBase + x] = sum;
            }
        }

        // Column pass — for each column (x), produce
        // f(x, y) by summing over v using the row-pass
        // intermediate results.
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                double sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    double cv = v == 0 ? InvSqrt2 : 1.0;
                    sum += cv * tmp[v * 8 + x] * IdctCos[v, y];
                }
                int iv = (int)Math.Round(sum * 0.25 + 128.0);
                if (iv < 0) iv = 0;
                else if (iv > 255) iv = 255;
                int idx = (oy + y) * stride + (ox + x);
                if ((uint)idx < (uint)output.Length)
                    output[idx] = (byte)iv;
            }
        }
    }

    // ── assemble → RGBA ──────────────────────────────────

    private static RasterBuffer AssembleRgba(State s)
    {
        var buf = new RasterBuffer(s.Width, s.Height);
        var px = buf.Pixels;
        var comps = s.Components!;
        var samples = s.ComponentSamples!;
        var strides = s.ComponentStrides!;

        if (comps.Length == 1)
        {
            // Grayscale JFIF: single Y plane maps straight
            // to R=G=B=Y, A=255.
            var y = samples[0];
            int ys = strides[0];
            for (int j = 0; j < s.Height; j++)
            {
                for (int i = 0; i < s.Width; i++)
                {
                    byte g = y[j * ys + i];
                    int d = (j * s.Width + i) * 4;
                    px[d] = g;
                    px[d + 1] = g;
                    px[d + 2] = g;
                    px[d + 3] = 255;
                }
            }
            return buf;
        }

        // Three-component JFIF — Y, Cb, Cr. Non-Y planes
        // may be subsampled; nearest-neighbor upsample by
        // scaling the output pixel coordinate back to each
        // plane's resolution (hFactor_c / maxH, etc.).
        int hY = comps[0].H, vY = comps[0].V;
        int hCb = comps[1].H, vCb = comps[1].V;
        int hCr = comps[2].H, vCr = comps[2].V;
        int maxH = s.MaxH, maxV = s.MaxV;
        int sY = strides[0], sCb = strides[1], sCr = strides[2];
        var pY = samples[0];
        var pCb = samples[1];
        var pCr = samples[2];

        for (int j = 0; j < s.Height; j++)
        {
            int yJ = j * vY / maxV;
            int cbJ = j * vCb / maxV;
            int crJ = j * vCr / maxV;
            for (int i = 0; i < s.Width; i++)
            {
                int yI = i * hY / maxH;
                int cbI = i * hCb / maxH;
                int crI = i * hCr / maxH;
                int y  = pY[yJ  * sY  + yI];
                int cb = pCb[cbJ * sCb + cbI] - 128;
                int cr = pCr[crJ * sCr + crI] - 128;
                // BT.601 / JFIF conversion
                int r = y + (int)(1.402 * cr);
                int g = y - (int)(0.34414 * cb + 0.71414 * cr);
                int b = y + (int)(1.772 * cb);
                if (r < 0) r = 0; else if (r > 255) r = 255;
                if (g < 0) g = 0; else if (g > 255) g = 255;
                if (b < 0) b = 0; else if (b > 255) b = 255;
                int d = (j * s.Width + i) * 4;
                px[d] = (byte)r;
                px[d + 1] = (byte)g;
                px[d + 2] = (byte)b;
                px[d + 3] = 255;
            }
        }
        return buf;
    }

    // ── types ────────────────────────────────────────────

    private sealed class State
    {
        public int Width;
        public int Height;
        public int MaxH;
        public int MaxV;
        public int RestartInterval;
        public Component[]? Components;
        public int[]?[] QTables = new int[4][];
        public HuffmanTable?[] DcTables = new HuffmanTable[4];
        public HuffmanTable?[] AcTables = new HuffmanTable[4];
        public byte[][]? ComponentSamples;
        public int[]? ComponentStrides;
        public int[]? ComponentWidths;
        public int[]? ComponentHeights;
    }

    private sealed class Component
    {
        public int Id;
        public int H;
        public int V;
        public int QtIndex;
        public int DcIndex;
        public int AcIndex;
        public bool InScan;
        public int PrevDc;
    }

    private sealed class HuffmanTable
    {
        public byte[] Values = Array.Empty<byte>();
        public int[] MinCode = new int[17];
        public int[] MaxCode = new int[17];
        public int[] ValPtr = new int[17];
    }

    /// <summary>Bit-level reader over a JPEG entropy-coded
    /// segment. Reads MSB-first out of each byte, skipping
    /// the 0x00 byte that follows any real 0xFF value (byte
    /// stuffing). Embedded RSTn markers are silently skipped
    /// — the decoder handles DC-reset timing separately on
    /// the MCU boundary.</summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _pos;
        private int _bits;
        private int _bitCount;
        public bool EndOfStream { get; private set; }

        public BitReader(byte[] data) { _data = data; }

        public int ReadBit()
        {
            if (_bitCount == 0)
            {
                Refill();
                if (EndOfStream) return 0;
            }
            _bitCount--;
            return (_bits >> _bitCount) & 1;
        }

        public int ReadBits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v = (v << 1) | ReadBit();
            return v;
        }

        public void AlignToByte()
        {
            _bitCount = 0;
        }

        public void ResetAfterRestart()
        {
            // No buffered bits after AlignToByte; nothing
            // else to reset — marker skipping already
            // happens lazily in Refill.
            _bitCount = 0;
        }

        private void Refill()
        {
            while (true)
            {
                if (_pos >= _data.Length) { EndOfStream = true; return; }
                byte b = _data[_pos++];
                if (b != 0xFF)
                {
                    _bits = b;
                    _bitCount = 8;
                    return;
                }
                if (_pos >= _data.Length) { EndOfStream = true; return; }
                byte next = _data[_pos++];
                if (next == 0x00)
                {
                    _bits = 0xFF;
                    _bitCount = 8;
                    return;
                }
                if (next >= 0xD0 && next <= 0xD7)
                {
                    // Embedded restart marker — swallow and
                    // keep reading.
                    continue;
                }
                // Any other marker means the entropy stream
                // ended — back up so the outer parser sees
                // the marker.
                _pos -= 2;
                EndOfStream = true;
                return;
            }
        }
    }
}
