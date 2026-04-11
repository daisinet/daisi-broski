namespace Daisi.Broski.Engine.Html;

/// <summary>
/// Token emitted by <see cref="Tokenizer"/>. One of
/// <see cref="StartTagToken"/>, <see cref="EndTagToken"/>,
/// <see cref="CharacterToken"/>, <see cref="CommentToken"/>,
/// <see cref="DoctypeToken"/>, or <see cref="EndOfFileToken"/>.
///
/// These are plain classes rather than a struct-based tagged union
/// for now. The tree builder (arriving shortly) holds the hot path;
/// the tokenizer is not allocation-sensitive in phase 1.
/// </summary>
public abstract class HtmlToken
{
    internal HtmlToken() { }
}

/// <summary>Start tag, e.g. <c>&lt;div id="x"&gt;</c> or <c>&lt;br/&gt;</c>.</summary>
public sealed class StartTagToken : HtmlToken
{
    public required string Name { get; init; }
    public required IReadOnlyList<HtmlAttribute> Attributes { get; init; }
    public required bool SelfClosing { get; init; }
}

/// <summary>End tag, e.g. <c>&lt;/div&gt;</c>. Attributes on end tags
/// are parsed for recovery but ignored here.</summary>
public sealed class EndTagToken : HtmlToken
{
    public required string Name { get; init; }
}

/// <summary>A run of text from the document's character data stream.
/// The tokenizer batches consecutive characters in the data state into
/// a single token to avoid allocation storms.</summary>
public sealed class CharacterToken : HtmlToken
{
    public required string Data { get; init; }
}

/// <summary><c>&lt;!-- ... --&gt;</c> comment contents.</summary>
public sealed class CommentToken : HtmlToken
{
    public required string Data { get; init; }
}

/// <summary><c>&lt;!DOCTYPE ...&gt;</c>. Phase 1 only captures the
/// name; public identifier, system identifier, and quirks-mode flag
/// are recorded by the tree builder based on the name.</summary>
public sealed class DoctypeToken : HtmlToken
{
    public string? Name { get; init; }
}

/// <summary>Signals end of input. Emitted exactly once.</summary>
public sealed class EndOfFileToken : HtmlToken
{
    public static readonly EndOfFileToken Instance = new();
    private EndOfFileToken() { }
}

/// <summary>An <c>name="value"</c> pair on a start tag. Unquoted and
/// single-quoted forms are normalized to the same shape here.</summary>
public sealed record HtmlAttribute(string Name, string Value);
