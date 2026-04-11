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

        Builtins.Install(this);
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
