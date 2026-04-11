namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Minimum-viable <c>Symbol</c> built-in for slice 3b-7a —
/// enough to expose the <c>Symbol.iterator</c> well-known
/// symbol and let user code create fresh symbols for their
/// own extension points.
///
/// Limitations (all deferred):
/// <list type="bullet">
/// <item><c>typeof sym === "object"</c> in this engine; the
///   spec says <c>"symbol"</c>. We trade faithful
///   <c>typeof</c> for a much simpler implementation.</item>
/// <item>No <c>Symbol.for</c> / <c>Symbol.keyFor</c> shared
///   registry — the only well-known symbol we ship is
///   <c>Symbol.iterator</c>, and user-created symbols are
///   always fresh.</item>
/// <item>No other well-known symbols (<c>asyncIterator</c>,
///   <c>hasInstance</c>, <c>toPrimitive</c>, etc.). Each one
///   gets added as a later slice picks it up.</item>
/// </list>
/// </summary>
internal static class BuiltinSymbol
{
    public static void Install(JsEngine engine)
    {
        // Symbol() — construct a new, unique symbol. Calling
        // as a constructor (`new Symbol()`) throws, per spec.
        var symbolCtor = new JsFunction(
            "Symbol",
            (thisVal, args) =>
            {
                string? description = null;
                if (args.Count > 0 && args[0] is not JsUndefined)
                {
                    description = JsValue.ToJsString(args[0]);
                }
                return new JsSymbol(description);
            });

        // Expose the well-known iterator symbol as a
        // non-enumerable static property so script code can
        // read it as `Symbol.iterator`.
        symbolCtor.SetNonEnumerable("iterator", engine.IteratorSymbol);

        // ES2015 shared registry. Symbol.for(key) returns
        // a symbol registered under `key`, creating a fresh
        // one if none exists. Symbol.keyFor(sym) returns the
        // registry key for a registered symbol, or undefined
        // for unregistered symbols. The registry is one-per-
        // engine and uses the description string itself as
        // the lookup key.
        var registry = new Dictionary<string, JsSymbol>();
        var reverse = new Dictionary<JsSymbol, string>(ReferenceEqualityComparer.Instance);

        symbolCtor.SetNonEnumerable("for", new JsFunction("for", (thisVal, args) =>
        {
            string key = args.Count > 0 ? JsValue.ToJsString(args[0]) : "undefined";
            if (registry.TryGetValue(key, out var existing))
            {
                return existing;
            }
            var fresh = new JsSymbol(key);
            registry[key] = fresh;
            reverse[fresh] = key;
            return fresh;
        }));

        symbolCtor.SetNonEnumerable("keyFor", new JsFunction("keyFor", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not JsSymbol sym) return JsValue.Undefined;
            return reverse.TryGetValue(sym, out var key) ? key : JsValue.Undefined;
        }));

        engine.Globals["Symbol"] = symbolCtor;
    }
}
