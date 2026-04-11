namespace Daisi.Broski.Engine.Dom;

/// <summary>
/// <c>&lt;!-- ... --&gt;</c> comment node.
/// </summary>
public sealed class Comment : Node
{
    public override NodeType NodeType => NodeType.Comment;

    public override string NodeName => "#comment";

    public string Data { get; set; }

    public override string TextContent => Data;

    internal Comment(string data)
    {
        Data = data;
    }
}
