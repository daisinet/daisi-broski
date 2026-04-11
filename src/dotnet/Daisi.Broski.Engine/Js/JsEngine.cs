namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Top-level facade for the phase-3a JavaScript engine. Parses
/// source text into an AST, compiles it into bytecode, and runs it
/// on the stack VM, returning the completion value of the last
/// evaluated expression (ECMA §14 top-level completion semantics).
///
/// Each <see cref="JsEngine"/> instance owns its own globals
/// dictionary plus the well-known prototype objects
/// (<see cref="ArrayPrototype"/>, <see cref="StringPrototype"/>,
/// etc.) that the VM consults when it encounters property access
/// on a primitive. Built-ins (<c>parseInt</c>,
/// <c>Array.prototype.push</c>, and friends) are installed in
/// the constructor, so successive <see cref="Evaluate"/> calls
/// on the same engine see a stable, populated global environment.
///
/// Not thread-safe: do not call methods on the same engine
/// instance from multiple threads.
/// </summary>
public sealed class JsEngine
{
    /// <summary>
    /// Global variable bindings. Exposed directly so tests and
    /// host code can set values before <see cref="Evaluate"/> runs
    /// and inspect them afterward. Pre-seeded with the ES5
    /// read-only globals (<c>undefined</c>, <c>NaN</c>,
    /// <c>Infinity</c>) and the built-in constructors /
    /// functions installed by slice 6.
    /// </summary>
    public Dictionary<string, object?> Globals { get; } = new();

    /// <summary>
    /// Root of the prototype chain for every object the VM
    /// allocates via a literal. Slice 6a doesn't yet install
    /// <c>Object.prototype</c> methods (<c>hasOwnProperty</c>,
    /// <c>toString</c>, etc.) — slice 6b adds those.
    /// </summary>
    public JsObject ObjectPrototype { get; }

    /// <summary>
    /// Prototype of every <see cref="JsArray"/>. The VM sets
    /// <c>CreateArray</c>'s output's prototype to this, and
    /// <c>Array.prototype.*</c> methods live here.
    /// </summary>
    public JsObject ArrayPrototype { get; }

    /// <summary>
    /// Prototype that the VM consults when a property is read
    /// from a string primitive. <c>String.prototype.charAt</c>
    /// and friends live here.
    /// </summary>
    public JsObject StringPrototype { get; }

    /// <summary>Number primitive prototype. Slice 6c populates.</summary>
    public JsObject NumberPrototype { get; }

    /// <summary>Boolean primitive prototype. Slice 6c populates.</summary>
    public JsObject BooleanPrototype { get; }

    /// <summary>
    /// <c>Function.prototype</c>. Every <see cref="JsFunction"/>
    /// value has this as its <c>[[Prototype]]</c> — that's what
    /// makes <c>fn.call(...)</c> / <c>fn.apply(...)</c> /
    /// <c>fn.bind(...)</c> resolve via the normal property
    /// lookup path. Distinct from
    /// <see cref="JsFunction.FunctionPrototype"/>, which is the
    /// per-function <c>.prototype</c> property used by
    /// <c>new F(...)</c> to set an instance's prototype.
    /// </summary>
    public JsObject FunctionPrototype { get; }

    /// <summary>
    /// <c>Date.prototype</c>. Installed by slice 6d; carries
    /// <c>getTime</c> / <c>valueOf</c> / <c>getFullYear</c> /
    /// <c>toISOString</c> / etc. Every <see cref="JsDate"/>
    /// value has this as its <c>[[Prototype]]</c>.
    /// </summary>
    public JsObject DatePrototype { get; internal set; } = null!;

    /// <summary>
    /// Root <c>Error.prototype</c>. Installed by slice 6b; used
    /// by <see cref="JsVM"/> when <c>RaiseError</c> creates an
    /// error object with no more specific subtype.
    /// </summary>
    public JsObject ErrorPrototype { get; internal set; } = null!;

