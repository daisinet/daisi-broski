namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Top-level facade for the phase-3a JavaScript engine. Parses
/// source text into an AST, compiles it into bytecode, and runs it
/// on the stack VM, returning the completion value of the last
/// evaluated expression (ECMA §14 top-level completion semantics).
///
/// Each <see cref="JsEngine"/> owns a single <see cref="Globals"/>
/// dictionary, so successive <see cref="Evaluate"/> calls see each
/// other's bindings — enough to support a REPL-style usage. No
/// concurrency guarantees: do not call methods on the same engine
/// instance from multiple threads.
///
/// This is a slice-3 surface. Slice 4 will add host-installable
/// functions (so tests can register a <c>log</c> built-in), and
/// slice 5 will add a proper error-reporting path with line / column
/// info on syntax and runtime errors.
/// </summary>
public sealed class JsEngine
{
    /// <summary>
    /// Global variable bindings. Exposed directly so tests and
    /// host code can set values before <see cref="Evaluate"/> runs,
    /// and inspect them afterward. Pre-seeded with the ES5 read-only
    /// globals <c>undefined</c>, <c>NaN</c>, and <c>Infinity</c>
    /// (writable here because phase 3a does not yet enforce the
    /// spec's [[Writable]] attribute).
    /// </summary>
    public Dictionary<string, object?> Globals { get; } = new()
    {
        ["undefined"] = JsValue.Undefined,
        ["NaN"] = double.NaN,
        ["Infinity"] = double.PositiveInfinity,
    };

    /// <summary>
    /// Parse, compile, and run <paramref name="source"/>. Returns
    /// the value of the last top-level expression statement, or
    /// <c>undefined</c> if the program had none. Throws
    /// <see cref="JsParseException"/> on a syntax error,
    /// <see cref="JsCompileException"/> on an unsupported language
    /// form, or <see cref="JsRuntimeException"/> on a runtime error.
    /// </summary>
    public object? Evaluate(string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = new JsCompiler().Compile(program);
        var vm = new JsVM(chunk, Globals);
        return vm.Run();
    }
}
