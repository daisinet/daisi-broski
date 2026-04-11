namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Stack-based bytecode interpreter for the phase-3a VM. Executes a
/// <see cref="Chunk"/> produced by <see cref="JsCompiler"/> against a
/// globals dictionary that the caller supplies (and can inspect
/// afterwards for assertions).
///
/// The VM is a classic "big switch" dispatch loop. Each opcode reads
/// its operands (if any) from the code stream, pops its inputs, and
/// pushes its output. The one piece of state beyond the stack and
/// globals is <see cref="CompletionValue"/>: a single slot that
/// <see cref="OpCode.StoreCompletion"/> writes to, used by the engine
/// to return the last-evaluated top-level expression's value.
///
/// Semantics match ECMA §11 for the operators we implement. Edge
/// cases the slice-3 scope deliberately approximates:
///
/// - <c>+</c> with an object operand falls through to numeric
///   addition because we have no <c>ToPrimitive</c> path yet. No
///   objects currently reach the VM, so this is unreachable.
/// - <c>&lt;</c> / <c>&lt;=</c> / <c>&gt;</c> / <c>&gt;=</c> between
///   two strings compare lexicographically (ordinal). Between a
///   string and a number, both sides are coerced to number.
/// - <c>Number(string)</c> uses .NET's <c>double.TryParse</c>
///   with invariant culture — good enough for decimal and
///   scientific notation, less strict than the spec's
///   <c>StringNumericLiteral</c> grammar.
/// </summary>
public sealed class JsVM
{
    // Reference to the owning engine so we can reach the
    // well-known prototypes (ArrayPrototype, StringPrototype, ...)
    // when constructing arrays and resolving property access on
    // primitive values.
    private readonly JsEngine _engine;

    // Per-execution-context view of the currently running chunk.
    // These change on every Call/Return via the _frames stack.
    private byte[] _code;
    private IReadOnlyList<object?> _constants;
    private IReadOnlyList<string> _names;
    private int _ip;

    // Current lexical environment and `this` binding. Also saved/
    // restored via the call frame stack.
    private JsEnvironment _env;
    private object? _this;

    // Value stack. 4096 is generous for slice 4b — function calls
    // push args on this stack, so depth grows with recursion.
    // Stack overflow will blow up the test suite rather than the
    // user's app, which is fine for phase 3a.
    private readonly object?[] _stack = new object?[4096];
    private int _sp;

    // Single compiler-owned scratch slot for postfix-update
    // sequences. Not observable to user code.
    private object? _scratch;

    // Call frame stack. Each entry captures the state we need to
    // restore on Return: code pointer, constant / name pools, ip,
    // env, this, plus constructor metadata for `new`.
    private readonly Stack<CallFrame> _frames = new();

    // Exception handler stack + pending-exception slot. Handlers
    // are registered by PushCatchHandler / PushFinallyHandler and
    // consumed on Throw. A finally-only handler sets
    // <see cref="_pendingException"/> when it fires; EndFinally
    // checks that slot and re-throws if non-null.
    private readonly Stack<HandlerRecord> _handlers = new();
    private object? _pendingException;

    // Stack of frame-depth boundaries that mark where a native
    // call was entered from JS. When <see cref="DoThrow"/> needs
    // to unwind past one of these boundaries, it escapes via a
    // <see cref="JsThrowSignal"/> instead, so the .NET exception
    // propagates through the native call. The outer
    // <see cref="InvokeFunction"/> catches the signal and resumes
    // the unwind from a safer context. Managed by
    // <see cref="InvokeJsFunction"/>.
    private readonly Stack<int> _nativeBoundaries = new();

    // Set by the Halt opcode; read by the dispatch loop to exit
    // cleanly at the end of the top-level program.
    private bool _halted;

    private sealed class HandlerRecord
    {
        public int HandlerIp;
        public bool IsCatch;
        public int StackBase;
        public int FrameDepth;
        public JsEnvironment Env = null!;
    }

    private sealed class CallFrame
    {
        public byte[] Code = null!;
        public IReadOnlyList<object?> Constants = null!;
        public IReadOnlyList<string> Names = null!;
        public int Ip;
        public JsEnvironment Env = null!;
        public object? This;
        // For `new`: whether this frame was entered via New, and
        // the instance allocated at call time. Used at Return to
        // choose between the constructor's return value and the
        // allocated instance per ECMA §13.2.2.
        public bool IsConstructor;
        public JsObject? NewInstance;
    }

    /// <summary>
    /// The most recent value stored by a
    /// <see cref="OpCode.StoreCompletion"/>, or <c>undefined</c> if
    /// the program had no top-level expression statements.
    /// </summary>
    public object? CompletionValue { get; private set; } = JsValue.Undefined;

    public JsVM(JsEngine engine)
    {
        _engine = engine;
        // Start with empty code; RunChunk replaces it on demand.
        _code = Array.Empty<byte>();
        _constants = Array.Empty<object?>();
        _names = Array.Empty<string>();
        // The globals env wraps the engine's globals dictionary
        // so mutations made by the VM are observable through
        // <see cref="JsEngine.Globals"/>. This env persists
        // across successive <see cref="RunChunk"/> calls so
        // event-loop tasks scheduled from one script can read
        // the bindings another script established.
        _env = new JsEnvironment(engine.Globals, parent: null);
        _this = JsValue.Undefined;
    }

