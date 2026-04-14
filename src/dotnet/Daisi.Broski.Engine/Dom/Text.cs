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

    private string _data;

    public string Data
    {
        get => _data;
        set
        {
            var old = _data;
            _data = value ?? "";
            NotifyCharacterDataMutation(old);
        }
    }

    public override string TextContent => _data;

    public int Length => _data.Length;

    internal Text(string data)
    {
        _data = data ?? "";
    }

    /// <summary>Append to the existing data. Faster than replacing the
    /// whole string when the tree builder is concatenating runs of
    /// character tokens.</summary>
    public void AppendData(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        var old = _data;
        _data += chunk;
        NotifyCharacterDataMutation(old);
    }

    private void NotifyCharacterDataMutation(string oldValue)
    {
        if (OwnerDocument is { HasMutationObservers: true } doc)
        {
            doc.MutationDispatcher.NotifyCharacterData(this, oldValue);
        }
    }
}
