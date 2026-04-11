namespace Daisi.Broski.Engine.Dom;

/// <summary>
/// <c>&lt;!DOCTYPE ...&gt;</c> node. Phase 1 only records the name;
/// public and system identifiers are deferred with the rest of the
/// tokenizer's DOCTYPE extensions.
/// </summary>
public sealed class DocumentType : Node
{
    public override NodeType NodeType => NodeType.DocumentType;

    public override string NodeName => Name;

    public string Name { get; }

    internal DocumentType(string name)
    {
        Name = name;
    }
}