    /// <summary>
    /// Run a compiled chunk to completion against this VM's
    /// persistent environment. Each call replaces the current
    /// code / IP / constant-pool view but reuses the globals
    /// env, so successive invocations see each other's
    /// bindings. Returns the top-level completion value per
    /// ECMA §14. Throws <see cref="JsRuntimeException"/> on
    /// an uncaught runtime error.
    /// </summary>
    public object? RunChunk(Chunk chunk)
    {
        _code = chunk.Code;
        _constants = chunk.Constants;
        _names = chunk.Names;
        _ip = 0;
        _sp = 0;
        _halted = false;
        _handlers.Clear();
        _frames.Clear();
        _pendingException = null;
        _this = JsValue.Undefined;
        // Reset the env to the globals env — any nested function
        // scopes from a prior run are no longer live and we
        // don't want their bindings leaking into this run.
        while (_env.Parent is not null) _env = _env.Parent;
        RunLoop(stopFrameDepth: -1);
        return CompletionValue;
    }

    /// <summary>
    /// Invoke a JS function synchronously from a native built-in.
    /// Used by callback-taking methods like
    /// <c>Array.prototype.forEach</c> to run a user-supplied
    /// callback. Native callees run inline; user functions run
    /// via a nested <see cref="RunLoop"/> invocation that stops
    /// when the call's return pops the pushed frame. Exceptions
    /// that would unwind past the caller's frame depth escape
    /// via a <see cref="JsThrowSignal"/> that bubbles up through
    /// the native call.
    /// </summary>
    public object? InvokeJsFunction(
        JsFunction fn,
        object? thisVal,
        IReadOnlyList<object?> args)
    {
        if (fn.NativeImpl is not null)
        {
            return fn.NativeImpl(thisVal, args);
        }
        if (fn.NativeCallable is not null)
        {
            return fn.NativeCallable(this, thisVal, args);
        }

        int enterDepth = _frames.Count;

        // Snapshot the caller's VM state so we can restore it
        // exactly if the nested call aborts via a cross-boundary
        // throw. On the normal path, DoCall / Return take care of
        // this via the call frame, but on abort the escape
        // happens BEFORE the nested frame is popped by Return,
        // so we can't rely on that.
        var savedCode = _code;
        var savedConstants = _constants;
        var savedNames = _names;
        int savedIp = _ip;
        var savedEnv = _env;
        var savedThis = _this;
        int savedSp = _sp;

        // Push call operands: [fn, this, arg0, ..., argN-1]
        Push(fn);
        Push(thisVal);
        for (int i = 0; i < args.Count; i++) Push(args[i]);

        _nativeBoundaries.Push(enterDepth);
        try
        {
            DoCall(args.Count);
            RunLoop(stopFrameDepth: enterDepth);
            return Pop();
        }
        catch (JsThrowSignal)
        {
            // Nested call aborted via a cross-boundary throw.
            // Discard any frames / handlers that belonged to the
            // nested invocation and restore the caller's VM state
            // so the outer context sees consistent state on
            // re-raise.
            while (_frames.Count > enterDepth) _frames.Pop();
            while (_handlers.Count > 0 &&
                   _handlers.Peek().FrameDepth > enterDepth)
            {
                _handlers.Pop();
            }
            _code = savedCode;
            _constants = savedConstants;
            _names = savedNames;
            _ip = savedIp;
            _env = savedEnv;
            _this = savedThis;
            _sp = savedSp;
            _pendingException = null;

            // Re-throw so a parent native-call frame can catch
            // the signal and dispatch it through DoThrow from
            // its own (safer) context. If there is no parent
            // frame — i.e., we're running from the event loop
            // or another host entry point — the signal
            // escapes all the way to <see cref="JsEngine"/>,
            // which is responsible for converting it to a
            // <see cref="JsRuntimeException"/>.
            throw;
        }
        finally
        {
            _nativeBoundaries.Pop();
        }
    }

