namespace Daisi.Broski.Engine.Dom;

/// <summary>
/// DOM Level 4 <c>nodeType</c> constants. The numeric values match the
/// values JavaScript code reads from <c>Node.ELEMENT_NODE</c> et al. —
/// we expose the same numbers so that when the JS engine wraps these
/// host objects in phase 3, script-visible behavior matches the spec.
/// </summary>
public enum NodeType
{
    Element = 1,
    Attribute = 2,          // Attr nodes exist conceptually; we don't model them separately in phase 1
    Text = 3,
    CDataSection = 4,       // deferred
    ProcessingInstruction = 7,
    Comment = 8,
    Document = 9,
    DocumentType = 10,
    DocumentFragment = 11,
}
