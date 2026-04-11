namespace Daisi.Broski.Engine.Js;

/// <summary>
/// Bytecode opcode set for the phase-3a stack VM. Each opcode is a
/// single byte; operands (when present) are a 2-byte little-endian
/// unsigned index into either the constant pool or the name table.
/// Jump offsets are 2-byte signed, relative to the byte immediately
/// following the jump instruction.
///
/// The set is deliberately small — this is the slice-3 subset, not
/// the final ES5 instruction stream. Slice 4 adds <c>MakeFunction</c>,
/// <c>Call</c>, <c>Return</c>, <c>CreateObject</c>, <c>CreateArray</c>,
/// <c>GetProperty</c>, <c>SetProperty</c>, <c>Throw</c>, and
/// <c>PushTryHandler</c>. Slice 5 adds built-in dispatch. Renumbering
/// is fine up until the VM is publicly consumed; each opcode's name
/// is load-bearing, its byte value is not.
/// </summary>
public enum OpCode : byte
{
    Nop = 0,

    // ---- Constants / literals ----
    PushUndefined,
    PushNull,
    PushTrue,
    PushFalse,
    /// <summary>Push a constant from the pool. Operand: u16 index.</summary>
    PushConst,

    // ---- Stack manipulation ----
    Pop,
    /// <summary>Duplicate the top of stack.</summary>
    Dup,
    /// <summary>
    /// Duplicate the top <i>two</i> values, preserving their order.
    /// Stack <c>[..., a, b]</c> → <c>[..., a, b, a, b]</c>. Used
    /// when compiling compound assignment and postfix update to a
    /// computed member, where both the object and the key need to
    /// be consumed twice (once to read the old value, once to
    /// write the new one).
    /// </summary>
    Dup2,
    /// <summary>
    /// Pop the top of stack into a single VM-internal scratch
    /// slot. Used by the compiler to save the "old value" of a
    /// postfix update-to-member expression while the rest of the
    /// read-modify-write sequence runs against the object and
    /// key on the main stack. The slot is not observable to user
    /// code and is overwritten by each subsequent
    /// <see cref="StoreScratch"/>; nested postfix expressions are
    /// safe because each completes its read of
    /// <see cref="LoadScratch"/> before the next nested
    /// expression writes its own scratch.
    /// </summary>
    StoreScratch,
    /// <summary>Push the scratch slot to the top of stack.</summary>
    LoadScratch,

    // ---- Name resolution (walks the env chain) ----
    //
    // These opcodes were originally called "LoadGlobal" back in
    // slice 3 when the only scope was the globals dictionary.
    // They are now scope-aware: at the top level the env chain has
    // one env (globals), so the behavior is identical; inside a
    // function the chain is function env → ... → globals, so the
    // same opcodes transparently reach lexically enclosing
    // bindings. The old names are kept for diff stability.
    /// <summary>
    /// Walk the env chain looking for the given name. Push the
    /// value if found; throw <see cref="JsRuntimeException"/> if
    /// not (to become a <c>ReferenceError</c> in slice 5).
    /// Operand: u16 name index.
    /// </summary>
    LoadGlobal,
    /// <summary>
    /// Walk the env chain; push the value if found, or
    /// <c>undefined</c> if not. Used only by
    /// <c>typeof identifier</c>, which must not throw per ECMA
    /// §11.4.3. Operand: u16 name index.
    /// </summary>
    LoadGlobalOrUndefined,
    /// <summary>
    /// Store the top of stack into a global (leaves the value on the
    /// stack — matches the evaluation model for <c>x = 1</c> being an
    /// expression of value <c>1</c>). Operand: u16 name index.
    /// </summary>
    StoreGlobal,
    /// <summary>
    /// <c>var x</c> — declare a global with initial value
    /// <c>undefined</c> if it does not already exist. Does not touch
    /// the stack. Operand: u16 name index.
    /// </summary>
    DeclareGlobal,
    /// <summary>
    /// <c>let x</c> / <c>const x</c> — declare a binding in the
    /// current env marked as uninitialized (the temporal dead
    /// zone per ECMA §13.3.1). Unlike <see cref="DeclareGlobal"/>
    /// this always overwrites any existing binding with the
    /// same name — block-scoped declarations do not cooperate
    /// with hoisted vars in the enclosing function. Accessing
    /// the binding before its initializer runs throws
    /// <c>ReferenceError</c> (even via <c>typeof</c>). The
    /// compiler emits this at block entry for every let/const
    /// declaration in the block, then the actual declaration
    /// statement overwrites the sentinel with the real value
    /// via <see cref="StoreGlobal"/>. Operand: u16 name index.
    /// </summary>
    DeclareLet,
    /// <summary>
    /// <c>delete x</c> on an unqualified identifier. In ES5
    /// non-strict mode this returns <c>false</c> (the variable was
    /// declared) unless the binding was created implicitly — we
    /// conservatively return <c>false</c> for any name bound
    /// anywhere up the env chain and <c>true</c> for undeclared
    /// ones. Operand: u16 name index.
    /// </summary>
    DeleteGlobal,