    /// <summary><c>TypeError.prototype</c>.</summary>
    public JsObject TypeErrorPrototype { get; internal set; } = null!;

    /// <summary><c>RangeError.prototype</c>.</summary>
    public JsObject RangeErrorPrototype { get; internal set; } = null!;

    /// <summary><c>SyntaxError.prototype</c>.</summary>
    public JsObject SyntaxErrorPrototype { get; internal set; } = null!;

    /// <summary><c>ReferenceError.prototype</c>.</summary>
    public JsObject ReferenceErrorPrototype { get; internal set; } = null!;

    public JsEngine()
    {
        // Seed the read-only globals before installing built-ins
        // so Builtins.Install can reference them if needed.
        Globals["undefined"] = JsValue.Undefined;
        Globals["NaN"] = double.NaN;
        Globals["Infinity"] = double.PositiveInfinity;

        // Create prototype objects in dependency order.
        ObjectPrototype = new JsObject();
        ArrayPrototype = new JsObject { Prototype = ObjectPrototype };
        StringPrototype = new JsObject { Prototype = ObjectPrototype };
        NumberPrototype = new JsObject { Prototype = ObjectPrototype };
        BooleanPrototype = new JsObject { Prototype = ObjectPrototype };
        FunctionPrototype = new JsObject { Prototype = ObjectPrototype };

        Builtins.Install(this);

        // Post-install: set [[Prototype]] = FunctionPrototype on
        // every JsFunction reachable from the engine so
        // `fn.call(...)` / `fn.apply(...)` / `fn.bind(...)`
        // resolve via the standard prototype-chain lookup.
        // User functions created later via the MakeFunction
        // opcode get their Prototype set at creation time by
        // the VM.
        FixupFunctionPrototypes();
    }

    /// <summary>
    /// Walk all objects reachable from <see cref="Globals"/> and
    /// the well-known prototypes, and set
    /// <c>JsFunction.Prototype</c> to <see cref="FunctionPrototype"/>
    /// for any function that doesn't already have one. Called
    /// once at the end of the engine constructor.
    /// </summary>
    private void FixupFunctionPrototypes()
    {
        var visited = new HashSet<JsObject>(ReferenceEqualityComparer.Instance);
        var roots = new JsObject[]
        {
            ObjectPrototype, ArrayPrototype, StringPrototype,
            NumberPrototype, BooleanPrototype, FunctionPrototype,
            ErrorPrototype, TypeErrorPrototype, RangeErrorPrototype,
            SyntaxErrorPrototype, ReferenceErrorPrototype,
            DatePrototype,
        };
        foreach (var root in roots)
        {
            if (root is not null) WalkForFunctions(root, visited);
        }
        foreach (var kv in Globals)
        {
            WalkValueForFunctions(kv.Value, visited);
        }
    }

    private void WalkValueForFunctions(object? value, HashSet<JsObject> visited)
    {
        if (value is JsObject obj) WalkForFunctions(obj, visited);
    }

    private void WalkForFunctions(JsObject obj, HashSet<JsObject> visited)
    {
        if (!visited.Add(obj)) return;
        if (obj is JsFunction fn && fn.Prototype is null)
        {
            fn.Prototype = FunctionPrototype;
        }
        foreach (var kv in obj.Properties)
        {
            WalkValueForFunctions(kv.Value, visited);
        }
    }

    /// <summary>
    /// Parse, compile, and run <paramref name="source"/>. Returns
    /// the value of the last top-level expression statement, or
    /// <c>undefined</c> if the program had none. Throws
    /// <see cref="JsParseException"/> on a syntax error,
    /// <see cref="JsCompileException"/> on an unsupported language
    /// form, or <see cref="JsRuntimeException"/> on an uncaught
    /// runtime exception.
    /// </summary>
    public object? Evaluate(string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = new JsCompiler().Compile(program);
        var vm = new JsVM(chunk, this);
        return vm.Run();
    }
}