    private void RunLoop(int stopFrameDepth)
    {
        while (true)
        {
            // Halt only terminates the outermost dispatch loop
            // (stopFrameDepth < 0). Nested invocations set a
            // concrete frame-depth exit condition and ignore
            // _halted, which stays set from a prior RunChunk so
            // the script's completion isn't lost.
            if (stopFrameDepth < 0)
            {
                if (_halted) return;
            }
            else
            {
                if (_frames.Count <= stopFrameDepth) return;
            }

            var op = (OpCode)_code[_ip++];
            switch (op)
            {
                case OpCode.Nop:
                    break;

                // ---- Constants ----
                case OpCode.PushUndefined: Push(JsValue.Undefined); break;
                case OpCode.PushNull: Push(JsValue.Null); break;
                case OpCode.PushTrue: Push(JsValue.True); break;
                case OpCode.PushFalse: Push(JsValue.False); break;
                case OpCode.PushConst:
                    Push(_constants[ReadU16()]);
                    break;

                // ---- Stack ----
                case OpCode.Pop: _sp--; break;
                case OpCode.Dup:
                    _stack[_sp] = _stack[_sp - 1];
                    _sp++;
                    break;
                case OpCode.Dup2:
                    // [..., a, b] -> [..., a, b, a, b]
                    _stack[_sp] = _stack[_sp - 2];
                    _stack[_sp + 1] = _stack[_sp - 1];
                    _sp += 2;
                    break;
                case OpCode.StoreScratch:
                    _scratch = Pop();
                    break;
                case OpCode.LoadScratch:
                    Push(_scratch);
                    break;

                // ---- Name resolution (walks the env chain) ----
                case OpCode.LoadGlobal:
                    {
                        var name = _names[ReadU16()];
                        if (!_env.TryResolve(name, out var v))
                        {
                            RaiseError("ReferenceError", $"{name} is not defined");
                            break;
                        }
                        if (v is JsUninitialized)
                        {
                            RaiseError("ReferenceError",
                                $"Cannot access '{name}' before initialization");
                            break;
                        }
                        Push(v);
                    }
                    break;
                case OpCode.LoadGlobalOrUndefined:
                    {
                        // Used by `typeof identifier`. For a
                        // completely undeclared name we return
                        // undefined — but per ECMA §13.3.1, a
                        // TDZ binding throws even through
                        // typeof.
                        var name = _names[ReadU16()];
                        if (_env.TryResolve(name, out var v))
                        {
                            if (v is JsUninitialized)
                            {
                                RaiseError("ReferenceError",
                                    $"Cannot access '{name}' before initialization");
                                break;
                            }
                            Push(v);
                        }
                        else
                        {
                            Push(JsValue.Undefined);
                        }
                    }
                    break;
                case OpCode.StoreGlobal:
                    {
                        var name = _names[ReadU16()];
                        _env.Assign(name, _stack[_sp - 1]);
                    }
                    break;
                case OpCode.DeclareGlobal:
                    {
                        // Declares in the CURRENT env — at top level
                        // that's globals, inside a function it's the
                        // function's own scope.
                        var name = _names[ReadU16()];
                        _env.DeclareLocal(name);
                    }
                    break;
                case OpCode.DeclareLet:
                    {
                        // Create a block-scoped binding in the
                        // current env initialized to the TDZ
                        // sentinel. Unconditional overwrite:
                        // a fresh block always introduces fresh
                        // let/const bindings, shadowing any
                        // outer binding with the same name.
                        var name = _names[ReadU16()];
                        _env.Bindings[name] = JsValue.Uninitialized;
                    }
                    break;
                case OpCode.DeleteGlobal:
                    {
                        var name = _names[ReadU16()];
                        // Non-strict delete: returns false for any
                        // name bound anywhere up the env chain,
                        // true otherwise. The binding stays in
                        // place — delete on a declared var is a
                        // no-op per ECMA §11.4.1.
                        Push(_env.TryResolve(name, out _) ? JsValue.False : JsValue.True);
                    }
                    break;

                // ---- Functions / calls / constructors ----
                case OpCode.MakeFunction:
                    {
                        var template = (JsFunctionTemplate)_constants[ReadU16()]!;
                        var fn = new JsFunction(template, _env)
                        {
                            Prototype = _engine.FunctionPrototype,
                        };
                        if (template.IsArrow)
                        {
                            // Arrow functions snapshot the
                            // surrounding `this` at creation
                            // time — they never bind their
                            // own `this` at call time.
                            fn.CapturedThis = _this;
                        }
                        Push(fn);
                    }
                    break;
                case OpCode.Call:
                    {
                        int argc = _code[_ip++];
                        DoCall(argc);
                    }
                    break;
                case OpCode.New:
                    {
                        int argc = _code[_ip++];
                        DoNew(argc);
                    }
                    break;
                case OpCode.Return:
                    {
                        var returnValue = Pop();
                        if (_frames.Count == 0)
                        {
                            // Top-level return — shouldn't happen
                            // because the compiler rejects it.
                            _halted = true;
                            break;
                        }
                        // Drop any handlers installed in this frame —
                        // we're returning normally, so they no
                        // longer apply.
                        while (_handlers.Count > 0 &&
                               _handlers.Peek().FrameDepth == _frames.Count)
                        {
                            _handlers.Pop();
                        }
                        var frame = _frames.Pop();
                        // Restore caller state.
                        _code = frame.Code;
                        _constants = frame.Constants;
                        _names = frame.Names;
                        _ip = frame.Ip;
                        _env = frame.Env;
                        _this = frame.This;
                        // Push the result onto the caller's stack.
                        // For a constructor: if the function
                        // returned a non-object, substitute the
                        // freshly allocated instance instead.
                        if (frame.IsConstructor && returnValue is not JsObject)
                        {
                            Push(frame.NewInstance);
                        }
                        else
                        {
                            Push(returnValue);
                        }
                    }
                    break;
                case OpCode.Instanceof:
                    {
                        var ctor = Pop();
                        var obj = Pop();
                        if (ctor is not JsFunction fn)
                        {
                            RaiseError("TypeError",
                                "Right-hand side of instanceof is not a function");
                            break;
                        }
                        Push(IsInstanceOf(obj, fn));
                    }
                    break;
                case OpCode.LoadThis:
                    Push(_this);
                    break;
                case OpCode.Swap:
                    {
                        var top = _stack[_sp - 1];
                        _stack[_sp - 1] = _stack[_sp - 2];
                        _stack[_sp - 2] = top;
                    }
                    break;

                // ---- Exception handling ----
                case OpCode.PushCatchHandler:
                    {
                        short offset = ReadS16();
                        _handlers.Push(new HandlerRecord
                        {
                            HandlerIp = _ip + offset,
                            IsCatch = true,
                            StackBase = _sp,
                            FrameDepth = _frames.Count,
                            Env = _env,
                        });
                    }
                    break;
                case OpCode.PushFinallyHandler:
                    {
                        short offset = ReadS16();
                        _handlers.Push(new HandlerRecord
                        {
                            HandlerIp = _ip + offset,
                            IsCatch = false,
                            StackBase = _sp,
                            FrameDepth = _frames.Count,
                            Env = _env,
                        });
                    }
                    break;
                case OpCode.PopHandler:
                    _handlers.Pop();
                    break;
                case OpCode.Throw:
                    {
                        var value = Pop();
                        DoThrow(value);
                    }
                    break;
                case OpCode.EndFinally:
                    if (_pendingException is not null)
                    {
                        var pending = _pendingException;
                        _pendingException = null;
                        DoThrow(pending);
                    }
                    break;
                case OpCode.PushEnv:
                    _env = new JsEnvironment(_env);
                    break;
                case OpCode.PopEnv:
                    _env = _env.Parent!;
                    break;

                // ---- for-in iteration ----
                case OpCode.ForInStart:
                    {
                        var target = Pop();
                        Push(ForInIterator.From(target));
                    }
                    break;
                case OpCode.ForInNext:
                    {
                        short offset = ReadS16();
                        var iter = (ForInIterator)_stack[_sp - 1]!;
                        if (iter.Index < iter.Keys.Count)
                        {
                            Push(iter.Keys[iter.Index]);
                            iter.Index++;
                        }
                        else
                        {
                            // Done — discard the iterator and jump out.
                            _sp--;
                            _ip += offset;
                        }
                    }
                    break;

                // ---- Arithmetic ----
                case OpCode.Add: DoAdd(); break;
                case OpCode.Sub: DoNumericBinary((a, b) => a - b); break;
                case OpCode.Mul: DoNumericBinary((a, b) => a * b); break;
                case OpCode.Div: DoNumericBinary((a, b) => a / b); break;
                case OpCode.Mod: DoNumericBinary((a, b) => a % b); break;
                case OpCode.Negate:
                    _stack[_sp - 1] = -JsValue.ToNumber(_stack[_sp - 1]);
                    break;
                case OpCode.UnaryPlus:
                    _stack[_sp - 1] = JsValue.ToNumber(_stack[_sp - 1]);
                    break;

                // ---- Bitwise ----
                case OpCode.BitAnd: DoIntBinary((a, b) => a & b); break;
                case OpCode.BitOr: DoIntBinary((a, b) => a | b); break;
                case OpCode.BitXor: DoIntBinary((a, b) => a ^ b); break;
                case OpCode.BitNot:
                    _stack[_sp - 1] = (double)(~JsValue.ToInt32(_stack[_sp - 1]));
                    break;
                case OpCode.Shl:
                    {
                        var b = Pop();
                        var a = Pop();
                        int left = JsValue.ToInt32(a);
                        int shift = (int)(JsValue.ToUint32(b) & 31);
                        Push((double)(left << shift));
                    }
                    break;
                case OpCode.Shr:
                    {
                        var b = Pop();
                        var a = Pop();
                        int left = JsValue.ToInt32(a);
                        int shift = (int)(JsValue.ToUint32(b) & 31);
                        Push((double)(left >> shift));
                    }
                    break;
                case OpCode.Ushr:
                    {
                        var b = Pop();
                        var a = Pop();
                        uint left = JsValue.ToUint32(a);
                        int shift = (int)(JsValue.ToUint32(b) & 31);
                        Push((double)(left >> shift));
                    }
                    break;

                // ---- Comparison ----
                case OpCode.LooseEq:
                    {
                        var b = Pop();
                        var a = Pop();
                        Push(JsValue.LooseEquals(a, b));
                    }
                    break;
                case OpCode.LooseNotEq:
                    {
                        var b = Pop();
                        var a = Pop();
                        Push(!JsValue.LooseEquals(a, b));
                    }
                    break;
                case OpCode.StrictEq:
                    {
                        var b = Pop();
                        var a = Pop();
                        Push(JsValue.StrictEquals(a, b));
                    }
                    break;
                case OpCode.StrictNotEq:
                    {
                        var b = Pop();
                        var a = Pop();
                        Push(!JsValue.StrictEquals(a, b));
                    }
                    break;
                case OpCode.Lt: DoRelational((a, b) => a < b); break;
                case OpCode.Le: DoRelational((a, b) => a <= b); break;
                case OpCode.Gt: DoRelational((a, b) => a > b); break;
                case OpCode.Ge: DoRelational((a, b) => a >= b); break;

                // ---- Logical ----
                case OpCode.LogicalNot:
                    _stack[_sp - 1] = !JsValue.ToBoolean(_stack[_sp - 1]);
                    break;

                // ---- Type ----
                case OpCode.TypeOf:
                    _stack[_sp - 1] = JsValue.TypeOf(_stack[_sp - 1]);
                    break;
                case OpCode.In:
                    {
                        var obj = Pop();
                        var key = Pop();
                        if (obj is not JsObject jo)
                        {
                            RaiseError("TypeError",
                                "Cannot use 'in' operator on non-object");
                            break;
                        }
                        Push(jo.Has(JsValue.ToJsString(key)));
                    }
                    break;

                // ---- Object / array construction ----
                case OpCode.CreateObject:
                    Push(new JsObject { Prototype = _engine.ObjectPrototype });
                    break;
                case OpCode.CreateArray:
                    {
                        int count = ReadU16();
                        var arr = new JsArray { Prototype = _engine.ArrayPrototype };
                        // Elements were pushed bottom-to-top, so we
                        // pop them in reverse to rebuild the order.
                        for (int i = count - 1; i >= 0; i--)
                        {
                            arr.Elements.Add(JsValue.Undefined);
                        }
                        for (int i = count - 1; i >= 0; i--)
                        {
                            arr.Elements[i] = Pop();
                        }
                        Push(arr);
                    }
                    break;
                case OpCode.InitProperty:
                    {
                        // [obj, value] -> [obj], side effect: obj[name] = value.
                        var name = _names[ReadU16()];
                        var value = _stack[_sp - 1];
                        var obj = (JsObject)_stack[_sp - 2]!;
                        obj.Set(name, value);
                        _sp--;
                    }
                    break;

                // ---- Member access ----
                case OpCode.GetProperty:
                    {
                        var name = _names[ReadU16()];
                        var target = _stack[_sp - 1];
                        if (TryLookupProperty(target, name, out var value))
                        {
                            _stack[_sp - 1] = value;
                        }
                        // else: RaiseError moved _ip to the handler;
                        // skip assignment and let dispatch fall through.
                    }
                    break;
                case OpCode.GetPropertyComputed:
                    {
                        var key = Pop();
                        var target = _stack[_sp - 1];
                        if (TryLookupProperty(target, JsValue.ToJsString(key), out var value))
                        {
                            _stack[_sp - 1] = value;
                        }
                    }
                    break;
                case OpCode.SetProperty:
                    {
                        // [obj, value] -> [value], side effect: obj[name] = value.
                        var name = _names[ReadU16()];
                        var value = _stack[_sp - 1];
                        var target = _stack[_sp - 2];
                        if (target is not JsObject jo)
                        {
                            RaiseError("TypeError",
                                $"Cannot assign property '{name}' on {JsValue.ToJsString(target)}");
                            break;
                        }
                        jo.Set(name, value);
                        _stack[_sp - 2] = value;
                        _sp--;
                    }
                    break;
                case OpCode.SetPropertyComputed:
                    {
                        // [obj, key, value] -> [value].
                        var value = _stack[_sp - 1];
                        var key = _stack[_sp - 2];
                        var target = _stack[_sp - 3];
                        if (target is not JsObject jo)
                        {
                            RaiseError("TypeError",
                                $"Cannot assign property on {JsValue.ToJsString(target)}");
                            break;
                        }
                        jo.Set(JsValue.ToJsString(key), value);
                        _stack[_sp - 3] = value;
                        _sp -= 2;
                    }
                    break;
                case OpCode.DeleteProperty:
                    {
                        var name = _names[ReadU16()];
                        var target = Pop();
                        if (target is JsObject jo)
                        {
                            Push(jo.Delete(name));
                        }
                        else
                        {
                            // Non-object target — ECMA §11.4.1 returns true.
                            Push(JsValue.True);
                        }
                    }
                    break;
                case OpCode.DeletePropertyComputed:
                    {
                        var key = Pop();
                        var target = Pop();
                        if (target is JsObject jo)
                        {
                            Push(jo.Delete(JsValue.ToJsString(key)));
                        }
                        else
                        {
                            Push(JsValue.True);
                        }
                    }
                    break;

                // ---- Control flow ----
                case OpCode.Jump:
                    {
                        short offset = ReadS16();
                        _ip += offset;
                    }
                    break;
                case OpCode.JumpIfFalse:
                    {
                        short offset = ReadS16();
                        if (!JsValue.ToBoolean(Pop())) _ip += offset;
                    }
                    break;
                case OpCode.JumpIfTrue:
                    {
                        short offset = ReadS16();
                        if (JsValue.ToBoolean(Pop())) _ip += offset;
                    }
                    break;
                case OpCode.JumpIfFalseKeep:
                    {
                        short offset = ReadS16();
                        if (!JsValue.ToBoolean(_stack[_sp - 1])) _ip += offset;
                    }
                    break;
                case OpCode.JumpIfTrueKeep:
                    {
                        short offset = ReadS16();
                        if (JsValue.ToBoolean(_stack[_sp - 1])) _ip += offset;
                    }
                    break;

                // ---- Completion ----
                case OpCode.StoreCompletion:
                    CompletionValue = Pop();
                    break;

                case OpCode.Halt:
                    _halted = true;
                    break;

                default:
                    throw new JsRuntimeException($"Unknown opcode: {op}");
            }
        }
    }