    // ---- Functions / calls / constructors ----
    /// <summary>
    /// Materialize a function value from a
    /// <see cref="JsFunctionTemplate"/> stored in the constant
    /// pool, capturing the current env as the closure. Operand:
    /// u16 constant index.
    /// </summary>
    MakeFunction,
    /// <summary>
    /// Call a function. Stack before: <c>[..., fn, this, arg1, ..., argN]</c>.
    /// Stack after: <c>[..., result]</c>. Operand: u8 argument
    /// count (max 255 — well beyond any realistic ES5 call site).
    /// </summary>
    Call,
    /// <summary>
    /// Construct via a constructor function. Stack before:
    /// <c>[..., fn, arg1, ..., argN]</c>. Stack after:
    /// <c>[..., instance]</c>. The VM allocates a fresh
    /// <see cref="JsObject"/>, links its prototype to
    /// <c>fn.prototype</c>, invokes <c>fn</c> with that as
    /// <c>this</c>, and per ECMA §13.2.2 uses the return value
    /// if it is an object, otherwise the freshly-allocated
    /// instance. Operand: u8 argument count.
    /// </summary>
    New,
    /// <summary>
    /// ES2015 spread call. Stack before:
    /// <c>[..., fn, this, argsArray]</c>. Stack after:
    /// <c>[..., result]</c>. <c>argsArray</c> must be a
    /// <see cref="JsArray"/> — the VM flattens its elements
    /// into the call's actual argument list. Emitted by the
    /// compiler whenever a call contains at least one
    /// <c>...expr</c> argument; plain calls continue to use
    /// <see cref="Call"/> for efficiency.
    /// </summary>
    CallSpread,
    /// <summary>
    /// ES2015 spread <c>new</c>. Stack before:
    /// <c>[..., fn, argsArray]</c>. Stack after:
    /// <c>[..., instance]</c>.
    /// </summary>
    NewSpread,

