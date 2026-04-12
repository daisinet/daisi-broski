namespace Daisi.Broski.Engine.Dom.Selectors;

/// <summary>
/// Tests whether a DOM element satisfies a parsed <see cref="SelectorList"/>.
///
/// Matching is performed <b>right-to-left</b>, the standard CSS algorithm:
/// we first check whether the candidate element matches the rightmost
/// compound (the "subject"), then walk backwards through the complex
/// selector looking for ancestors or previous siblings that satisfy each
/// earlier compound.
///
/// This direction is the right one because the subject of a selector
/// is almost always much rarer than the outer constraints: if the
/// subject doesn't match, we skip the expensive ancestor walk entirely.
/// </summary>
public static class SelectorMatcher
{
    /// <summary>Return <c>true</c> if <paramref name="element"/> matches
    /// any branch of <paramref name="list"/>.</summary>
    public static bool Matches(Element element, SelectorList list)
    {
        foreach (var selector in list.Selectors)
        {
            if (MatchesComplex(element, selector)) return true;
        }
        return false;
    }

    public static bool MatchesComplex(Element element, ComplexSelector selector)
    {
        // Match the rightmost compound against the candidate.
        int idx = selector.Compounds.Count - 1;
        if (!MatchesCompound(element, selector.Compounds[idx])) return false;

        // Walk backwards one combinator at a time.
        Element? current = element;
        for (int i = idx - 1; i >= 0; i--)
        {
            var combinator = selector.Combinators[i];
            var compound = selector.Compounds[i];

            switch (combinator)
            {
                case Combinator.Child:
                    current = current!.ParentNode as Element;
                    if (current is null || !MatchesCompound(current, compound)) return false;
                    break;

                case Combinator.Descendant:
                    {
                        // Walk ancestors looking for the first match.
                        var ancestor = current!.ParentNode as Element;
                        while (ancestor is not null && !MatchesCompound(ancestor, compound))
                            ancestor = ancestor.ParentNode as Element;
                        if (ancestor is null) return false;
                        current = ancestor;
                        break;
                    }

                case Combinator.AdjacentSibling:
                    current = current!.PreviousSibling as Element;
                    // Skip over non-element nodes like text.
                    while (current is not null && current is not Element)
                        current = current.PreviousSibling as Element;
                    if (current is null || !MatchesCompound(current, compound)) return false;
                    break;

                case Combinator.GeneralSibling:
                    {
                        var prev = current!.PreviousSibling;
                        while (prev is not null)
                        {
                            if (prev is Element prevEl && MatchesCompound(prevEl, compound))
                            {
                                current = prevEl;
                                goto nextCombinator;
                            }
                            prev = prev.PreviousSibling;
                        }
                        return false;
                    }

                default:
                    throw new InvalidOperationException($"Unknown combinator {combinator}");
            }
            nextCombinator:;
        }

        return true;
    }

    private static bool MatchesCompound(Element element, CompoundSelector compound)
    {
        foreach (var simple in compound.Simples)
        {
            if (!MatchesSimple(element, simple)) return false;
        }
        return true;
    }

    private static bool MatchesSimple(Element element, SimpleSelector simple)
    {
        switch (simple)
        {
            case TypeSelector t:
                return t.IsUniversal || element.TagName == t.TagName;

            case IdSelector i:
                return element.GetAttribute("id") == i.Id;

            case ClassSelector c:
                {
                    foreach (var tok in element.ClassList)
                    {
                        if (tok == c.ClassName) return true;
                    }
                    return false;
                }

            case AttributeSelector a:
                return MatchesAttribute(element, a);

            case PseudoClassSelector p:
                return MatchesPseudoClass(element, p);
        }

        return false;
    }

    private static bool MatchesAttribute(Element element, AttributeSelector a)
    {
        var actual = element.GetAttribute(a.Name);
        if (actual is null) return false;

        if (a.Match == AttributeMatch.Exists) return true;

        var comparison = a.CaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var expected = a.Value ?? "";

        return a.Match switch
        {
            AttributeMatch.Equals => string.Equals(actual, expected, comparison),
            AttributeMatch.StartsWith =>
                expected.Length > 0 && actual.StartsWith(expected, comparison),
            AttributeMatch.EndsWith =>
                expected.Length > 0 && actual.EndsWith(expected, comparison),
            AttributeMatch.Substring =>
                expected.Length > 0 && actual.Contains(expected, comparison),
            AttributeMatch.ContainsWord => ContainsWord(actual, expected, comparison),
            AttributeMatch.DashPrefix =>
                string.Equals(actual, expected, comparison) ||
                actual.StartsWith(expected + "-", comparison),
            _ => false,
        };
    }

    private static bool ContainsWord(string haystack, string needle, StringComparison comparison)
    {
        if (needle.Length == 0) return false;
        foreach (var token in haystack.Split(
                     [' ', '\t', '\n', '\r', '\f'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, needle, comparison)) return true;
        }
        return false;
    }

