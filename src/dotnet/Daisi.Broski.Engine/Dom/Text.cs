namespace Daisi.Broski.Engine.Dom;

/// <summary>
/// Text node. Stores a mutable <see cref="Data"/> string. The tree
/// builder merges consecutive character tokens into a single
/// <see cref="Text"/> node by appending to this field rather than
/// creating a new node per token.
/// </summary>
public sealed class Text : Node
{
    public override NodeType NodeType => NodeType.Text;

    public override string NodeName => "#text";

    public string Data { get; set; }

    public override string TextContent => Data;

    public int Length => Data.Length;

    internal Text(string data)
    {
        Data = data;
    }

    /// <summary>Append to the existing data. Faster than replacing the
    /// whole string when the tree builder is concatenating runs of
    /// character tokens.</summary>
    public void AppendData(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        Data += chunk;
    }
}