    // ---- ES2015 classes ----
    /// <summary>
    /// Wire a subclass's prototype chain to its parent. Stack
    /// <c>[subclass, parent]</c> → <c>[subclass]</c>. Sets
    /// <c>subclass.[[Prototype]] = parent</c> (so static
    /// methods on the parent are inherited) and
    /// <c>subclass.prototype.[[Prototype]] = parent.prototype</c>
    /// (so instance methods on the parent are inherited via
    /// the instance prototype chain). Pops the parent; keeps
    /// the subclass on the stack for further class-assembly
    /// ops (method installation, etc.).
    /// </summary>
    SetupSubclass,
    /// <summary>
    /// Install an instance method on a class's prototype.
    /// Stack <c>[class, fn]</c> → <c>[class]</c>. The method
    /// is installed as a non-enumerable property under the
    /// operand name (u16 name index), and its
    /// <see cref="JsFunction.HomeSuper"/> is set to
    /// <c>class.prototype.[[Prototype]]</c> when that is a
    /// non-null <see cref="JsObject"/>, so
    /// <see cref="LoadSuper"/> inside the method body can
    /// walk to the parent's prototype. For a class with no
    /// <c>extends</c>, the home-super chain resolves to the
    /// engine's default object prototype.
    /// </summary>
    InstallMethod,
    /// <summary>
    /// Install a static method on a class function itself.
    /// Stack <c>[class, fn]</c> → <c>[class]</c>. Non-enumerable
    /// property store under the operand name (u16). The
    /// method's <see cref="JsFunction.HomeSuper"/> is set to
    /// <c>class.[[Prototype]]</c>, which after
    /// <see cref="SetupSubclass"/> is the parent class value —
    /// so <c>super.foo()</c> inside a static method resolves
    /// against the parent class's static methods.
    /// </summary>
    InstallStaticMethod,
    /// <summary>
    /// Copy the constructor function's home-super from its
    /// class's prototype chain. Stack <c>[class]</c> →
    /// <c>[class]</c>. After <see cref="SetupSubclass"/> has
    /// run, <c>class.prototype.[[Prototype]]</c> is the
    /// parent class's prototype; this opcode sets
    /// <c>class.HomeSuper</c> to that object so the
    /// constructor body's <c>super(args)</c> and
    /// <c>super.foo</c> references resolve correctly.
    /// Emitted only for classes with an <c>extends</c>
    /// clause.
    /// </summary>
    LinkConstructorSuper,
    /// <summary>
    /// Push the current call frame's function's
    /// <see cref="JsFunction.HomeSuper"/> onto the stack.
    /// Throws <c>SyntaxError</c>-shaped <c>TypeError</c> if
    /// the current function has no home-super (i.e. it is
    /// not a class method, or is a method in a class with no
    /// <c>extends</c> clause).
    /// </summary>
    LoadSuper,

    // ---- ES2015 iterator protocol ----
    /// <summary>
    /// Start iterating an iterable value via the ES2015
    /// iterator protocol. Stack <c>[iterable]</c> →
    /// <c>[iter]</c>. Looks up <c>[Symbol.iterator]</c> on
    /// the iterable, calls it with the iterable as
    /// <c>this</c>, and pushes the returned iterator object.
    /// Throws <c>TypeError</c> when the iterable is null /
    /// undefined or has no Symbol.iterator method.
    /// </summary>
    ForOfStart,
    /// <summary>
    /// Advance a for-of iterator by one step. Stack before:
    /// <c>[iter]</c>. On a non-done step the opcode leaves
    /// the iterator at its position and pushes the step's
    /// <c>value</c> onto the stack (result: <c>[iter, value]</c>).
    /// On done, it pops the iterator and jumps to the u16
    /// operand offset, which the compiler patches to point
    /// past the loop body. The calling step's <c>next()</c>
    /// method runs through the normal VM call machinery
    /// (including any exception handling).
    /// </summary>
    ForOfNext,

    // ---- ES2015 generators ----
    /// <summary>
    /// Generator <c>yield expr</c> — suspend the current
    /// generator VM with the top-of-stack as the yielded
    /// value. Stack <c>[value]</c> → <c>[]</c>. The opcode
    /// stores <c>value</c> into the VM's yield slot and sets
    /// the <c>_halted</c> flag so <c>RunLoop</c> exits. Always
    /// paired with a <see cref="YieldResume"/> at the next
    /// instruction; when <c>gen.next(sent)</c> resumes the
    /// VM, <c>YieldResume</c> is the first opcode that runs.
    /// </summary>
    YieldValue,
    /// <summary>
    /// Companion to <see cref="YieldValue"/>: pushes the
    /// most recently "sent" value (passed in via
    /// <c>gen.next(arg)</c>) onto the stack as the result
    /// of the yield expression. On the very first resume
    /// (when we reach a yield point for the first time), the
    /// sent value is <c>undefined</c>.
    /// </summary>
    YieldResume,
    /// <summary>
    /// Return from the current function call. Pops the top of
    /// stack as the return value, restores the calling frame, and
    /// pushes the value onto the caller's stack. At the top-level
    /// program frame the VM halts.
    /// </summary>
    Return,
    /// <summary>
    /// <c>x instanceof F</c>. Stack <c>[x, F]</c> → <c>[bool]</c>.
    /// Walks <c>x</c>'s prototype chain looking for
    /// <c>F.prototype</c>. Throws if <c>F</c> is not a
    /// <see cref="JsFunction"/>.
    /// </summary>
    Instanceof,
    /// <summary>Push the current call frame's <c>this</c> value.</summary>
    LoadThis,
    /// <summary>
    /// Swap the top two values on the stack. Used by the compiler
    /// when compiling a method call, where <c>obj</c> and the
    /// resolved method arrive in the wrong order for <see cref="Call"/>.
    /// </summary>
    Swap,

