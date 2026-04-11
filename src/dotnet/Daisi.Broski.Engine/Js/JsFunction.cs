namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Compile-time description of a function body — the bytecode
/// <see cref="Chunk"/>, parameter names, and declared name. A
/// template is immutable and shared across every function-value
/// materialized from the same source (e.g., if a factory creates
/// many instances of the same inner function, they share the
/// chunk but each gets its own captured environment).
/// </summary>
public sealed class JsFunctionTemplate
{
    public Chunk Body { get; }
    public IReadOnlyList<string> ParamNames { get; }
    public string? Name { get; }

    /// <summary>
    /// Synthesized source length for diagnostics — the byte length
    /// of the original function expression / declaration in the
    /// source text.
    /// </summary>
    public int SourceLength { get; }

    public int ParamCount => ParamNames.Count;

    public JsFunctionTemplate(
        Chunk body,
        IReadOnlyList<string> paramNames,
        string? name,
        int sourceLength)
    {
        Body = body;
        ParamNames = paramNames;
        Name = name;
        SourceLength = sourceLength;
    }
}

/// <summary>
/// A function value at runtime. Inherits from <see cref="JsObject"/>
/// because JavaScript functions <i>are</i> objects — you can assign
/// properties on them, and they carry a <c>prototype</c> property
/// that's used by <c>new</c>. The captured environment is the
/// closure's "snapshot" — actually, a live reference to the env at
/// the moment the function was created.
///
/// Host-provided "native" functions (for the host to expose APIs
/// like <c>console.log</c>) can be modeled by the
/// <see cref="NativeImpl"/> delegate: if it's set, the VM calls it
/// instead of running the bytecode. Slice 4b does not yet ship
/// native functions, but the hook is wired so slice 6 (built-ins)
/// can attach without touching the VM dispatch loop.
/// </summary>
public sealed class JsFunction : JsObject
{
    public JsFunctionTemplate? Template { get; }
    public JsEnvironment? CapturedEnv { get; }
    public Func<object?, IReadOnlyList<object?>, object?>? NativeImpl { get; }

    /// <summary>
    /// User functions have a <c>prototype</c> property, a fresh
    /// object whose <c>constructor</c> points back to the function.
    /// <c>new F(...)</c> uses this as the prototype of the allocated
    /// instance. Native functions skip this — they're constructed
    /// via the host and the host decides how to respond to
    /// <c>new</c> (usually by throwing TypeError).
    /// </summary>
    public JsObject? FunctionPrototype { get; }

    public string? Name => Template?.Name ?? NativeName;
    private readonly string? NativeName;

    /// <summary>Construct a user function from a compiled template.</summary>
    public JsFunction(JsFunctionTemplate template, JsEnvironment capturedEnv)
    {
        Template = template;
        CapturedEnv = capturedEnv;

        // Every user function gets a prototype object. constructor
        // on that object points back at us so `new F().constructor === F`.
        // Both prototype and constructor are non-enumerable to match
        // how browsers expose them (they shouldn't appear in for..in).
        var proto = new JsObject();
        proto.SetNonEnumerable("constructor", this);
        FunctionPrototype = proto;
        SetNonEnumerable("prototype", proto);
    }

    /// <summary>
    /// Construct a native-backed function. Host code uses this to
    /// install built-ins like <c>console.log</c>. The function has
    /// no <c>prototype</c> property and cannot normally be used as
    /// a constructor (the VM throws if you <c>new</c> one, unless
    /// slice 6 extends the native-function protocol).
    /// </summary>
    public JsFunction(string name, Func<object?, IReadOnlyList<object?>, object?> impl)
    {
        NativeName = name;
        NativeImpl = impl;
        Template = null;
        CapturedEnv = null;
        FunctionPrototype = null;
    }

    public override string ToString() =>
        Name is not null ? $"function {Name}() {{ [native code] }}" : "function () { [native code] }";
}