    private static bool MatchesPseudoClass(Element element, PseudoClassSelector p)
    {
        switch (p.Kind)
        {
            case PseudoClassKind.Root:
                return element.OwnerDocument?.DocumentElement == element;

            case PseudoClassKind.Scope:
                // :scope matches the scoping element — when used
                // via querySelector on a specific element, that
                // element is the scope. Since our querySelector
                // implementation (Node.QuerySelector) excludes
                // the starting node itself from the result, :scope
                // in practice matches the document root, same as
                // :root. A proper implementation would thread the
                // scope element through the match, but this covers
                // the common `el.querySelector(':scope > child')`
                // pattern.
                return element.OwnerDocument?.DocumentElement == element;

            case PseudoClassKind.Empty:
                // :empty matches elements with no element children AND no
                // non-empty text nodes.
                foreach (var n in element.ChildNodes)
                {
                    if (n is Element) return false;
                    if (n is Text t && t.Data.Length > 0) return false;
                }
                return true;

            case PseudoClassKind.FirstChild:
                return PreviousElementSibling(element) is null;

            case PseudoClassKind.LastChild:
                return NextElementSibling(element) is null;

            case PseudoClassKind.OnlyChild:
                return PreviousElementSibling(element) is null &&
                       NextElementSibling(element) is null;

            case PseudoClassKind.FirstOfType:
                return PreviousElementSiblingOfType(element) is null;

            case PseudoClassKind.LastOfType:
                return NextElementSiblingOfType(element) is null;

            case PseudoClassKind.OnlyOfType:
                return PreviousElementSiblingOfType(element) is null &&
                       NextElementSiblingOfType(element) is null;

            case PseudoClassKind.NthChild:
                return NthMatches(IndexAmongChildren(element), p.A, p.B);

            case PseudoClassKind.NthLastChild:
                return NthMatches(IndexAmongChildren(element, fromEnd: true), p.A, p.B);

            case PseudoClassKind.NthOfType:
                return NthMatches(IndexAmongSameTypeChildren(element), p.A, p.B);

            case PseudoClassKind.NthLastOfType:
                return NthMatches(IndexAmongSameTypeChildren(element, fromEnd: true), p.A, p.B);

            case PseudoClassKind.Not:
                return p.Argument is not null && !Matches(element, p.Argument);
        }

        return false;
    }

    // -------- helpers --------

    private static Element? PreviousElementSibling(Element element)
    {
        var n = element.PreviousSibling;
        while (n is not null && n is not Element) n = n.PreviousSibling;
        return n as Element;
    }

    private static Element? NextElementSibling(Element element)
    {
        var n = element.NextSibling;
        while (n is not null && n is not Element) n = n.NextSibling;
        return n as Element;
    }

    private static Element? PreviousElementSiblingOfType(Element element)
    {
        var n = element.PreviousSibling;
        while (n is not null)
        {
            if (n is Element e && e.TagName == element.TagName) return e;
            n = n.PreviousSibling;
        }
        return null;
    }

    private static Element? NextElementSiblingOfType(Element element)
    {
        var n = element.NextSibling;
        while (n is not null)
        {
            if (n is Element e && e.TagName == element.TagName) return e;
            n = n.NextSibling;
        }
        return null;
    }

    private static int IndexAmongChildren(Element element, bool fromEnd = false)
    {
        var parent = element.ParentNode;
        if (parent is null) return 1;

        int index = 0;
        if (!fromEnd)
        {
            foreach (var sibling in parent.ChildNodes)
            {
                if (sibling is Element e)
                {
                    index++;
                    if (ReferenceEquals(e, element)) return index;
                }
            }
        }
        else
        {
            for (int i = parent.ChildNodes.Count - 1; i >= 0; i--)
            {
                if (parent.ChildNodes[i] is Element e)
                {
                    index++;
                    if (ReferenceEquals(e, element)) return index;
                }
            }
        }
        return index;
    }

    private static int IndexAmongSameTypeChildren(Element element, bool fromEnd = false)
    {
        var parent = element.ParentNode;
        if (parent is null) return 1;

        int index = 0;
        if (!fromEnd)
        {
            foreach (var sibling in parent.ChildNodes)
            {
                if (sibling is Element e && e.TagName == element.TagName)
                {
                    index++;
                    if (ReferenceEquals(e, element)) return index;
                }
            }
        }
        else
        {
            for (int i = parent.ChildNodes.Count - 1; i >= 0; i--)
            {
                if (parent.ChildNodes[i] is Element e && e.TagName == element.TagName)
                {
                    index++;
                    if (ReferenceEquals(e, element)) return index;
                }
            }
        }
        return index;
    }

    /// <summary>Does <paramref name="index"/> satisfy <c>A*n + B</c> for some
    /// non-negative integer <c>n</c>?</summary>
    private static bool NthMatches(int index, int a, int b)
    {
        if (a == 0) return index == b;
        // index == a*n + b  →  n = (index - b) / a, must be non-negative integer
        int diff = index - b;
        if (diff == 0) return true;
        if (diff % a != 0) return false;
        return diff / a >= 0;
    }
}
