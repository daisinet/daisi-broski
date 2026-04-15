namespace Daisi.Broski.Docs.Xlsx;

/// <summary>
/// Spreadsheetml (<c>.xlsx</c>) → article HTML converter. Produces
/// one <c>&lt;h2&gt;{sheet name}&lt;/h2&gt;</c> + <c>&lt;table&gt;</c>
/// per worksheet, all wrapped in a single <c>&lt;article&gt;</c>.
///
/// <para>Numbers, booleans, and shared/inline strings render as
/// text in their cell; dates (cells whose style points at a date
/// numFmt) render as <c>yyyy-MM-dd</c>. Merged ranges render as a
/// single cell with <c>colspan</c>/<c>rowspan</c>; the subsumed
/// cells are elided so the table stays rectangular.</para>
/// </summary>
internal sealed class XlsxConverter : IDocConverter
{
    public string Convert(byte[] body, Uri sourceUrl)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(sourceUrl);
        using var package = Docx.OpcPackage.Open(body);
        var strings = SharedStringsReader.Load(package);
        var styles = StylesReader.Load(package);
        var sheets = WorkbookReader.Load(package);
        var core = Docx.CorePropertiesReader.Load(package);
        var rendered = new List<RenderedSheet>(sheets.Count);
        foreach (var sheet in sheets)
        {
            var worksheet = WorksheetReader.Load(package, sheet.PartPath, strings, styles);
            rendered.Add(new RenderedSheet(sheet.Name, worksheet));
        }
        return XlsxHtmlEmitter.Render(rendered, core, sourceUrl);
    }
}