    // -------------------------------------------------------------------
    // Stack helpers
    // -------------------------------------------------------------------

    private void Push(object? v)
    {
        _stack[_sp++] = v;
    }

    private object? Pop()
    {
        return _stack[--_sp];
    }

    private ushort ReadU16()
    {
        ushort v = (ushort)(_code[_ip] | (_code[_ip + 1] << 8));
        _ip += 2;
        return v;
    }

    private short ReadS16() => (short)ReadU16();

    // -------------------------------------------------------------------
    // Binary op helpers
    // -------------------------------------------------------------------

    /// <summary>
    /// <c>+</c> is the one operator whose semantics branch on operand
    /// type: if either side is a string (after <c>ToPrimitive</c>,
    /// which we skip because there are no objects), concatenate;
    /// otherwise, coerce both to number and add.
    /// </summary>
    private void DoAdd()
    {
        var b = Pop();
        var a = Pop();
        if (a is string || b is string)
        {
            Push(JsValue.ToJsString(a) + JsValue.ToJsString(b));
        }
        else
        {
            Push(JsValue.ToNumber(a) + JsValue.ToNumber(b));
        }
    }

    private void DoNumericBinary(Func<double, double, double> fn)
    {
        var b = Pop();
        var a = Pop();
        Push(fn(JsValue.ToNumber(a), JsValue.ToNumber(b)));
    }

