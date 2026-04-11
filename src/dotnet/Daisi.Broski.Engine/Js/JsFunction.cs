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

    /// <summary>
    /// True for ES2015 arrow functions. The VM uses this at
    /// <see cref="OpCode.MakeFunction"/> time to capture the
    /// current <c>this</c> binding into
    /// <see cref="JsFunction.CapturedThis"/>, and at call time
    /// to skip binding a fresh <c>arguments</c> object (arrows
    /// inherit the enclosing function's <c>arguments</c> via
    /// the env chain).
    /// </summary>
    public bool IsArrow { get; }

    /// <summary>
    /// True for ES2015 generator functions
    /// (<c>function* foo(){}</c>). Calling a generator
    /// function does not run its body — the VM's call
    /// machinery returns a fresh <see cref="JsGenerator"/>
    /// instead, and the body is executed incrementally by
    /// <see cref="JsGenerator.Next"/> via a separate
    /// per-generator <see cref="JsVM"/> instance.
    /// </summary>
    public bool IsGenerator { get; }

    /// <summary>
    /// Index of the ES2015 rest parameter in
    /// <see cref="ParamNames"/>, or <c>-1</c> if the function
    /// has no rest parameter. At call time the VM binds
    /// <c>ParamNames[RestParamIndex]</c> to a fresh
    /// <see cref="JsArray"/> holding the extra args beyond
    /// that index. The parser guarantees that a rest param is
    /// always the last formal parameter, so this is either
    /// <c>-1</c> or <c>ParamNames.Count - 1</c>.
    /// </summary>
    public int RestParamIndex { get; }

    public int ParamCount => ParamNames.Count;

    public JsFunctionTemplate(
        Chunk body,
        IReadOnlyList<string> paramNames,
        string? name,
        int sourceLength,
        bool isArrow = false,
        int restParamIndex = -1,
        bool isGenerator = false)
    {
        Body = body;
        ParamNames = paramNames;
        Name = name;
        SourceLength = sourceLength;
        IsArrow = isArrow;
        RestParamIndex = restParamIndex;
        IsGenerator = isGenerator;
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
    /// Alternative native implementation for built-ins that
    /// need to invoke JS functions (e.g., <c>Array.prototype.forEach</c>
    /// calling its callback). These receive a
    /// <see cref="JsVM"/> reference so they can call
    /// <c>vm.InvokeJsFunction</c> to run a user-supplied
    /// callback synchronously. Only one of
    /// <see cref="NativeImpl"/> or <see cref="NativeCallable"/>
    /// is set on any given <see cref="JsFunction"/>.
    /// </summary>
    public Func<JsVM, object?, IReadOnlyList<object?>, object?>? NativeCallable { get; }

    /// <summary>
    /// User functions have a <c>prototype</c> property, a fresh
    /// object whose <c>constructor</c> points back to the function.
    /// <c>new F(...)</c> uses this as the prototype of the allocated
    /// instance. Native functions skip this — they're constructed
    /// via the host and the host decides how to respond to
    /// <c>new</c> (usually by throwing TypeError).
    /// </summary>
    public JsObject? FunctionPrototype { get; }

    /// <summary>
    /// For arrow functions: the <c>this</c> value captured at
    /// the moment the arrow was materialized by
    /// <see cref="OpCode.MakeFunction"/>. Arrows don't bind
    /// their own <c>this</c> at call time — they use this
    /// captured value regardless of how they are called.
    /// Ignored for non-arrow functions.
    /// </summary>
    public object? CapturedThis { get; set; }

    /// <summary>
    /// For ES2015 class methods: the [[HomeObject]]'s
    /// prototype used to resolve <c>super.foo</c> / <c>super()</c>
    /// references in the method body.
    ///
    /// - For an instance method or a derived-class constructor,
    ///   this is the <i>parent class's prototype object</i>.
    ///   <c>super.foo(args)</c> reads <c>foo</c> off this
    ///   prototype, and <c>super(args)</c> inside a constructor
    ///   goes through <c>HomeSuper.constructor</c>.
    /// - For a static class method, this is the parent class
    ///   (the constructor function itself), so
    ///   <c>super.bar()</c> reads a static method off the
    ///   parent class.
    /// - <c>null</c> for ordinary (non-class) functions and for
    ///   class methods in a class with no <c>extends</c> clause.
    ///   <see cref="OpCode.LoadSuper"/> throws at runtime if
    ///   the current method's <c>HomeSuper</c> is null.
    /// </summary>
    public JsObject? HomeSuper { get; set; }

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
    /// install built-ins like <c>console.log</c>. The function
    /// has no <c>prototype</c> property by default; slice 6
    /// built-in constructors (Array, String, ...) install their
    /// own via <see cref="JsObject.SetNonEnumerable"/> after
    /// construction.
    /// </summary>
    public JsFunction(string name, Func<object?, IReadOnlyList<object?>, object?> impl)
    {
        NativeName = name;
        NativeImpl = impl;
        Template = null;
        CapturedEnv = null;
        FunctionPrototype = null;
    }

    /// <summary>
    /// Construct a native-backed function that has access to the
    /// VM — used by callback-taking built-ins like
    /// <c>Array.prototype.forEach</c>.
    /// </summary>
    public JsFunction(string name, Func<JsVM, object?, IReadOnlyList<object?>, object?> impl)
    {
        NativeName = name;
        NativeCallable = impl;
        Template = null;
        CapturedEnv = null;
        FunctionPrototype = null;
    }

    public override string ToString() =>
        Name is not null ? $"function {Name}() {{ [native code] }}" : "function () { [native code] }";
}
