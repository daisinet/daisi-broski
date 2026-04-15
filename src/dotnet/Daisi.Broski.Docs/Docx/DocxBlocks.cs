namespace Daisi.Broski.Docs.Docx;

/// <summary>
/// Internal block / inline model the body reader populates and the
/// HTML emitter renders. Kept deliberately small — each block is
/// either a heading, a paragraph, a list item, or a table, and each
/// inline is either text or a hyperlink. Everything the rest of the
/// docx format can express (fields, shapes, SmartArt, footnotes)
/// degrades to plain text in this pipeline.
/// </summary>
internal abstract record DocxBlock;

internal sealed record DocxHeading(int Level, IReadOnlyList<DocxInline> Inlines)
    : DocxBlock;

internal sealed record DocxParagraph(IReadOnlyList<DocxInline> Inlines) : DocxBlock;

internal sealed record DocxListItem(
    bool Ordered, int Level, IReadOnlyList<DocxInline> Inlines) : DocxBlock;

internal sealed record DocxTable(IReadOnlyList<DocxTableRow> Rows) : DocxBlock;

internal sealed record DocxTableRow(IReadOnlyList<DocxTableCell> Cells);

internal sealed record DocxTableCell(IReadOnlyList<DocxBlock> Blocks);

internal abstract record DocxInline;

internal sealed record DocxTextRun(
    string Text, bool Bold, bool Italic, bool Underline) : DocxInline;

internal sealed record DocxHyperlink(
    string Href, IReadOnlyList<DocxInline> Inlines) : DocxInline;

internal sealed record DocxLineBreak : DocxInline;