    /// <summary>
    /// Shared helper for the integer-valued binary operators
    /// (<c>&amp;</c>, <c>|</c>, <c>^</c>, <c>&lt;&lt;</c>,
    /// <c>&gt;&gt;</c>). Both operands are coerced to Int32; the
    /// result is also Int32, then re-boxed as a JS Number (double).
    /// </summary>
    private void DoIntBinary(Func<int, int, int> fn)
    {
        var b = Pop();
        var a = Pop();
        int result = fn(JsValue.ToInt32(a), JsValue.ToInt32(b));
        Push((double)result);
    }

    // -------------------------------------------------------------------
    // Exception handling
    // -------------------------------------------------------------------

    /// <summary>
    /// Unwind handlers and call frames to find the nearest
    /// <c>try</c> handler for <paramref name="value"/>. On success,
    /// restores the stack and env to the state at handler-push
    /// time and jumps execution to the handler target. If no
    /// handler can be reached without crossing a native-call
    /// boundary (set up by <see cref="InvokeJsFunction"/>),
    /// escapes via a <see cref="JsThrowSignal"/> that the outer
    /// native call's wrapper catches and re-routes. If no
    /// handler exists anywhere, throws
    /// <see cref="JsRuntimeException"/> with the value attached.
    /// </summary>
    private void DoThrow(object? value)
    {
        // Frame depth below which we cannot unwind safely —
        // doing so would discard a frame that a native call
        // farther up the .NET stack is still running in. If
        // there are no active native boundaries, we can unwind
        // all the way down to frame 0 (the top-level program).
        int safeMin = _nativeBoundaries.Count > 0 ? _nativeBoundaries.Peek() : -1;

        while (true)
        {
            // Drop any stale handlers whose frame has been
            // unwound past.
            while (_handlers.Count > 0 &&
                   _handlers.Peek().FrameDepth > _frames.Count)
            {
                _handlers.Pop();
            }

            if (_handlers.Count > 0 &&
                _handlers.Peek().FrameDepth == _frames.Count)
            {
                // A handler exists in the current frame.
                // Reaching it requires activating it now; this
                // is safe iff the current frame itself is safe
                // to unwind (i.e., deeper than safeMin).
                if (_frames.Count <= safeMin)
                {
                    // The handler is in a frame that a native
                    // call above us owns — escape through the
                    // native call.
                    throw new JsThrowSignal(value);
                }

                var h = _handlers.Pop();
                _sp = h.StackBase;
                _env = h.Env;
                _ip = h.HandlerIp;
                if (h.IsCatch)
                {
                    Push(value);
                    _pendingException = null;
                }
                else
                {
                    _pendingException = value;
                }
                return;
            }

            // No handler in the current frame — unwind to the
            // caller if possible, else escape.
            if (_frames.Count == 0)
            {
                throw new JsRuntimeException(
                    $"Uncaught {JsValue.ToJsString(value)}",
                    value);
            }
            if (_frames.Count <= safeMin + 1)
            {
                // Unwinding one more frame would put us at or
                // below the native boundary. Escape instead.
                throw new JsThrowSignal(value);
            }

            var frame = _frames.Pop();
            _code = frame.Code;
            _constants = frame.Constants;
            _names = frame.Names;
            _ip = frame.Ip;
            _env = frame.Env;
            _this = frame.This;
        }
    }

