using System.Text.RegularExpressions;

namespace Daisi.Broski.Engine.Js;

/// <summary>
/// JS-side <c>RegExp</c> value. Wraps a .NET
/// <see cref="System.Text.RegularExpressions.Regex"/>,
/// translating the JS flag set and tracking the
/// spec-required <c>lastIndex</c> for stateful
/// global / sticky matching.
///
/// <para>
/// DD-01 tracks the phase 3c placeholder plan: BCL regex
/// now, hand-written ECMA-262 NFA later (for step budget
/// + catastrophic-backtracking safety in the sandbox).
/// The two flavors mostly agree on the common subset,
/// and the translation is explicit so swapping the
/// backend in the future is a single-file change.
/// </para>
///
/// <para>
/// Differences from the spec worth knowing:
/// <list type="bullet">
/// <item><c>\d</c> / <c>\w</c> / <c>\s</c> are slightly
///   different between JS and .NET around Unicode, but
///   agree on ASCII input — the common case.</item>
/// <item>Named groups use <c>(?&lt;name&gt;...)</c> in
///   both syntaxes, so they survive translation
///   unchanged.</item>
/// <item>JS <c>(?=)</c> / <c>(?!)</c> / <c>(?&lt;=)</c>
///   / <c>(?&lt;!)</c> lookarounds are all supported by
///   .NET's engine natively.</item>
/// </list>
/// </para>
/// </summary>
public sealed class JsRegExp : JsObject
{
    /// <summary>Original JS pattern text, as written.</summary>
    public string Source { get; }

    /// <summary>Original JS flag set, e.g. <c>"gim"</c>.</summary>
    public string Flags { get; }

    public bool Global { get; }
    public bool IgnoreCase { get; }
    public bool Multiline { get; }
    public bool DotAll { get; }
    public bool Sticky { get; }
    public bool Unicode { get; }

    /// <summary>
    /// Current match index for global / sticky execution.
    /// Spec §22.2.5.5 — consumers that use
    /// <c>regex.exec(str)</c> in a loop read and advance
    /// this between matches.
    /// </summary>
    public int LastIndex { get; set; }

    /// <summary>
    /// Lazy .NET regex built from <see cref="Source"/> +
    /// translated options on first use. Held as a field so
    /// repeated calls against the same literal reuse the
    /// compiled state.
    /// </summary>
    private Regex? _compiled;

    public JsRegExp(string source, string flags)
    {
        Source = source;
        Flags = flags;
        Global = flags.Contains('g');
        IgnoreCase = flags.Contains('i');
        Multiline = flags.Contains('m');
        DotAll = flags.Contains('s');
        Sticky = flags.Contains('y');
        Unicode = flags.Contains('u');
    }

    /// <summary>
    /// Return (and lazily build) the .NET
    /// <see cref="Regex"/> for this pattern. Translates JS
    /// flags into <see cref="RegexOptions"/>:
    /// <list type="bullet">
    /// <item><c>i</c> → <c>IgnoreCase</c></item>
    /// <item><c>m</c> → <c>Multiline</c></item>
    /// <item><c>s</c> → <c>Singleline</c> (dotall)</item>
    /// <item><c>u</c> is a spec hint the backend honors by
    ///   default; we leave <see cref="RegexOptions"/>
    ///   alone.</item>
    /// <item><c>g</c> and <c>y</c> are stateful in JS;
    ///   .NET's engine is stateless, so global / sticky
    ///   behavior is driven by <see cref="LastIndex"/>
    ///   bookkeeping in the match helpers.</item>
    /// </list>
    /// </summary>
    public Regex Compile()
    {
        if (_compiled is not null) return _compiled;
        var options = RegexOptions.ECMAScript;
        if (IgnoreCase) options |= RegexOptions.IgnoreCase;
        if (Multiline) options |= RegexOptions.Multiline;
        if (DotAll) options |= RegexOptions.Singleline;
        try
        {
            _compiled = new Regex(Source, options);
        }
        catch (ArgumentException)
        {
            // The ECMAScript-compat mode supports the common
            // subset but doesn't cover every spec construct
            // (e.g. Unicode property escapes). Retry once
            // without the ECMAScript flag for maximum
            // compatibility with real-world patterns that
            // drift outside the strict subset.
            var fallback = options & ~RegexOptions.ECMAScript;
            _compiled = new Regex(Source, fallback);
        }
        return _compiled;
    }

    /// <inheritdoc />
    public override object? Get(string key) => key switch
    {
        "source" => Source,
        "flags" => Flags,
        "global" => Global,
        "ignoreCase" => IgnoreCase,
        "multiline" => Multiline,
        "dotAll" => DotAll,
        "sticky" => Sticky,
        "unicode" => Unicode,
        "lastIndex" => (double)LastIndex,
        _ => base.Get(key),
    };

    /// <inheritdoc />
    public override void Set(string key, object? value)
    {
        if (key == "lastIndex")
        {
            LastIndex = (int)JsValue.ToNumber(value);
            return;
        }
        if (key is "source" or "flags" or "global" or "ignoreCase" or
            "multiline" or "dotAll" or "sticky" or "unicode")
        {
            // Read-only accessors; silently ignore writes in
            // non-strict mode.
            return;
        }
        base.Set(key, value);
    }
}
