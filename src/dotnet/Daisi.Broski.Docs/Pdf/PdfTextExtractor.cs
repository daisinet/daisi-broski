using System.Text;

namespace Daisi.Broski.Docs.Pdf;

/// <summary>A single text-show event from a content stream, with
/// the PDF user-space coordinates of its baseline left edge. The
/// layout analyzer clusters runs by <see cref="Y"/> into rows and
/// by <see cref="X"/> into columns to detect tables.
/// <see cref="Href"/> is non-null when the run falls inside the
/// rect of a <c>/Link</c> annotation with a URI action — the
/// converter wraps these in <c>&lt;a&gt;</c> so markdown output
/// gets <c>[text](url)</c>.</summary>
internal readonly record struct PdfTextRun(
    double X, double Y, string Text, string? Href = null);

/// <summary>
/// Walks a decoded content stream and pulls out every text-show
/// operator's payload, capturing the text's position so the
/// downstream layout analyzer can reconstruct rows / tables.
/// Recognized operators: <c>BT</c>/<c>ET</c>, <c>Tf</c>,
/// <c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c>, <c>Td</c>/<c>TD</c>/
/// <c>Tm</c>/<c>T*</c>, <c>TL</c>, plus the graphics-state
/// no-ops (<c>q</c>/<c>Q</c>/<c>cm</c>).
///
/// <para>Text matrix tracking is approximate: we maintain only
/// the translation components (e, f) of the text matrix and the
/// line matrix (Tlm). The full a/b/c/d transform isn't needed for
/// text-position clustering — every real producer keeps text
/// upright with no shear, and our tolerance buckets absorb the
/// occasional off-axis run. Glyph-width advance after Tj/TJ is
/// not modeled (we'd need font metrics); subsequent operators
/// always re-position via Tm/Td anyway.</para>
/// </summary>
internal static class PdfTextExtractor
{
    /// <summary>Backwards-compat: extract a flat string by
    /// joining positioned runs in content-stream order, with
    /// newlines between runs whose y-coordinate dropped (matches
    /// the milestone-2 behavior).</summary>
    public static string Extract(
        byte[] contentStream, Func<string, PdfFont?> fontResolver)
    {
        var runs = ExtractRuns(contentStream, fontResolver);
        if (runs.Count == 0) return "";
        var sb = new StringBuilder();
        double? lastY = null;
        for (int i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            if (lastY is double y && Math.Abs(r.Y - y) > 0.5)
            {
                sb.Append('\n');
            }
            sb.Append(r.Text);
            lastY = r.Y;
        }
        return sb.ToString();
    }

    /// <summary>Extract positioned text runs. Each <see cref="Tj"/>,
    /// <see cref="TJ"/>, <c>'</c> or <c>"</c> operator yields one
    /// run carrying the (x, y) of its baseline-left in PDF
    /// user-space units.</summary>
    public static IReadOnlyList<PdfTextRun> ExtractRuns(
        byte[] contentStream, Func<string, PdfFont?> fontResolver)
    {
        var lexer = new PdfLexer(contentStream);
        var parser = new PdfParser(lexer);
        var operands = new List<PdfObject>();
        var state = new TextState();
        var runs = new List<PdfTextRun>();
        while (true)
        {
            var token = lexer.PeekToken();
            if (token is null) break;
            if (token.Kind == PdfTokenKind.Keyword)
            {
                lexer.NextToken();
                HandleOperator(token.Text, operands, fontResolver, state, runs);
                operands.Clear();
                continue;
            }
            var obj = parser.ReadObject();
            if (obj is PdfOperator op)
            {
                HandleOperator(op.Name, operands, fontResolver, state, runs);
                operands.Clear();
                continue;
            }
            operands.Add(obj);
        }
        return runs;
    }