    /// <summary>
    /// Raise a catchable JS error with the given <c>name</c> and
    /// <c>message</c>. Constructs a <see cref="JsObject"/> linked
    /// to the appropriate error prototype on the engine (so
    /// <c>e instanceof TypeError</c> works from script), sets
    /// <c>message</c>, and routes it through <see cref="DoThrow"/>.
    /// </summary>
    private void RaiseError(string name, string message)
    {
        var err = new JsObject
        {
            Prototype = name switch
            {
                "TypeError" => _engine.TypeErrorPrototype,
                "RangeError" => _engine.RangeErrorPrototype,
                "SyntaxError" => _engine.SyntaxErrorPrototype,
                "ReferenceError" => _engine.ReferenceErrorPrototype,
                _ => _engine.ErrorPrototype,
            },
        };
        err.Set("message", message);
        DoThrow(err);
    }

    /// <summary>
    /// Resolve a property access for a <see cref="OpCode.GetProperty"/>
    /// or <see cref="OpCode.GetPropertyComputed"/> opcode. Supports:
    ///
    /// - Object / array / function targets via the usual
    ///   prototype-chain walk.
    /// - String primitives: <c>length</c>, numeric indices (as
    ///   single-char strings), and prototype-chain methods on
    ///   <see cref="JsEngine.StringPrototype"/>.
    /// - Number primitives: delegate to
    ///   <see cref="JsEngine.NumberPrototype"/>.
    /// - Boolean primitives: delegate to
    ///   <see cref="JsEngine.BooleanPrototype"/>.
    /// - <c>null</c> / <c>undefined</c>: raise a
    ///   <c>TypeError</c> and return <c>false</c>; the caller
    ///   must not overwrite the stack slot because
    ///   <see cref="DoThrow"/> already unwound <c>_ip</c> to the
    ///   handler.
    /// </summary>
    private bool TryLookupProperty(object? target, string name, out object? value)
    {
        if (target is JsObject jo)
        {
            value = jo.Get(name);
            return true;
        }
        if (target is string s)
        {
            value = LookupStringProperty(s, name);
            return true;
        }
        if (target is double)
        {
            value = _engine.NumberPrototype.Get(name);
            return true;
        }
        if (target is bool)
        {
            value = _engine.BooleanPrototype.Get(name);
            return true;
        }
        // null, undefined, any other primitive → TypeError.
        RaiseError("TypeError",
            $"Cannot read property '{name}' of {JsValue.ToJsString(target)}");
        value = null;
        return false;
    }

