namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Wordprocessingml (<c>.docx</c>) → article HTML converter.
///
/// <para>Wraps the OOXML package reader
/// (<see cref="OpcPackage"/>), style table
/// (<see cref="DocxStyles"/>), list-numbering resolver
/// (<see cref="DocxNumbering"/>), relationship table
/// (<see cref="DocxRelationships"/>), and the body reader
/// (<see cref="DocxBodyReader"/>) into a single entry point. The
/// output is a full <c>&lt;!doctype html&gt;</c> document whose body
/// contains a single <c>&lt;article&gt;</c> matching what the
/// article extractor expects — ContentExtractor.Extract will pick
/// that article as the content root with no further heuristics
/// involved.</para>
/// </summary>
internal sealed class DocxConverter : IDocConverter
{
    public string Convert(byte[] body, Uri sourceUrl)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(sourceUrl);
        using var package = OpcPackage.Open(body);
        var styles = DocxStyles.Load(package);
        var numbering = DocxNumbering.Load(package);
        var relationships = DocxRelationships.Load(package, package.MainDocumentPath);
        var core = CorePropertiesReader.Load(package);
        var blocks = DocxBodyReader.Read(package, styles, numbering, relationships);
        return DocxHtmlEmitter.Render(blocks, core, sourceUrl);
    }
}
