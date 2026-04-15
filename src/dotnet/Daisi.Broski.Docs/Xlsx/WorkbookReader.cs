using System.Xml;
using Daisi.Broski.Docs.Docx;

namespace Daisi.Broski.Docs.Xlsx;

/// <summary>
/// Reads <c>xl/workbook.xml</c> + the accompanying workbook
/// relationships part to produce an ordered list of
/// <c>(sheet name, worksheet part path)</c>. The relationships
/// indirection is load-bearing: the worksheet path inside the ZIP
/// isn't in the workbook XML itself — that file only carries
/// <c>r:id="rId3"</c> and we have to look the target up separately.
/// </summary>
internal static class WorkbookReader
{
    internal sealed record SheetRef(string Name, string PartPath);

    internal static IReadOnlyList<SheetRef> Load(OpcPackage package)
    {
        var workbookPath = package.MainDocumentPath;
        var relationships = DocxRelationships.Load(package, workbookPath);

        var result = new List<SheetRef>();
        using var stream = package.OpenPart(workbookPath);
        using var xr = XmlReader.Create(stream, OpcPackage.NewReaderSettings());
        while (xr.Read())
        {
            if (xr.NodeType != XmlNodeType.Element) continue;
            if (xr.LocalName != "sheet") continue;
            var name = xr.GetAttribute("name") ?? "Sheet";
            var rid = xr.GetAttribute("id",
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            if (string.IsNullOrEmpty(rid)) continue;
            var rel = relationships.GetById(rid);
            if (rel is null) continue;
            var target = relationships.Resolve(rel.Value);
            result.Add(new SheetRef(name, target));
        }
        return result;
    }
}