    // ---- Exception handling ----
    /// <summary>
    /// Register a catch handler at the given 2-byte signed
    /// offset (relative to the instruction after PushCatchHandler).
    /// Saves the current stack depth and env so they can be
    /// restored when a throw unwinds to this handler. When the
    /// handler fires, the VM pushes the thrown value onto the
    /// stack and jumps to the handler target.
    /// </summary>
    PushCatchHandler,
    /// <summary>
    /// Register a finally-only handler. Same shape as
    /// <see cref="PushCatchHandler"/> but on unwind the VM does
    /// not push the thrown value — instead it stores it in the
    /// VM's pending-exception slot, which <see cref="EndFinally"/>
    /// checks and re-throws.
    /// </summary>
    PushFinallyHandler,
    /// <summary>
    /// Remove the top handler. Emitted after a try block
    /// completes normally so the handler doesn't catch a
    /// subsequent exception that logically belongs to an
    /// outer try.
    /// </summary>
    PopHandler,
    /// <summary>
    /// Pop the top of stack and throw it. Unwinds handlers and
    /// call frames until the nearest catch or finally handler
    /// is found. If none exists in this script, the VM escapes
    /// with a <see cref="JsRuntimeException"/> whose
    /// <c>JsValue</c> carries the thrown value.
    /// </summary>
    Throw,
    /// <summary>
    /// End of a finally block. If there is a pending exception
    /// (the finally was entered because a throw occurred with no
    /// catch in the same try), re-throw it — which unwinds to
    /// the next enclosing handler. Otherwise continue execution.
    /// </summary>
    EndFinally,
    /// <summary>
    /// Push a fresh environment whose parent is the current env.
    /// Used to give the <c>catch</c> parameter its own block
    /// scope so it doesn't leak into the enclosing function
    /// after the catch body ends.
    /// </summary>
    PushEnv,
    /// <summary>
    /// Pop back to the parent of the current environment.
    /// </summary>
    PopEnv,

    // ---- for-in iteration ----
    /// <summary>
    /// Pop an object off the stack and push a fresh
    /// <see cref="ForInIterator"/> positioned at the first
    /// enumerable string key. Non-object arguments (per spec
    /// §12.6.4: <c>null</c> and <c>undefined</c>) push an empty
    /// iterator so the subsequent loop executes zero times.
    /// </summary>
    ForInStart,
    /// <summary>
    /// Advance the iterator sitting on top of the stack by one
    /// key. If there is a next key, push it (stack becomes
    /// <c>[iter, key]</c>) and fall through. If the iterator is
    /// done, pop the iterator and jump by a 2-byte signed offset
    /// (relative to the instruction after the jump).
    /// </summary>
    ForInNext,

    // ---- Arithmetic (all pop two operands, push one) ----
    Add, Sub, Mul, Div, Mod,
    Negate, UnaryPlus,

    // ---- Bitwise ----
    BitAnd, BitOr, BitXor, BitNot,
    Shl, Shr, Ushr,

    // ---- Comparison ----
    LooseEq, LooseNotEq, StrictEq, StrictNotEq,
    Lt, Le, Gt, Ge,

    // ---- Logical ----
    LogicalNot,

    // ---- Type operators ----
    TypeOf,
    /// <summary>
    /// ES <c>in</c> operator. Stack <c>[key, obj]</c> →
    /// <c>[bool]</c>. Coerces <c>key</c> to string and walks
    /// <c>obj</c>'s prototype chain. Throws at runtime if
    /// <c>obj</c> is not an object — ECMA §11.8.7.
    /// </summary>
    In,