    private static void HandleOperator(
        string op, List<PdfObject> operands,
        Func<string, PdfFont?> fontResolver,
        TextState state, List<PdfTextRun> runs)
    {
        switch (op)
        {
            case "BT":
                // Begin text — reset text matrix (Tm) and text
                // line matrix (Tlm) to identity. Per spec §9.4.1,
                // Tm and Tlm are undefined outside a text object.
                state.Tm = state.Tlm = Matrix.Identity;
                break;
            case "ET":
                break;
            case "Tf":
                if (operands.Count >= 2 && operands[0] is PdfName fontName)
                {
                    state.Font = fontResolver(fontName.Value);
                }
                if (operands.Count >= 2 && AsDouble(operands[1]) is double sz)
                {
                    state.FontSize = sz;
                }
                break;
            case "TL":
                if (operands.Count >= 1 && AsDouble(operands[0]) is double L)
                {
                    state.Leading = L;
                }
                break;
            case "Tj":
                if (operands.Count >= 1 && operands[0] is PdfString s)
                {
                    EmitRun(state, s.Bytes, runs);
                }
                break;
            case "TJ":
                if (operands.Count >= 1 && operands[0] is PdfArray arr)
                {
                    EmitTextArrayRun(state, arr, runs);
                }
                break;
            case "'":
                AdvanceLine(state);
                if (operands.Count >= 1 && operands[0] is PdfString s1)
                {
                    EmitRun(state, s1.Bytes, runs);
                }
                break;
            case "\"":
                AdvanceLine(state);
                if (operands.Count >= 3 && operands[2] is PdfString s2)
                {
                    EmitRun(state, s2.Bytes, runs);
                }
                break;
            case "Td":
                if (operands.Count >= 2
                    && AsDouble(operands[0]) is double tdx
                    && AsDouble(operands[1]) is double tdy)
                {
                    // Td: Tlm' = [1 0 0 1 tx ty] × Tlm. Then Tm = Tlm'.
                    var translate = new Matrix(1, 0, 0, 1, tdx, tdy);
                    state.Tlm = MultiplyMatrix(translate, state.Tlm);
                    state.Tm = state.Tlm;
                }
                break;
            case "TD":
                if (operands.Count >= 2
                    && AsDouble(operands[0]) is double tdx2
                    && AsDouble(operands[1]) is double tdy2)
                {
                    state.Leading = -tdy2;
                    var translate = new Matrix(1, 0, 0, 1, tdx2, tdy2);
                    state.Tlm = MultiplyMatrix(translate, state.Tlm);
                    state.Tm = state.Tlm;
                }
                break;
            case "T*":
                AdvanceLine(state);
                break;
            case "Tm":
                if (operands.Count >= 6
                    && AsDouble(operands[0]) is double ma
                    && AsDouble(operands[1]) is double mb
                    && AsDouble(operands[2]) is double mc
                    && AsDouble(operands[3]) is double md
                    && AsDouble(operands[4]) is double me
                    && AsDouble(operands[5]) is double mf)
                {
                    state.Tm = state.Tlm = new Matrix(ma, mb, mc, md, me, mf);
                }
                break;
            case "q":
                state.GraphicsStack.Push(state.Ctm);
                break;
            case "Q":
                if (state.GraphicsStack.Count > 0)
                    state.Ctm = state.GraphicsStack.Pop();
                break;
            case "cm":
                if (operands.Count >= 6
                    && AsDouble(operands[0]) is double a
                    && AsDouble(operands[1]) is double b
                    && AsDouble(operands[2]) is double c
                    && AsDouble(operands[3]) is double d
                    && AsDouble(operands[4]) is double e
                    && AsDouble(operands[5]) is double f)
                {
                    // CTM' = CTM * [a b 0; c d 0; e f 1]. We
                    // store CTM as a 6-tuple in PDF row-major:
                    // (Ma, Mb, Mc, Md, Me, Mf). The composed
                    // transform places new-frame coords through
                    // [a b c d e f] then through the previous CTM.
                    state.Ctm = MultiplyMatrix(
                        new Matrix(a, b, c, d, e, f), state.Ctm);
                }
                break;
            // Text-state operators we don't model spatially.
            case "Tc": case "Tw": case "Tz": case "Ts": case "Tr":
                break;
            default:
                break;
        }
    }

    /// <summary>Compose two PDF affine matrices: the result is
    /// what you get from applying <paramref name="x"/> first and
    /// then <paramref name="y"/>.</summary>
    private static Matrix MultiplyMatrix(Matrix x, Matrix y)
        => new(
            x.A * y.A + x.B * y.C,
            x.A * y.B + x.B * y.D,
            x.C * y.A + x.D * y.C,
            x.C * y.B + x.D * y.D,
            x.E * y.A + x.F * y.C + y.E,
            x.E * y.B + x.F * y.D + y.F);