    /// <summary>
    /// String primitive property lookup. <c>"abc".length</c>
    /// returns 3; <c>"abc"[1]</c> returns <c>"b"</c>. Anything
    /// else walks <see cref="JsEngine.StringPrototype"/>.
    /// </summary>
    private object? LookupStringProperty(string s, string name)
    {
        if (name == "length") return (double)s.Length;
        if (IsCanonicalIndex(name, out int idx) && idx >= 0 && idx < s.Length)
        {
            return s[idx].ToString();
        }
        return _engine.StringPrototype.Get(name);
    }

    /// <summary>
    /// Only canonical integer strings (<c>"0"</c>, <c>"1"</c>,
    /// ...) are valid array indices — <c>"01"</c>, <c>"1.0"</c>,
    /// and <c>"  1"</c> are not. Matches the spec definition of
    /// an "array index" in ES2015+.
    /// </summary>
    private static bool IsCanonicalIndex(string s, out int idx)
    {
        idx = 0;
        if (s.Length == 0) return false;
        if (s == "0") { idx = 0; return true; }
        if (s[0] == '0') return false;
        foreach (var c in s)
        {
            if (c < '0' || c > '9') return false;
        }
        return int.TryParse(s, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out idx);
    }

    // -------------------------------------------------------------------
    // Call / New / Return helpers
    // -------------------------------------------------------------------

    /// <summary>
    /// Execute a <see cref="OpCode.Call"/>. Pops the <paramref name="argc"/>
    /// argument values plus a <c>this</c> value plus the function
    /// itself off the stack, then either dispatches to the native
    /// implementation or sets up a new call frame and jumps into
    /// the function's bytecode.
    /// </summary>
    private void DoCall(int argc)
    {
        // Stack layout: [..., fn, this, arg0, ..., arg(argc-1)]
        int argsBase = _sp - argc;
        var args = CollectArgs(argc, argsBase);
        _sp -= argc;                  // remove args
        var thisVal = Pop();          // remove this
        var calleeValue = Pop();      // remove fn

        if (calleeValue is not JsFunction fn)
        {
            RaiseError("TypeError",
                $"{JsValue.ToJsString(calleeValue)} is not a function");
            return;
        }

        InvokeFunction(fn, thisVal, args, isConstructor: false, newInstance: null);
    }

    /// <summary>
    /// Execute a <see cref="OpCode.New"/>. Like <see cref="DoCall"/>
    /// but allocates a fresh instance and uses it as <c>this</c>,
    /// linking the prototype per ECMA §13.2.2.
    /// </summary>
    private void DoNew(int argc)
    {
        // Stack layout: [..., fn, arg0, ..., arg(argc-1)]
        int argsBase = _sp - argc;
        var args = CollectArgs(argc, argsBase);
        _sp -= argc;
        var calleeValue = Pop();

        if (calleeValue is not JsFunction fn)
        {
            RaiseError("TypeError",
                $"{JsValue.ToJsString(calleeValue)} is not a constructor");
            return;
        }
        if (fn.Template?.IsArrow == true)
        {
            RaiseError("TypeError",
                "Arrow functions cannot be used as constructors");
            return;
        }

        // Native constructors (like Array / String) are
        // responsible for returning a fully-formed object. We
        // still allocate an instance linked to fn.prototype so
        // that a native function declining to take over gets a
        // reasonable this binding, but since ECMA §13.2.2 says
        // the return value wins when it is an object, native
        // factories that return a JsObject effectively discard
        // the pre-allocated one.
        var protoValue = fn.Get("prototype");
        var instance = new JsObject
        {
            Prototype = protoValue as JsObject ?? _engine.ObjectPrototype,
        };
        InvokeFunction(fn, instance, args, isConstructor: true, newInstance: instance);
    }