    // ---- Object / array construction ----
    /// <summary>Push a fresh empty <see cref="JsObject"/>.</summary>
    CreateObject,
    /// <summary>
    /// Pop the top <c>count</c> values, construct a
    /// <see cref="JsArray"/> with those elements in stack order
    /// (bottom-to-top), and push the array. Operand: u16 count.
    /// </summary>
    CreateArray,
    /// <summary>
    /// Append a single value to an array. Stack
    /// <c>[array, value]</c> → <c>[array]</c>. Used by the
    /// ES2015 spread lowering in array literals and calls —
    /// each non-spread element is pushed via this opcode while
    /// the evolving result array stays at TOS.
    /// </summary>
    ArrayAppend,
    /// <summary>
    /// Append every element of an iterable to an array.
    /// Stack <c>[array, iter]</c> → <c>[array]</c>. The
    /// iterable must be a <see cref="JsArray"/> in this slice
    /// (generic iterator protocol support is deferred to the
    /// iterator/for-of slice). Non-array iterables throw
    /// <c>TypeError</c>.
    /// </summary>
    ArrayAppendSpread,
    /// <summary>
    /// Initialize a property on the object currently under a
    /// single value. Stack <c>[obj, value]</c> → <c>[obj]</c>.
    /// Unlike <see cref="SetProperty"/>, this leaves the object
    /// on the stack so successive <c>InitProperty</c>s can chain
    /// cleanly inside an object literal. Operand: u16 name index.
    /// </summary>
    InitProperty,

    // ---- Object / array member access ----
    /// <summary>
    /// Dotted member load. Stack <c>[obj]</c> → <c>[value]</c>.
    /// Operand: u16 name index.
    /// </summary>
    GetProperty,
    /// <summary>
    /// Computed member load (<c>obj[key]</c>). Stack
    /// <c>[obj, key]</c> → <c>[value]</c>. Key is coerced to
    /// string via <see cref="JsValue.ToJsString"/>.
    /// </summary>
    GetPropertyComputed,
    /// <summary>
    /// Dotted member store. Stack <c>[obj, value]</c> →
    /// <c>[value]</c>. Leaves the assigned value on the stack
    /// so <c>obj.x = 1</c> has the expression value <c>1</c>.
    /// Operand: u16 name index.
    /// </summary>
    SetProperty,
    /// <summary>
    /// Computed member store. Stack <c>[obj, key, value]</c> →
    /// <c>[value]</c>.
    /// </summary>
    SetPropertyComputed,
    /// <summary>
    /// <c>delete obj.x</c>. Stack <c>[obj]</c> → <c>[bool]</c>.
    /// Operand: u16 name index.
    /// </summary>
    DeleteProperty,
    /// <summary>
    /// <c>delete obj[key]</c>. Stack <c>[obj, key]</c> →
    /// <c>[bool]</c>.
    /// </summary>
    DeletePropertyComputed,

    // ---- Control flow (operand: s16 relative offset) ----
    Jump,
    /// <summary>Pops TOS, jumps if falsy.</summary>
    JumpIfFalse,
    /// <summary>Pops TOS, jumps if truthy.</summary>
    JumpIfTrue,
    /// <summary>Peeks TOS, jumps if falsy; otherwise pops. Used for <c>&amp;&amp;</c>.</summary>
    JumpIfFalseKeep,
    /// <summary>Peeks TOS, jumps if truthy; otherwise pops. Used for <c>||</c>.</summary>
    JumpIfTrueKeep,

    // ---- Program completion ----
    /// <summary>
    /// Pops the top of stack and stores it as the VM's completion
    /// value. Emitted after every <c>ExpressionStatement</c> at the
    /// top level so <see cref="JsEngine.Evaluate"/> can return the
    /// last-evaluated expression, matching the REPL-style completion
    /// semantics of ECMA §14.
    /// </summary>
    StoreCompletion,

    /// <summary>End of program. The VM halts and returns its current completion value.</summary>
    Halt,
}

/// <summary>
/// A compiled unit of bytecode — the output of <see cref="JsCompiler"/>
/// and the input to <see cref="JsVM"/>. A <see cref="Chunk"/> owns its
/// code bytes, its constant pool (boxed literal values indexed by
/// <see cref="OpCode.PushConst"/>), and its name table (identifier
/// strings indexed by the global-access opcodes). The two tables are
/// kept separate so string pooling for identifiers can be deduped
/// independently of numeric / string constants.
/// </summary>
public sealed class Chunk
{
    private readonly List<byte> _code = new();
    private readonly List<object?> _constants = new();
    private readonly List<string> _names = new();

