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
    private readonly byte[] _code;
    private readonly IReadOnlyList<object?> _constants;
    private readonly IReadOnlyList<string> _names;
    private readonly Dictionary<string, object?> _globals;

    // Value stack. 1024 is a generous start for slice 3 — no
    // function calls, so stack depth is bounded by nesting of
    // expressions, and in practice never exceeds a handful.
    private readonly object?[] _stack = new object?[1024];
    private int _sp;
    private int _ip;
    // Single compiler-owned scratch slot for the postfix-update
    // sequences. Not observable to user code.
    private object? _scratch;

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
        _globals = globals;
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

                // ---- Globals ----
                case OpCode.LoadGlobal:
                    {
                        var name = _names[ReadU16()];
                        if (!_globals.TryGetValue(name, out var v))
                        {
                            throw new JsRuntimeException($"{name} is not defined");
                        }
                        Push(v);
                    }
                    break;
                case OpCode.LoadGlobalOrUndefined:
                    {
                        var name = _names[ReadU16()];
                        Push(_globals.TryGetValue(name, out var v) ? v : JsValue.Undefined);
                    }
                    break;
                case OpCode.StoreGlobal:
                    {
                        var name = _names[ReadU16()];
                        _globals[name] = _stack[_sp - 1];
                    }
                    break;
                case OpCode.DeclareGlobal:
                    {
                        var name = _names[ReadU16()];
                        if (!_globals.ContainsKey(name))
                        {
                            _globals[name] = JsValue.Undefined;
                        }
                    }
                    break;
                case OpCode.DeleteGlobal:
                    {
                        var name = _names[ReadU16()];
                        // `delete` on a declared binding returns
                        // false in non-strict mode; the binding
                        // stays. We do not remove.
                        Push(_globals.ContainsKey(name) ? JsValue.False : JsValue.True);
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
                            throw new JsRuntimeException(
                                "Cannot use 'in' operator on non-object");
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
                            throw new JsRuntimeException(
                                $"Cannot read property '{name}' of non-object");
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
                            throw new JsRuntimeException(
                                "Cannot read property of non-object");
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
                            throw new JsRuntimeException(
                                $"Cannot assign property '{name}' on non-object");
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
                            throw new JsRuntimeException(
                                "Cannot assign property on non-object");
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
/// continue — typically reading an undeclared global or encountering
/// an unknown opcode. Once slice 5 lands <c>try</c> / <c>catch</c>
/// these will become catchable JS exceptions; for now they are
/// uncaught .NET exceptions that propagate to the host.
/// </summary>
public sealed class JsRuntimeException : Exception
{
    public JsRuntimeException(string message) : base(message) { }
}
