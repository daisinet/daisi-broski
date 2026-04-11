namespace Daisi.Broski.Engine.Dom.Selectors;

/// <summary>
/// Parsed representation of a CSS selector list (e.g. <c>"a, div > .foo"</c>).
/// Matches any element the browser standard says the selector list matches.
///
/// AST shape (simplified CSS Selectors Level 4 grammar):
///
/// <code>
///   SelectorList    = ComplexSelector ("," ComplexSelector)*
///   ComplexSelector = CompoundSelector (Combinator CompoundSelector)*
///   CompoundSelector= SimpleSelector+
///   SimpleSelector  = Type | Universal | Id | Class | Attribute | PseudoClass
/// </code>
///
/// Matching is performed right-to-left by
/// <see cref="SelectorMatcher"/>: match the last compound against the
/// candidate element, then walk back along the complex selector looking
/// for ancestors / previous siblings that satisfy the earlier compounds.
/// </summary>
public sealed class SelectorList
{
    public IReadOnlyList<ComplexSelector> Selectors { get; }

    public SelectorList(IReadOnlyList<ComplexSelector> selectors)
    {
        Selectors = selectors;
    }
}

/// <summary>A single complex selector. <see cref="Compounds"/> and
/// <see cref="Combinators"/> alternate: <c>Compounds[0] Combinators[0]
/// Compounds[1] Combinators[1] ... Compounds[n]</c>.</summary>
public sealed class ComplexSelector
{
    public IReadOnlyList<CompoundSelector> Compounds { get; }
    public IReadOnlyList<Combinator> Combinators { get; }

    public ComplexSelector(
        IReadOnlyList<CompoundSelector> compounds,
        IReadOnlyList<Combinator> combinators)
    {
        if (combinators.Count != compounds.Count - 1)
            throw new ArgumentException(
                "Combinators must have count = Compounds.Count - 1");
        Compounds = compounds;
        Combinators = combinators;
    }
}

public enum Combinator
{
    Descendant,      // space
    Child,           // >
    AdjacentSibling, // +
    GeneralSibling,  // ~
}

/// <summary>One element's worth of constraints: <c>div.foo#bar[attr]:first-child</c>
/// is a single compound.</summary>
public sealed class CompoundSelector
{
    public IReadOnlyList<SimpleSelector> Simples { get; }

    public CompoundSelector(IReadOnlyList<SimpleSelector> simples)
    {
        Simples = simples;
    }
}

public abstract class SimpleSelector
{
    internal SimpleSelector() { }
}

/// <summary>Type selector (<c>div</c>) or universal (<c>*</c>).</summary>
public sealed class TypeSelector : SimpleSelector
{
    public string TagName { get; }
    public bool IsUniversal => TagName == "*";

    public TypeSelector(string tagName)
    {
        TagName = tagName;
    }
}

public sealed class IdSelector : SimpleSelector
{
    public string Id { get; }
    public IdSelector(string id) => Id = id;
}

public sealed class ClassSelector : SimpleSelector
{
    public string ClassName { get; }
    public ClassSelector(string className) => ClassName = className;
}

/// <summary>Attribute selector (<c>[name]</c>, <c>[name="x"]</c>,
/// <c>[name^="x"]</c> etc.).</summary>
public sealed class AttributeSelector : SimpleSelector
{
    public string Name { get; }
    public AttributeMatch Match { get; }
    public string? Value { get; }
    public bool CaseInsensitive { get; }

    public AttributeSelector(
        string name, AttributeMatch match, string? value, bool caseInsensitive = false)
    {
        Name = name;
        Match = match;
        Value = value;
        CaseInsensitive = caseInsensitive;
    }
}

public enum AttributeMatch
{
    /// <summary>Attribute must exist (any value).</summary>
    Exists,
    /// <summary><c>[name="value"]</c> exact match.</summary>
    Equals,
    /// <summary><c>[name~="value"]</c> — value is a whitespace-separated token.</summary>
    ContainsWord,
    /// <summary><c>[name|="value"]</c> — exact match or starts with <c>value-</c>.</summary>
    DashPrefix,
    /// <summary><c>[name^="value"]</c> — starts with.</summary>
    StartsWith,
    /// <summary><c>[name$="value"]</c> — ends with.</summary>
    EndsWith,
    /// <summary><c>[name*="value"]</c> — contains.</summary>
    Substring,
}

/// <summary>Pseudo-class selector (<c>:first-child</c>, <c>:nth-child(2n+1)</c>,
/// <c>:not(...)</c>).</summary>
public sealed class PseudoClassSelector : SimpleSelector
{
    public PseudoClassKind Kind { get; }

    /// <summary>For <c>:nth-*</c> pseudos: the A and B coefficients of
    /// <c>An+B</c>. <see cref="Argument"/> is used by <see cref="PseudoClassKind.Not"/>.</summary>
    public int A { get; }
    public int B { get; }

    /// <summary>For <c>:not(X)</c>: the inner selector list.</summary>
    public SelectorList? Argument { get; }

    public PseudoClassSelector(PseudoClassKind kind) { Kind = kind; }

    public PseudoClassSelector(PseudoClassKind kind, int a, int b)
    {
        Kind = kind;
        A = a;
        B = b;
    }

    public PseudoClassSelector(PseudoClassKind kind, SelectorList argument)
    {
        Kind = kind;
        Argument = argument;
    }
}

public enum PseudoClassKind
{
    FirstChild,
    LastChild,
    OnlyChild,
    FirstOfType,
    LastOfType,
    OnlyOfType,
    NthChild,       // An+B
    NthLastChild,   // An+B
    NthOfType,      // An+B
    NthLastOfType,  // An+B
    Root,
    Empty,
    Not,            // :not(X) — Argument holds the inner selector list
}