    public byte[] Code => _code.ToArray();
    public IReadOnlyList<object?> Constants => _constants;
    public IReadOnlyList<string> Names => _names;

    public int Position => _code.Count;

    // -------------------------------------------------------------------
    // Emit helpers
    // -------------------------------------------------------------------

    public void Emit(OpCode op) => _code.Add((byte)op);

    public void EmitWithU16(OpCode op, int operand)
    {
        _code.Add((byte)op);
        _code.Add((byte)(operand & 0xFF));
        _code.Add((byte)((operand >> 8) & 0xFF));
    }

    public void EmitWithU8(OpCode op, int operand)
    {
        if (operand < 0 || operand > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(operand),
                $"u8 operand out of range: {operand}");
        }
        _code.Add((byte)op);
        _code.Add((byte)operand);
    }

    /// <summary>
    /// Emit a jump opcode and a placeholder 2-byte operand; returns
    /// the address of the operand so it can be back-patched by
    /// <see cref="PatchJump"/> once the target is known.
    /// </summary>
    public int EmitJump(OpCode op)
    {
        _code.Add((byte)op);
        _code.Add(0);
        _code.Add(0);
        return _code.Count - 2;
    }

    /// <summary>
    /// Back-patch a jump whose operand starts at
    /// <paramref name="operandAddr"/> so that it targets the current
    /// end-of-code position. The stored offset is relative to the
    /// instruction immediately after the jump (i.e. to
    /// <c>operandAddr + 2</c>), matching the VM's dispatch model.
    /// </summary>
    public void PatchJump(int operandAddr) => PatchJumpTo(operandAddr, _code.Count);

    /// <summary>
    /// Back-patch a jump to an arbitrary already-known target. Used
    /// for <c>continue</c> statements in <c>for</c> loops, where the
    /// continue target is the loop's update clause and comes after
    /// the body in the emitted code.
    /// </summary>
    public void PatchJumpTo(int operandAddr, int targetAddr)
    {
        int offset = targetAddr - (operandAddr + 2);
        if (offset < short.MinValue || offset > short.MaxValue)
        {
            throw new InvalidOperationException(
                "Jump offset out of s16 range — method too large for phase-3a VM");
        }
        _code[operandAddr] = (byte)(offset & 0xFF);
        _code[operandAddr + 1] = (byte)((offset >> 8) & 0xFF);
    }

    /// <summary>
    /// Emit a backward jump to a previously-recorded position.
    /// Used for the bottom-of-loop jump in <c>while</c> / <c>do..while</c>
    /// / <c>for</c>.
    /// </summary>
    public void EmitLoopJump(OpCode op, int loopStart)
    {
        _code.Add((byte)op);
        int offset = loopStart - (_code.Count + 2);
        if (offset < short.MinValue || offset > short.MaxValue)
        {
            throw new InvalidOperationException(
                "Loop jump offset out of s16 range");
        }
        _code.Add((byte)(offset & 0xFF));
        _code.Add((byte)((offset >> 8) & 0xFF));
    }

    // -------------------------------------------------------------------
    // Constant / name pools
    // -------------------------------------------------------------------

    /// <summary>
    /// Add a constant to the pool. Returns the index to use with
    /// <see cref="OpCode.PushConst"/>. Not deduped — phase 3a values
    /// intern trivially because numbers are boxed lazily, and the
    /// cost of scanning the pool on every insert outweighs the win
    /// on realistic program sizes.
    /// </summary>
    public int AddConstant(object? value)
    {
        _constants.Add(value);
        return _constants.Count - 1;
    }

    /// <summary>
    /// Add (or look up) a name in the name table. Names are deduped
    /// because the same identifier is touched repeatedly and each
    /// access site needs its index.
    /// </summary>
    public int AddName(string name)
    {
        for (int i = 0; i < _names.Count; i++)
        {
            if (_names[i] == name) return i;
        }
        _names.Add(name);
        return _names.Count - 1;
    }
}
