using Daisi.Broski.Engine.Dom.Selectors;

namespace Daisi.Broski.Engine.Css;

/// <summary>
/// Top-level parse result for one <c>&lt;style&gt;</c> block,
/// inline declaration list, or external CSS file. Holds the
/// rules in source order — the cascade slice will sort
/// per-element on demand rather than eagerly per stylesheet.
/// </summary>
public sealed class Stylesheet
{
    public IReadOnlyList<Rule> Rules { get; init; } = Array.Empty<Rule>();

    /// <summary>Optional URL of the source — populated for
    /// stylesheets pulled from <c>&lt;link rel=stylesheet&gt;</c>
    /// (when external-stylesheet fetching lands in a later
    /// slice). Null for inline <c>&lt;style&gt;</c> blocks.</summary>
    public Uri? Href { get; init; }

    /// <summary>Convenience: walk every <see cref="StyleRule"/>
    /// in source order, descending into <c>@media</c> /
    /// <c>@supports</c> blocks. <c>@keyframes</c> and
    /// <c>@font-face</c> rules are skipped (they don't carry
    /// matchable selectors against a document tree).</summary>
    public IEnumerable<StyleRule> AllStyleRules()
    {
        foreach (var rule in Rules)
        {
            switch (rule)
            {
                case StyleRule sr: yield return sr; break;
                case AtRule ar when ar.Rules is { Count: > 0 }
                    && ar.Name is "media" or "supports":
                    foreach (var nested in ar.Rules)
                    {
                        if (nested is StyleRule nsr) yield return nsr;
                    }
                    break;
            }
        }
    }
}

/// <summary>Base class for every rule in a stylesheet.
/// Concrete: <see cref="StyleRule"/> and <see cref="AtRule"/>.</summary>
public abstract class Rule { }

/// <summary>One <c>selector { decl... }</c> rule. Carries
/// both the parsed <see cref="SelectorList"/> (when valid)
/// and the raw <see cref="SelectorText"/> so CSSOM consumers
/// see the original text and the matcher can use the
/// structured form.</summary>
public sealed class StyleRule : Rule
{
    public required string SelectorText { get; init; }

    /// <summary>Parsed selectors — null when the source text
    /// failed <see cref="SelectorParser.Parse"/>. The cascade
    /// slice will skip rules where this is null; CSSOM still
    /// exposes the rule via <see cref="SelectorText"/>.</summary>
    public SelectorList? Selectors { get; init; }

    public required IReadOnlyList<Declaration> Declarations { get; init; }
}

/// <summary>An at-rule (<c>@media</c>, <c>@keyframes</c>,
/// <c>@font-face</c>, <c>@import</c>, ...). Either holds
/// nested <see cref="Rules"/> (block-with-rules form) OR
/// nested <see cref="Declarations"/> (block-with-declarations
/// form like <c>@font-face</c>) OR neither (declaration-only
/// at-rules like <c>@import</c> / <c>@charset</c>) — exactly
/// which depends on <see cref="Name"/>.</summary>
public sealed class AtRule : Rule
{
    public required string Name { get; init; }
    public required string Prelude { get; init; }
    public IReadOnlyList<Rule> Rules { get; init; } = Array.Empty<Rule>();
    public IReadOnlyList<Declaration> Declarations { get; init; } = Array.Empty<Declaration>();
}

/// <summary>One CSS property declaration: <c>property: value
/// [!important]</c>. Property names are normalized to
/// lowercase; values are kept as the trimmed source text
/// (the cascade / computed-style slice does the per-property
/// parsing).</summary>
public sealed class Declaration
{
    public required string Property { get; init; }
    public required string Value { get; init; }
    public bool Important { get; init; }
}
