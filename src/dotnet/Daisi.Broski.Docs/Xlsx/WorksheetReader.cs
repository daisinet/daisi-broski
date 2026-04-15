using System.Globalization;
using System.Xml;
using Daisi.Broski.Docs.Docx;

namespace Daisi.Broski.Docs.Xlsx;

/// <summary>
/// Streams a single <c>xl/worksheets/sheet{n}.xml</c> into a
/// <see cref="Worksheet"/>. The xlsx schema's sheetData is a flat
/// list of <c>&lt;row&gt;</c> elements; each row carries its cells
/// by <c>r="A1"</c> reference. Cells missing from the stream
/// represent blank spots — we pad the rendered row to the widest
/// column seen in the sheet so the HTML table stays rectangular.
/// </summary>
internal static class WorksheetReader
{
    internal static Worksheet Load(
        OpcPackage package, string partPath,
        IReadOnlyList<string> sharedStrings, XlsxStyles styles)
    {
        using var stream = package.OpenPart(partPath);
        using var xr = XmlReader.Create(stream, OpcPackage.NewReaderSettings());

        var rows = new List<List<(int col, string value)>>();
        var merges = new List<MergedRange>();
        int maxCol = 0;
        int maxRow = 0;

        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.LocalName == "row")
            {
                var (rowIdx, cells, rowMax) = ReadRow(xr, sharedStrings, styles);
                if (rowIdx >= 0)
                {
                    while (rows.Count <= rowIdx) rows.Add(new());
                    rows[rowIdx] = cells;
                    if (rowMax > maxCol) maxCol = rowMax;
                    if (rowIdx > maxRow) maxRow = rowIdx;
                }
            }
            else if (xr.LocalName == "mergeCell")
            {
                var rangeAttr = xr.GetAttribute("ref");
                var parsed = MergedRange.Parse(rangeAttr);
                if (parsed is not null) merges.Add(parsed);
            }
        }

        // Flatten sparse rows into a padded grid.
        int width = maxCol + 1;
        int height = rows.Count;
        var dense = new List<IReadOnlyList<string>>(height);
        for (int r = 0; r < height; r++)
        {
            var row = new string[width];
            foreach (var (c, v) in rows[r])
            {
                if (c >= 0 && c < width) row[c] = v;
            }
            for (int c = 0; c < width; c++) row[c] ??= "";
            dense.Add(row);
        }
        return new Worksheet(dense, merges);
    }

    private static (int rowIdx, List<(int col, string value)> cells, int maxCol)
        ReadRow(XmlReader xr, IReadOnlyList<string> sharedStrings,
                XlsxStyles styles)
    {
        var rowRefAttr = xr.GetAttribute("r");
        int rowIdx = int.TryParse(rowRefAttr, out int r1) ? r1 - 1 : -1;
        var cells = new List<(int col, string value)>();
        int maxCol = -1;
        if (xr.IsEmptyElement) return (rowIdx, cells, maxCol);
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.LocalName != "c") continue;
            var (col, value) = ReadCell(xr, sharedStrings, styles, rowIdx);
            if (col < 0) continue;
            cells.Add((col, value));
            if (col > maxCol) maxCol = col;
        }
        return (rowIdx, cells, maxCol);
    }

    private static (int col, string value) ReadCell(
        XmlReader xr, IReadOnlyList<string> sharedStrings,
        XlsxStyles styles, int rowIdx)
    {
        var refAttr = xr.GetAttribute("r");
        int col = -1;
        if (!string.IsNullOrEmpty(refAttr)
            && MergedRange.TryParseRef(refAttr, out _, out int c)) col = c;

        string type = xr.GetAttribute("t") ?? "n";
        var styleAttr = xr.GetAttribute("s");
        int styleIdx = int.TryParse(styleAttr, out int si) ? si : -1;

        string value = "";
        if (xr.IsEmptyElement) return (col, "");
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            switch (xr.LocalName)
            {
                case "v":
                    // ReadInnerText leaves the reader on </v>'s
                    // EndElement so the outer loop's next Read()
                    // lands on the next cell child (either <f> or
                    // </c>) instead of skipping one.
                    value = xr.ReadInnerText();
                    break;
                case "is":
                    value = ReadInlineString(xr);
                    break;
                case "f":
                    // Formula — cached value lives in <v>, which is
                    // either sibling. Consume the formula's content
                    // via ReadInnerText so the reader stops on the
                    // </f> EndElement; xr.Skip() here would advance
                    // past it and make the outer loop skip <v>.
                    _ = xr.ReadInnerText();
                    break;
            }
        }
        return (col, FormatValue(value, type, styleIdx, sharedStrings, styles));
    }

    private static string ReadInlineString(XmlReader xr)
    {
        if (xr.IsEmptyElement) return "";
        var sb = new System.Text.StringBuilder();
        int depth = xr.Depth;
        while (xr.Read() && xr.Depth > depth)
        {
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "t")
            {
                sb.Append(xr.ReadInnerText());
            }
        }
        return sb.ToString();
    }

    /// <summary>Resolve a cell's rendered text given its xlsx type
    /// attribute, raw value, and style index.</summary>
    private static string FormatValue(
        string rawValue, string type, int styleIdx,
        IReadOnlyList<string> sharedStrings, XlsxStyles styles)
    {
        switch (type)
        {
            case "s":
                if (int.TryParse(rawValue, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int idx)
                    && idx >= 0 && idx < sharedStrings.Count)
                    return sharedStrings[idx];
                return "";
            case "str":
            case "inlineStr":
                return rawValue;
            case "b":
                return rawValue == "1" ? "TRUE" : "FALSE";
            case "e":
                return rawValue; // error text (e.g., "#N/A")
            case "n":
            case "":
                if (styleIdx >= 0 && styles.IsDateStyle(styleIdx)
                    && double.TryParse(rawValue, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double serial))
                {
                    return FormatSerialDate(serial);
                }
                return rawValue;
            default:
                return rawValue;
        }
    }

    /// <summary>Render an xlsx date serial as <c>yyyy-MM-dd</c>, or
    /// <c>yyyy-MM-dd HH:mm</c> when the value has a fractional
    /// (time) part. Uses the default 1900 epoch with Excel's leap-
    /// year bug (1900 treated as leap) — the 1904 epoch variant is
    /// rare enough to skip.</summary>
    private static string FormatSerialDate(double serial)
    {
        // DateTime.FromOADate matches Excel's 1900 convention.
        DateTime dt;
        try { dt = DateTime.FromOADate(serial); }
        catch (ArgumentException) { return serial.ToString(CultureInfo.InvariantCulture); }
        if (Math.Abs(serial - Math.Floor(serial)) < 1e-9)
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