    /// <summary>
    /// Core call machinery shared by <see cref="DoCall"/> and
    /// <see cref="DoNew"/>. For native functions, dispatches to
    /// <see cref="JsFunction.NativeImpl"/> synchronously and
    /// pushes the result. For user functions, builds a new
    /// environment with the parameters bound (and an
    /// <c>arguments</c> object), pushes a call frame, and switches
    /// execution to the function's chunk. When the function
    /// returns, the frame is popped and control resumes at the
    /// instruction after this call.
    /// </summary>
    private void InvokeFunction(
        JsFunction fn,
        object? thisVal,
        object?[] args,
        bool isConstructor,
        JsObject? newInstance)
    {
        if (fn.NativeImpl is not null || fn.NativeCallable is not null)
        {
            // Synchronous native call — no frame push needed.
            // Native built-ins signal exceptions via JsThrowSignal,
            // which we catch here and route to DoThrow so the
            // throw behaves exactly like a script-level throw.
            object? result;
            try
            {
                result = fn.NativeImpl is not null
                    ? fn.NativeImpl(thisVal, args)
                    : fn.NativeCallable!(this, thisVal, args);
            }
            catch (JsThrowSignal sig)
            {
                DoThrow(sig.JsValue);
                return;
            }
            if (isConstructor)
            {
                Push(result is JsObject ? result : newInstance);
            }
            else
            {
                Push(result);
            }
            return;
        }

        var template = fn.Template!;
        var newEnv = new JsEnvironment(fn.CapturedEnv);

        // Bind parameters — missing args are undefined.
        for (int i = 0; i < template.ParamCount; i++)
        {
            newEnv.Bindings[template.ParamNames[i]] =
                i < args.Length ? args[i] : JsValue.Undefined;
        }

        // Arrow functions inherit the enclosing function's
        // `arguments` via the env chain, so we skip creating
        // one in their new env. Regular functions get a fresh
        // `arguments` object containing the call's args.
        if (!template.IsArrow)
        {
            var argsArr = new JsArray();
            foreach (var a in args) argsArr.Elements.Add(a);
            newEnv.Bindings["arguments"] = argsArr;
        }

        // Save caller state.
        _frames.Push(new CallFrame
        {
            Code = _code,
            Constants = _constants,
            Names = _names,
            Ip = _ip,
            Env = _env,
            This = _this,
            IsConstructor = isConstructor,
            NewInstance = newInstance,
        });

        // Switch to callee. Arrow functions replace the call-
        // site `this` with their captured-at-creation value so
        // `this` inside an arrow matches its enclosing scope
        // regardless of how the arrow is invoked.
        _code = template.Body.Code;
        _constants = template.Body.Constants;
        _names = template.Body.Names;
        _ip = 0;
        _env = newEnv;
        _this = template.IsArrow ? fn.CapturedThis : thisVal;
    }

    /// <summary>
    /// Copy the top <paramref name="count"/> stack values into a
    /// fresh array, preserving order. Used when we need to
    /// retain arg values past the frame switch — the underlying
    /// stack slots are then popped by the caller.
    /// </summary>
    private object?[] CollectArgs(int count, int argsBase)
    {
        var args = new object?[count];
        for (int i = 0; i < count; i++)
        {
            args[i] = _stack[argsBase + i];
        }
        return args;
    }

    /// <summary>
    /// Walk <paramref name="obj"/>'s prototype chain looking for
    /// an object identical to <c>ctor.prototype</c>. Matches
    /// ECMA §15.3.5.3. Returns false for non-object subjects
    /// (per spec, <c>1 instanceof Number</c> is false, not an
    /// error).
    /// </summary>
    private static bool IsInstanceOf(object? obj, JsFunction fn)
    {
        if (obj is not JsObject target) return false;

        // Read F.prototype live so user-reassigned prototypes
        // participate in the chain walk.
        if (fn.Get("prototype") is not JsObject proto) return false;

        var walker = target.Prototype;
        while (walker is not null)
        {
            if (ReferenceEquals(walker, proto)) return true;
            walker = walker.Prototype;
        }
        return false;
    }

    /// <summary>
    /// Abstract relational comparison — ECMA §11.8.5. If both
    /// operands are strings, compare lexicographically. Otherwise
    /// coerce to number; if either is NaN, the result is false
    /// regardless of the operator.
    /// </summary>
    private void DoRelational(Func<double, double, bool> numericOp)
    {
        var b = Pop();
        var a = Pop();
        if (a is string sa && b is string sb)
        {
            // Lexicographic — use ordinal string compare, then
            // apply the operator to the resulting int.
            int cmp = string.CompareOrdinal(sa, sb);
            Push(numericOp(cmp, 0));
            return;
        }
        double na = JsValue.ToNumber(a);
        double nb = JsValue.ToNumber(b);
        if (double.IsNaN(na) || double.IsNaN(nb))
        {
            Push(false);
            return;
        }
        Push(numericOp(na, nb));
    }
}

/// <summary>
/// Runtime error thrown by <see cref="JsVM"/> when execution cannot
/// continue. Two cases:
///
/// - <b>Uncaught JS exceptions.</b> A <c>throw</c> in script that
///   escapes all <c>try</c>/<c>catch</c> handlers becomes a
///   <see cref="JsRuntimeException"/> whose <see cref="JsValue"/>
///   is the thrown value and whose message is its string form.
///   Hosts can inspect <see cref="JsValue"/> to inspect the
///   structured error (e.g., to read <c>.name</c> and
///   <c>.message</c> on a thrown <c>Error</c>-shaped object).
///
/// - <b>Internal VM invariant violations.</b> Unknown opcodes,
///   stack underflow, and similar bugs still throw this exception
///   with a null <see cref="JsValue"/>. These are not catchable
///   from script and indicate a compiler or VM defect.
/// </summary>
public sealed class JsRuntimeException : Exception
{
    public object? JsValue { get; }

    public JsRuntimeException(string message, object? jsValue = null) : base(message)
    {
        JsValue = jsValue;
    }
}
