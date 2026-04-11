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

    public JsVM(Chunk chunk, Dictionary<string, object?> globals)
    {
        _code = chunk.Code;
        _constants = chunk.Constants;
        _names = chunk.Names;
        // The globals env wraps the caller-owned dictionary so
        // mutations made by the VM are observable through
        // <see cref="JsEngine.Globals"/>.
        _env = new JsEnvironment(globals, parent: null);
        _this = JsValue.Undefined;
    }

    /// <summary>
    /// Run the chunk to completion. Returns the completion value
    /// (same as <see cref="CompletionValue"/>). Throws
    /// <see cref="JsRuntimeException"/> on a runtime error like
    /// reading an undeclared global.
    /// </summary>
    public object? Run()
    {
        while (true)
        {
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
                        Push(v);
                    }
                    break;
                case OpCode.LoadGlobalOrUndefined:
                    {
                        var name = _names[ReadU16()];
                        Push(_env.TryResolve(name, out var v) ? v : JsValue.Undefined);
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
                        Push(new JsFunction(template, _env));
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
                            return CompletionValue;
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
                    Push(new JsObject());
                    break;
                case OpCode.CreateArray:
                    {
                        int count = ReadU16();
                        var arr = new JsArray();
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
                        if (target is not JsObject jo)
                        {
                            RaiseError("TypeError",
                                $"Cannot read property '{name}' of {JsValue.ToJsString(target)}");
                            break;
                        }
                        _stack[_sp - 1] = jo.Get(name);
                    }
                    break;
                case OpCode.GetPropertyComputed:
                    {
                        var key = Pop();
                        var target = _stack[_sp - 1];
                        if (target is not JsObject jo)
                        {
                            RaiseError("TypeError",
                                $"Cannot read property of {JsValue.ToJsString(target)}");
                            break;
                        }
                        _stack[_sp - 1] = jo.Get(JsValue.ToJsString(key));
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
                    return CompletionValue;

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
    /// time and jumps execution to the handler target. On
    /// failure (no handler anywhere), throws
    /// <see cref="JsRuntimeException"/> with the value attached —
    /// this escapes the VM as a .NET exception.
    /// </summary>
    private void DoThrow(object? value)
    {
        while (true)
        {
            // Drop any stale handlers whose frame has been
            // unwound past (shouldn't happen if frame-pop cleans
            // up, but belt-and-suspenders).
            while (_handlers.Count > 0 &&
                   _handlers.Peek().FrameDepth > _frames.Count)
            {
                _handlers.Pop();
            }

            if (_handlers.Count > 0 &&
                _handlers.Peek().FrameDepth == _frames.Count)
            {
                var h = _handlers.Pop();
                _sp = h.StackBase;
                _env = h.Env;
                _ip = h.HandlerIp;
                if (h.IsCatch)
                {
                    // Push the thrown value onto the stack for the
                    // catch binding sequence to consume.
                    Push(value);
                    _pendingException = null;
                }
                else
                {
                    // Finally-only — carry the pending throw
                    // through the finally body; EndFinally will
                    // re-throw it.
                    _pendingException = value;
                }
                return;
            }

            // No handler in the current frame — unwind to the
            // caller. If there's no caller, the throw escapes
            // as a .NET exception.
            if (_frames.Count == 0)
            {
                throw new JsRuntimeException(
                    $"Uncaught {JsValue.ToJsString(value)}",
                    value);
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
    /// <c>message</c>. Constructs a plain <see cref="JsObject"/>
    /// with those two properties (the full <c>Error</c> constructor
    /// is a slice-6 built-in) and routes it through
    /// <see cref="DoThrow"/> so script-level <c>try</c>/<c>catch</c>
    /// can inspect it.
    /// </summary>
    private void RaiseError(string name, string message)
    {
        var err = new JsObject();
        err.Set("name", name);
        err.Set("message", message);
        DoThrow(err);
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
        if (fn.NativeImpl is not null)
        {
            RaiseError("TypeError",
                "'new' on native functions is not supported in this slice");
            return;
        }

        // Read `F.prototype` live so user reassignment
        // (`Dog.prototype = new Animal()`) takes effect.
        var protoValue = fn.Get("prototype");
        var instance = new JsObject
        {
            Prototype = protoValue as JsObject,
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
        if (fn.NativeImpl is not null)
        {
            // Synchronous native call — no frame push needed.
            var result = fn.NativeImpl(thisVal, args);
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

        // Bind `arguments` object (phase-3a simplification: a
        // plain JsArray; the spec mandates a special Arguments
        // exotic object with a linked-list to the parameters,
        // which is a slice-4c refinement if test262 complains).
        var argsArr = new JsArray();
        foreach (var a in args) argsArr.Elements.Add(a);
        newEnv.Bindings["arguments"] = argsArr;

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

        // Switch to callee.
        _code = template.Body.Code;
        _constants = template.Body.Constants;
        _names = template.Body.Names;
        _ip = 0;
        _env = newEnv;
        _this = thisVal;
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