    /// <summary>Transform a (text-space) point through the
    /// current CTM into user-space, returning (x, y).</summary>
    private static (double X, double Y) Transform(
        Matrix m, double x, double y)
        => (m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F);

    private readonly record struct Matrix(
        double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
    }

    private static void AdvanceLine(TextState s)
    {
        double advance = s.Leading > 0 ? s.Leading : s.FontSize;
        // T* = 0 -L Td.
        var translate = new Matrix(1, 0, 0, 1, 0, -advance);
        s.Tlm = MultiplyMatrix(translate, s.Tlm);
        s.Tm = s.Tlm;
    }

    private static void EmitRun(
        TextState state, byte[] bytes, List<PdfTextRun> runs)
    {
        string text = state.Font?.Decode(bytes) ?? "";
        if (text.Length > 0)
        {
            // The run's user-space position comes from composing
            // the current text matrix with the CTM and reading
            // off the translation — that is, Tm × CTM × (0, 0).
            var combined = MultiplyMatrix(state.Tm, state.Ctm);
            runs.Add(new PdfTextRun(combined.E, combined.F, text));
        }
        // After the show operator, post-multiply Tm by
        // [1 0 0 1 tx 0] where tx = (width × fontSize) / 1000.
        // The advance is in text-space before Tm scales it.
        double glyph = state.Font?.MeasureAdvance(bytes) ?? 0;
        double tx = glyph * state.FontSize / 1000.0;
        var advance = new Matrix(1, 0, 0, 1, tx, 0);
        state.Tm = MultiplyMatrix(advance, state.Tm);
    }

    private static void EmitTextArrayRun(
        TextState state, PdfArray arr, List<PdfTextRun> runs)
    {
        // Concatenate the array's strings into one run at the
        // starting text-matrix position, advancing the matrix
        // through each string and each kerning offset so subsequent
        // operators land at the correct x. TJ numeric offsets
        // translate Tm by -offset × fontSize / 1000 (the spec's
        // negative-right convention).
        var sb = new StringBuilder();
        var combinedStart = MultiplyMatrix(state.Tm, state.Ctm);
        double startUx = combinedStart.E, startUy = combinedStart.F;
        foreach (var item in arr.Items)
        {
            switch (item)
            {
                case PdfString s:
                    sb.Append(state.Font?.Decode(s.Bytes) ?? "");
                    double g = state.Font?.MeasureAdvance(s.Bytes) ?? 0;
                    double tx = g * state.FontSize / 1000.0;
                    state.Tm = MultiplyMatrix(
                        new Matrix(1, 0, 0, 1, tx, 0), state.Tm);
                    break;
                case PdfInt or PdfReal:
                    double v = item is PdfInt i ? i.Value
                        : ((PdfReal)item).Value;
                    double offset = -v * state.FontSize / 1000.0;
                    state.Tm = MultiplyMatrix(
                        new Matrix(1, 0, 0, 1, offset, 0), state.Tm);
                    if (v <= -150 && sb.Length > 0
                        && sb[^1] != ' ' && sb[^1] != '\n')
                    {
                        sb.Append(' ');
                    }
                    break;
            }
        }
        if (sb.Length > 0)
        {
            runs.Add(new PdfTextRun(startUx, startUy, sb.ToString()));
        }
    }

    private static double? AsDouble(PdfObject obj) => obj switch
    {
        PdfInt i => i.Value,
        PdfReal r => r.Value,
        _ => null,
    };

    /// <summary>Mutable text-state container the operator handler
    /// updates in place. Allocated once per content stream so we
    /// don't pay for ref-juggling on every operator. The CTM
    /// (current transformation matrix) and graphics-state stack
    /// live alongside text-state because they're updated by the
    /// same operator dispatch — keeping the two in one struct
    /// avoids threading two refs through every helper.</summary>
    private sealed class TextState
    {
        public PdfFont? Font;
        public double FontSize = 12;
        public double Leading;
        // Full text matrix and text line matrix per spec §9.4.1.
        // Position of the next glyph is Tm × (0, 0); line-break
        // operators operate on Tlm and copy to Tm.
        public Matrix Tm = Matrix.Identity;
        public Matrix Tlm = Matrix.Identity;
        public Matrix Ctm = Matrix.Identity;
        public Stack<Matrix> GraphicsStack = new();
    }
}
