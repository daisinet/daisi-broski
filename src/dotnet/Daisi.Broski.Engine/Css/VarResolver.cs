using System.Text;

namespace Daisi.Broski.Engine.Css;

/// <summary>
/// Substitutes CSS custom-property references (<c>var(--name)</c>
/// and <c>var(--name, fallback)</c>) inside computed-style
/// values. Runs as a post-pass after the cascade + inheritance
/// resolve so the lookup map already contains every
/// <c>--*</c> property the element has access to.
///
/// <para>
/// Recursion is bounded — vars can reference vars
/// (<c>--accent: var(--brand-blue)</c>), but a circular
/// reference like <c>--a: var(--b); --b: var(--a)</c>
/// would infinite-loop. We cap depth at 32 and bail with
/// the unsubstituted text, matching what real browsers do
/// for over-deep substitution.
/// </para>
///
/// <para>
/// Deferred: the spec's <c>@property</c> at-rule (with its
/// type-aware substitution rules), <c>env()</c> for safe-
/// area insets, and animation interpolation through var
/// boundaries.
/// </para>
/// </summary>
internal static class VarResolver
{
    private const int MaxDepth = 32;

    /// <summary>Replace every <c>var(...)</c> reference in
    /// every value of <paramref name="values"/> with the
    /// resolved property text. Mutates the dictionary in
    /// place.</summary>
    public static void SubstituteAll(Dictionary<string, string> values)
    {
        // Snapshot the keys so we can mutate values during
        // iteration. The set of keys doesn't change — only
        // their string contents.
        var keys = new string[values.Count];
        values.Keys.CopyTo(keys, 0);
        foreach (var key in keys)
        {
            var v = values[key];
            if (v.IndexOf("var(", StringComparison.Ordinal) < 0) continue;
            values[key] = Substitute(v, values, depth: 0);
        }
    }

    /// <summary>Substitute a single value string. Public so
    /// callers that just want one-off resolution (e.g. the
    /// painter's color shorthand fallback) can use it
    /// without going through the dictionary path.</summary>
    public static string Substitute(string value, Dictionary<string, string> vars, int depth)
    {
        if (depth > MaxDepth) return value;
        if (string.IsNullOrEmpty(value)) return value;
        if (value.IndexOf("var(", StringComparison.Ordinal) < 0) return value;

        var sb = new StringBuilder();
        int i = 0;
        while (i < value.Length)
        {
            int varStart = IndexOf(value, "var(", i);
            if (varStart < 0)
            {
                sb.Append(value, i, value.Length - i);
                break;
            }
            sb.Append(value, i, varStart - i);

            // Find the matching close paren. Track nesting
            // so `var(--a, var(--b))` resolves correctly.
            int parenDepth = 1;
            int p = varStart + 4;
            while (p < value.Length && parenDepth > 0)
            {
                char c = value[p];
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                if (parenDepth > 0) p++;
            }
            if (p >= value.Length)
            {
                // Unbalanced — emit the rest verbatim and stop.
                sb.Append(value, varStart, value.Length - varStart);
                return sb.ToString();
            }

            // value[varStart+4 .. p-1] is the inner.
            var inner = value.Substring(varStart + 4, p - (varStart + 4));
            int commaIdx = TopLevelComma(inner);
            string name = (commaIdx < 0 ? inner : inner.Substring(0, commaIdx)).Trim();
            string? fallback = commaIdx < 0
                ? null
                : inner.Substring(commaIdx + 1).Trim();

            string resolved;
            if (vars.TryGetValue(name, out var direct))
            {
                resolved = Substitute(direct, vars, depth + 1);
            }
            else if (fallback is not null)
            {
                resolved = Substitute(fallback, vars, depth + 1);
            }
            else
            {
                // Unresolved — empty string per CSS Values 4
                // §11. Most consumers (color parsers) will
                // treat the value as transparent, which is
                // closer to "missing" than the literal
                // var(...) text.
                resolved = "";
            }

            sb.Append(resolved);
            i = p + 1;
        }
        return sb.ToString();
    }

    /// <summary>Find the position of the first comma at the
    /// top level of <paramref name="s"/> (depth-zero), so
    /// commas inside nested var()/calc() function calls
    /// don't fool the fallback split.</summary>
    private static int TopLevelComma(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) return i;
        }
        return -1;
    }

    private static int IndexOf(string s, string needle, int start)
    {
        return s.IndexOf(needle, start, StringComparison.Ordinal);
    }
}
