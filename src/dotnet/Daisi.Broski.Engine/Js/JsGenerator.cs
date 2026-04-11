namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Runtime state of a generator object returned by calling
/// an ES2015 <c>function*</c>. Each generator owns a private
/// <see cref="JsVM"/> instance so its bytecode state (ip,
/// stack, frames, environment, handlers) can be suspended
/// at a <c>yield</c> and resumed by a subsequent
/// <c>gen.next()</c> without interfering with whatever
/// other VM activity is happening on the engine's main VM.
/// </summary>
internal enum GeneratorStatus
{
    SuspendedStart,
    SuspendedYield,
    Completed,
}

/// <summary>
/// ES2015 generator object — implements the iterator
/// protocol. The object doubles as its own iterator (the
/// spec-mandated <c>Generator.prototype[Symbol.iterator]</c>
/// returns <c>this</c>), and <c>next()</c> is a native
/// method installed on each instance that drives the
/// underlying per-generator VM one step at a time.
///
/// Slice 3b-7b scope:
/// <list type="bullet">
/// <item><c>gen.next()</c> — initial and subsequent advances.</item>
/// <item><c>gen.next(sent)</c> — the sent value flows back
///   into the yield expression as its result.</item>
/// <item>Spread / <c>for..of</c> consume generators like
///   any other iterable.</item>
/// </list>
///
/// Deferred: <c>yield*</c> delegation, <c>gen.return()</c>,
/// <c>gen.throw()</c>, async generators.
/// </summary>
public sealed class JsGenerator : JsObject
{
    private readonly JsEngine _engine;
    private readonly JsVM _vm;
    private readonly JsFunction _fn;
    private readonly object? _thisVal;
    private readonly object?[] _args;
    private GeneratorStatus _status = GeneratorStatus.SuspendedStart;

    public JsGenerator(JsEngine engine, JsFunction fn, object? thisVal, object?[] args)
    {
        _engine = engine;
        _fn = fn;
        _thisVal = thisVal;
        _args = args;
        Prototype = engine.ObjectPrototype;
        _vm = new JsVM(engine);

        // Install next() as a native-callable method that
        // drives this specific generator. Closure-captures
        // `this` (the JsGenerator instance) so calls like
        // `var n = gen.next; n()` work — the generator is
        // identified by the closed-over instance, not by the
        // runtime `this` receiver.
        var self = this;
        SetNonEnumerable(
            "next",
            new JsFunction(
                "next",
                (vm, thisCall, args) =>
                {
                    object? sent = args.Count > 0 ? args[0] : JsValue.Undefined;
                    return self.Next(sent);
                }));

        // Generators are their own iterator — per spec,
        // `gen[Symbol.iterator]()` returns the generator
        // itself.
        SetSymbol(
            engine.IteratorSymbol,
            new JsFunction(
                "[Symbol.iterator]",
                (thisCall, args) => self));
    }

    /// <summary>
    /// Advance the generator by one step. Returns a fresh
    /// <c>{value, done}</c> result object matching the
    /// iterator protocol's shape.
    /// </summary>
    public JsObject Next(object? sentValue)
    {
        var result = new JsObject { Prototype = _engine.ObjectPrototype };

        if (_status == GeneratorStatus.Completed)
        {
            result.Set("value", JsValue.Undefined);
            result.Set("done", JsValue.True);
            return result;
        }

        if (_status == GeneratorStatus.SuspendedStart)
        {
            _vm.StartGeneratorExecution(_fn, _thisVal, _args);
            _status = GeneratorStatus.SuspendedYield;
        }
        else
        {
            _vm.YieldSentValue = sentValue;
        }

        var outcome = _vm.ContinueGeneratorExecution();
        if (outcome.Done)
        {
            _status = GeneratorStatus.Completed;
            result.Set("value", outcome.Value);
            result.Set("done", JsValue.True);
        }
        else
        {
            result.Set("value", outcome.Value);
            result.Set("done", JsValue.False);
        }
        return result;
    }
}
