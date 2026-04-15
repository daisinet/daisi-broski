using System.Globalization;

namespace Daisi.Broski.Docs.Xlsx;

/// <summary>
/// In-memory shape the worksheet reader hands to the HTML emitter.
/// One row is a list of rendered cell strings (already formatted
/// against the style table); ragged rows (real-world sheets often
/// have blank gaps) are padded to the max column in the sheet so
/// the emitted table is rectangular.
/// </summary>
internal sealed record Worksheet(
    IReadOnlyList<IReadOnlyList<string>> Rows,
    IReadOnlyList<MergedRange> Merges);

/// <summary>A rectangular merge in
/// (rowStart, colStart, rowEnd, colEnd) form — all inclusive,
/// zero-based. The renderer skips subsumed cells and emits a single
/// cell spanning the range at (rowStart, colStart).</summary>
internal sealed record MergedRange(
    int RowStart, int ColStart, int RowEnd, int ColEnd)
{
    /// <summary>Parse an A1-range string (<c>"B2:D4"</c>) into the
    /// four zero-based indices. Returns null on malformed input —
    /// the caller drops that merge rather than fail the whole
    /// sheet.</summary>
    internal static MergedRange? Parse(string? a1Range)
    {
        if (string.IsNullOrEmpty(a1Range)) return null;
        int colon = a1Range.IndexOf(':');
        if (colon < 0) return null;
        if (!TryParseRef(a1Range[..colon], out int r1, out int c1)) return null;
        if (!TryParseRef(a1Range[(colon + 1)..], out int r2, out int c2)) return null;
        return new MergedRange(
            Math.Min(r1, r2), Math.Min(c1, c2),
            Math.Max(r1, r2), Math.Max(c1, c2));
    }

    internal static bool TryParseRef(string cellRef, out int row, out int col)
    {
        row = 0; col = 0;
        if (string.IsNullOrEmpty(cellRef)) return false;
        int i = 0;
        int colIdx = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i]))
        {
            colIdx = colIdx * 26 + (char.ToUpperInvariant(cellRef[i]) - 'A' + 1);
            i++;
        }
        if (i == 0 || colIdx == 0) return false;
        if (!int.TryParse(cellRef.AsSpan(i), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int rowIdx)
            || rowIdx < 1) return false;
        row = rowIdx - 1;
        col = colIdx - 1;
        return true;
    }
}

/// <summary>One row's view shape — a fixed-width array of strings.
/// Kept as a separate record so the emitter can branch on
/// null-vs-empty: null means "this cell is subsumed by a merge
/// anchored elsewhere" and should be omitted entirely.</summary>
internal readonly record struct RenderedSheet(string Name, Worksheet Worksheet);
