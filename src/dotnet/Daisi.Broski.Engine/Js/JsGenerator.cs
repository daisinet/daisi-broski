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

        // Install next() / return() / throw() as native
        // methods on this specific generator instance.
        // They closure-capture the generator (as `self`) so
        // calls like `var n = gen.next; n()` keep working
        // — the identity comes from the captured reference,
        // not the runtime `this` receiver.
        var self = this;
        SetNonEnumerable("next", new JsFunction("next", (vm, thisCall, args) =>
        {
            object? sent = args.Count > 0 ? args[0] : JsValue.Undefined;
            return self.Next(sent);
        }));

        SetNonEnumerable("return", new JsFunction("return", (vm, thisCall, args) =>
        {
            object? value = args.Count > 0 ? args[0] : JsValue.Undefined;
            return self.ReturnMethod(value);
        }));

        SetNonEnumerable("throw", new JsFunction("throw", (vm, thisCall, args) =>
        {
            object? reason = args.Count > 0 ? args[0] : JsValue.Undefined;
            return self.ThrowMethod(reason);
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
    /// <c>gen.return(value)</c>. Forcibly completes the
    /// generator with <paramref name="value"/> as the final
    /// result. Any pending <c>finally</c> clauses do not
    /// run in this slice — the generator transitions
    /// straight to <see cref="GeneratorStatus.Completed"/>.
    /// Spec-faithful finally handling is a documented
    /// deferral.
    /// </summary>
    public JsObject ReturnMethod(object? value)
    {
        var result = new JsObject { Prototype = _engine.ObjectPrototype };
        _status = GeneratorStatus.Completed;
        result.Set("value", value);
        result.Set("done", JsValue.True);
        return result;
    }

    /// <summary>
    /// <c>gen.throw(reason)</c>. Injects a thrown value at
    /// the current yield point so a surrounding
    /// <c>try</c>/<c>catch</c> in the generator body can
    /// catch it. If uncaught the generator completes and
    /// the caller receives a rejected <see cref="JsObject"/>
    /// result (or the C# <see cref="JsRuntimeException"/>
    /// escapes, depending on how the body handles it).
    /// </summary>
    public JsObject ThrowMethod(object? reason)
    {
        var result = new JsObject { Prototype = _engine.ObjectPrototype };
        if (_status == GeneratorStatus.Completed || _status == GeneratorStatus.SuspendedStart)
        {
            // Before any yield has been reached the thrown
            // value escapes directly — caller sees an
            // uncaught exception.
            _status = GeneratorStatus.Completed;
            throw new JsRuntimeException(
                $"Uncaught {JsValue.ToJsString(reason)}",
                reason);
        }
        JsVM.GeneratorStepOutcome raw;
        try
        {
            raw = AdvanceRaw(reason, isThrow: true);
        }
        catch (JsRuntimeException)
        {
            _status = GeneratorStatus.Completed;
            throw;
        }
        result.Set("value", raw.Value);
        result.Set("done", raw.Done ? (object)JsValue.True : JsValue.False);
        return result;
    }

    /// <summary>
    /// Advance the generator by one step. Returns a fresh
    /// <c>{value, done}</c> result object matching the
    /// iterator protocol's shape.
    /// </summary>
    public JsObject Next(object? sentValue)
    {
        var raw = AdvanceRaw(sentValue, isThrow: false);
        var result = new JsObject { Prototype = _engine.ObjectPrototype };
        result.Set("value", raw.Value);
        result.Set("done", raw.Done ? (object)JsValue.True : JsValue.False);
        return result;
    }

    /// <summary>
    /// Drive the generator VM forward one step and return the
    /// raw outcome — the yielded / returned value and whether
    /// the generator has completed — without boxing it into a
    /// JS-visible <c>{value, done}</c> object. Used by the
    /// async stepper, which needs the raw value to decide how
    /// to chain onto the awaited promise.
    ///
    /// <paramref name="isThrow"/> selects between the normal
    /// resume path (push the sent value at the yield point)
    /// and the throw-injection path (call <c>DoThrow</c> at
    /// the yield point so <c>try</c>/<c>catch</c> around
    /// <c>await</c> observes the rejection as a thrown value).
    /// </summary>
    public JsVM.GeneratorStepOutcome AdvanceRaw(object? sentValue, bool isThrow)
    {
        if (_status == GeneratorStatus.Completed)
        {
            return new JsVM.GeneratorStepOutcome(JsValue.Undefined, done: true);
        }

        if (_status == GeneratorStatus.SuspendedStart)
        {
            // Starting a generator with a throw injects it
            // before any bytecode has run. There is no yield
            // point to throw at, so the generator simply
            // completes with the thrown value as an
            // exception that escapes via the outer driver.
            _vm.StartGeneratorExecution(_fn, _thisVal, _args);
            _status = GeneratorStatus.SuspendedYield;
            if (isThrow)
            {
                _vm.YieldResumeWithThrow = true;
                _vm.YieldThrownValue = sentValue;
            }
        }
        else
        {
            if (isThrow)
            {
                _vm.YieldResumeWithThrow = true;
                _vm.YieldThrownValue = sentValue;
            }
            else
            {
                _vm.YieldSentValue = sentValue;
            }
        }

        JsVM.GeneratorStepOutcome outcome;
        try
        {
            outcome = _vm.ContinueGeneratorExecution();
        }
        catch (JsRuntimeException)
        {
            // Uncaught exception inside the generator body —
            // mark complete and re-throw so the caller
            // (async stepper or user gen.next()) can handle.
            _status = GeneratorStatus.Completed;
            throw;
        }
        if (outcome.Done) _status = GeneratorStatus.Completed;
        return outcome;
    }
}
