namespace Daisi.Broski.Engine.Js;

/// <summary>
/// AST → bytecode compiler for the phase-3a slice-3 subset. Walks a
/// <see cref="Program"/> and emits into a <see cref="Chunk"/> that
/// <see cref="JsVM"/> can execute.
///
/// Supported today:
///
/// - Primitive literals (number, string, boolean, null, undefined).
/// - Global variable declarations (<c>var</c>) with hoisting: every
///   <c>var</c> name declared anywhere in the program is emitted as
///   a <see cref="OpCode.DeclareGlobal"/> at the top of the chunk
///   before any body code runs, matching the spec's hoisting model.
///   (<c>let</c> / <c>const</c> are accepted by the parser and
///   currently compiled the same as <c>var</c> — block scoping is
///   phase 3b.)
/// - Assignment, compound assignment, update (prefix + postfix
///   <c>++</c> / <c>--</c>) — target must be an unqualified
///   identifier in this slice.
/// - Every unary / binary / logical / conditional / sequence operator.
///   Logical <c>&amp;&amp;</c> and <c>||</c> short-circuit via the
///   <c>JumpIf*Keep</c> opcodes.
/// - <c>typeof</c> on an identifier is special-cased to avoid throwing
///   a ReferenceError on undeclared names, matching ES5 semantics.
/// - <c>if</c> / <c>else</c>, <c>while</c>, <c>do..while</c>, C-style
///   <c>for</c>, <c>break</c> / <c>continue</c> (unlabeled),
///   <c>{ ... }</c>, empty statement, expression statement.
/// - Top-level completion tracking: each top-level
///   <see cref="ExpressionStatement"/> emits a
///   <see cref="OpCode.StoreCompletion"/> so
///   <see cref="JsEngine.Evaluate"/> can return the last-evaluated
///   expression's value.
///
/// Deliberately deferred (will throw <see cref="JsCompileException"/>
/// at compile time so the failure is obvious):
///
/// - <b>Functions, <c>this</c>, closures, <c>arguments</c>, <c>return</c>.</b>
///   Slice 4.
/// - <b>Objects, arrays, member access, computed member access,
///   calls, <c>new</c>.</b> Slice 4.
/// - <b><c>try</c> / <c>catch</c> / <c>throw</c>, <c>switch</c>,
///   labeled <c>break</c> / <c>continue</c>, <c>for..in</c>,
///   <c>with</c>.</b> Slices 4–5.
/// - <b>Block scoping.</b> Every <c>var</c> lands in the global
///   environment. <c>let</c> / <c>const</c> behave the same for now.
///   Phase 3b.
/// - <b>Strict mode.</b> No enforcement yet.
/// </summary>
public sealed class JsCompiler
{
    // Nested function bodies push their own frame, which owns its
    // own Chunk and break-target stack. The active frame's Chunk
    // is cached in <see cref="_chunk"/> and targets in
    // <see cref="_breakTargets"/> so the existing emit helpers
    // continue to work unchanged.
    private readonly Stack<CompilerFrame> _frames = new();
    private Chunk _chunk = null!;
    private Stack<BreakTarget> _breakTargets = null!;
    // Label pending attachment to the next loop / switch — set
    // by a <see cref="LabeledStatement"/> when its inner body
    // is a loop or switch, so break / continue referring to the
    // label find the context on the stack.
    private string? _pendingLabel;

    private sealed class CompilerFrame
    {
        public Chunk Chunk { get; } = new();
        public Stack<BreakTarget> BreakTargets { get; } = new();
        public bool IsFunction { get; init; }
    }

    /// <summary>
    /// An in-progress control-flow context that <c>break</c>
    /// (and possibly <c>continue</c>) statements can target.
    /// Loops set <see cref="IsLoop"/> and may carry an optional
    /// <see cref="Label"/>; <c>switch</c> pushes one with
    /// <c>IsLoop = false</c>; a labeled non-loop statement
    /// pushes one with <c>IsLoop = false</c> and its label.
    /// </summary>
    private sealed class BreakTarget
    {
        public string? Label;
        public bool IsLoop;
        public readonly List<int> BreakJumps = new();
        public readonly List<int> ContinueJumps = new();
    }

    private void PushFrame(bool isFunction)
    {
        var f = new CompilerFrame { IsFunction = isFunction };
        _frames.Push(f);
        _chunk = f.Chunk;
        _breakTargets = f.BreakTargets;
    }

    private Chunk PopFrame()
    {
        var popped = _frames.Pop();
        if (_frames.Count > 0)
        {
            _chunk = _frames.Peek().Chunk;
            _breakTargets = _frames.Peek().BreakTargets;
        }
        else
        {
            _chunk = null!;
            _breakTargets = null!;
        }
        return popped.Chunk;
    }

    private bool InFunction()
    {
        foreach (var f in _frames)
        {
            if (f.IsFunction) return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve the target of a <c>break</c> statement. Unlabeled
    /// breaks take the nearest enclosing loop or switch;
    /// labeled breaks walk up the stack looking for a matching
    /// label. Throws <see cref="JsCompileException"/> if no
    /// matching target is found.
    /// </summary>
    private BreakTarget ResolveBreakTarget(string? label, int sourceOffset)
    {
        if (_breakTargets.Count == 0)
        {
            throw new JsCompileException(
                label is null
                    ? "'break' outside of loop or switch"
                    : $"Labeled 'break {label}' target not found",
                sourceOffset);
        }
        if (label is null)
        {
            return _breakTargets.Peek();
        }
        foreach (var ctx in _breakTargets)
        {
            if (ctx.Label == label) return ctx;
        }
        throw new JsCompileException(
            $"Labeled 'break {label}' target not found", sourceOffset);
    }

    /// <summary>
    /// Resolve the target of a <c>continue</c> statement. Unlike
    /// <c>break</c>, <c>continue</c> can only target a loop —
    /// never a switch. Unlabeled continues take the nearest
    /// enclosing loop (walking past any intervening switches);
    /// labeled continues walk up looking for a loop with the
    /// matching label.
    /// </summary>
    private BreakTarget ResolveContinueTarget(string? label, int sourceOffset)
    {
        if (label is null)
        {
            foreach (var ctx in _breakTargets)
            {
                if (ctx.IsLoop) return ctx;
            }
            throw new JsCompileException("'continue' outside of loop", sourceOffset);
        }
        foreach (var ctx in _breakTargets)
        {
            if (ctx.IsLoop && ctx.Label == label) return ctx;
        }
        throw new JsCompileException(
            $"Labeled 'continue {label}' target not found or not a loop",
            sourceOffset);
    }

    /// <summary>
    /// Pop the pending label (if any) and return it. Called at
    /// the start of each loop / switch compilation so it can
    /// attach the label to its own <see cref="BreakTarget"/>.
    /// Having this as a consume-once operation avoids the need to
    /// explicitly clear it after.
    /// </summary>
    private string? TakePendingLabel()
    {
        var l = _pendingLabel;
        _pendingLabel = null;
        return l;
    }

    public Chunk Compile(Program program)
    {
        PushFrame(isFunction: false);
        HoistDeclarations(program.Body);

        // Top-level let/const and function declarations are
        // pre-scanned into the globals env directly (no
        // PushEnv) so they survive across successive Evaluate
        // calls on the same engine — the REPL-style usage
        // pattern. This is a pragmatic deviation from the
        // spec, which puts script-top-level let/const in a
        // module-scope record. If we ever ship modules
        // properly (phase 3b module loader), that will
        // revisit the top-level behavior.
        foreach (var stmt in program.Body)
        {
            if (stmt is VariableDeclaration vd &&
                vd.Kind != VariableDeclarationKind.Var)
            {
                foreach (var d in vd.Declarations)
                {
                    foreach (var name in CollectPatternNames(d.Id))
                    {
                        int idx = _chunk.AddName(name);
                        _chunk.EmitWithU16(OpCode.DeclareLet, idx);
                    }
                }
            }
            else if (stmt is FunctionDeclaration fd)
            {
                var template = CompileFunctionTemplate(
                    fd.Id.Name,
                    fd.Params,
                    fd.Body,
                    fd.End - fd.Start,
                    isGenerator: fd.IsGenerator);
                int templateIdx = _chunk.AddConstant(template);
                int nameIdx = _chunk.AddName(fd.Id.Name);
                _chunk.EmitWithU16(OpCode.DeclareGlobal, nameIdx);
                _chunk.EmitWithU16(OpCode.MakeFunction, templateIdx);
                _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
                _chunk.Emit(OpCode.Pop);
            }
            else if (stmt is ClassDeclaration cd)
            {
                int idx = _chunk.AddName(cd.Id.Name);
                _chunk.EmitWithU16(OpCode.DeclareLet, idx);
            }
        }

        foreach (var stmt in program.Body)
        {
            CompileStatement(stmt);
        }
        _chunk.Emit(OpCode.Halt);
        return PopFrame();
    }

    // -------------------------------------------------------------------
    // Block compilation
    // -------------------------------------------------------------------

    /// <summary>
    /// Compile a block with block scoping. Pushes a fresh
    /// environment, pre-declares all <c>let</c> / <c>const</c>
    /// names in the block body as
    /// <see cref="OpCode.DeclareLet"/> (putting them in the
    /// temporal dead zone), then compiles the body, then pops
    /// the environment.
    ///
    /// Function bodies are themselves <see cref="BlockStatement"/>s
    /// and therefore get their own block scope too. That is
    /// one extra env push per call (harmless) and makes
    /// function-body let/const work the same as nested-block
    /// let/const.
    /// </summary>
    private void CompileBlock(BlockStatement block)
    {
        _chunk.Emit(OpCode.PushEnv);
        HoistBlockScopedDeclarations(block);
        foreach (var s in block.Body) CompileStatement(s);
        _chunk.Emit(OpCode.PopEnv);
    }

    /// <summary>
    /// Pre-scan a block's direct children for <c>let</c> /
    /// <c>const</c> declarations and function declarations, and
    /// emit their binding-creation opcodes inside the block's
    /// env. Letting function declarations live in the block
    /// scope makes inner closures capture the block env, so
    /// they can see the block's <c>let</c> bindings. That's
    /// narrower than ES5's hoist-to-function-scope rule — if
    /// a function declared inside a nested block needs to be
    /// visible outside it, use a <c>var fn = function () {}</c>
    /// pattern (slice 3b-1 documented deferral).
    ///
    /// Pre-scan does not descend into further nested blocks,
    /// if bodies, loops, or switches — those have their own
    /// block scopes and pre-scan themselves on compile.
    /// </summary>
    private void HoistBlockScopedDeclarations(BlockStatement block)
    {
        foreach (var stmt in block.Body)
        {
            if (stmt is VariableDeclaration vd &&
                vd.Kind != VariableDeclarationKind.Var)
            {
                foreach (var d in vd.Declarations)
                {
                    foreach (var name in CollectPatternNames(d.Id))
                    {
                        int idx = _chunk.AddName(name);
                        _chunk.EmitWithU16(OpCode.DeclareLet, idx);
                    }
                }
            }
            else if (stmt is FunctionDeclaration fd)
            {
                var template = CompileFunctionTemplate(
                    fd.Id.Name,
                    fd.Params,
                    fd.Body,
                    fd.End - fd.Start,
                    isGenerator: fd.IsGenerator);
                int templateIdx = _chunk.AddConstant(template);
                int nameIdx = _chunk.AddName(fd.Id.Name);
                // Declare in the current env with undefined
                // (via DeclareGlobal), then overwrite with the
                // materialized function so inner closures get
                // the block env captured.
                _chunk.EmitWithU16(OpCode.DeclareGlobal, nameIdx);
                _chunk.EmitWithU16(OpCode.MakeFunction, templateIdx);
                _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
                _chunk.Emit(OpCode.Pop);
            }
            else if (stmt is ClassDeclaration cd)
            {
                // Classes are block-scoped and start in the TDZ
                // until the declaration statement runs.
                int idx = _chunk.AddName(cd.Id.Name);
                _chunk.EmitWithU16(OpCode.DeclareLet, idx);
            }
        }
    }

    // -------------------------------------------------------------------
    // var + function hoisting
    // -------------------------------------------------------------------
    //
    // ES5 hoists var declarations AND function declarations to the
    // top of the enclosing function (or program). Var names bind
    // to undefined; function names bind to the fully-constructed
    // function value, so `f(); function f() {}` runs `f` before
    // its source-order position.
    //
    // We implement both in a single pre-pass that walks the
    // statement tree and emits DeclareGlobal for vars and
    // MakeFunction + StoreGlobal + Pop for function declarations.
    // The recursive main-compile pass then treats FunctionDeclaration
    // as a no-op (it was already emitted here).

    private void HoistDeclarations(IEnumerable<Statement> body)
    {
        foreach (var stmt in body) HoistInStatement(stmt);
    }

    private void HoistInStatement(Statement stmt)
    {
        switch (stmt)
        {
            case VariableDeclaration vd:
                // Only `var` hoists to the enclosing function —
                // `let` and `const` are block-scoped and handled
                // by HoistBlockLetConst when their containing
                // BlockStatement compiles.
                if (vd.Kind != VariableDeclarationKind.Var) break;
                foreach (var d in vd.Declarations)
                {
                    foreach (var name in CollectPatternNames(d.Id))
                    {
                        int idx = _chunk.AddName(name);
                        _chunk.EmitWithU16(OpCode.DeclareGlobal, idx);
                    }
                }
                break;
            case FunctionDeclaration:
                // Function declarations are block-scoped per
                // slice 3b-1. HoistBlockScopedDeclarations
                // emits them inside the enclosing block's env,
                // so their inner closures capture the block
                // env. Nothing to do during the function-scope
                // var hoist pass.
                break;
            case BlockStatement b:
                foreach (var s in b.Body) HoistInStatement(s);
                break;
            case IfStatement i:
                HoistInStatement(i.Consequent);
                if (i.Alternate is not null) HoistInStatement(i.Alternate);
                break;
            case WhileStatement w:
                HoistInStatement(w.Body);
                break;
            case DoWhileStatement dw:
                HoistInStatement(dw.Body);
                break;
            case ForStatement f:
                if (f.Init is VariableDeclaration fvd && fvd.Kind == VariableDeclarationKind.Var)
                {
                    foreach (var d in fvd.Declarations)
                    {
                        foreach (var name in CollectPatternNames(d.Id))
                        {
                            int idx = _chunk.AddName(name);
                            _chunk.EmitWithU16(OpCode.DeclareGlobal, idx);
                        }
                    }
                }
                HoistInStatement(f.Body);
                break;
            case ForOfStatement fo:
                if (fo.Left is VariableDeclaration fovd && fovd.Kind == VariableDeclarationKind.Var)
                {
                    foreach (var d in fovd.Declarations)
                    {
                        foreach (var name in CollectPatternNames(d.Id))
                        {
                            int idx = _chunk.AddName(name);
                            _chunk.EmitWithU16(OpCode.DeclareGlobal, idx);
                        }
                    }
                }
                HoistInStatement(fo.Body);
                break;
            case LabeledStatement l:
                HoistInStatement(l.Body);
                break;
            // Statements with no nested statements or no
            // declarations inside them need no hoisting.
        }
    }

    /// <summary>
    /// Compile a function body into a standalone
    /// <see cref="JsFunctionTemplate"/>. Uses a fresh compiler
    /// frame so inner loops / vars / nested functions don't
    /// contaminate the outer frame's chunk.
    /// </summary>
    private JsFunctionTemplate CompileFunctionTemplate(
        string? name,
        IReadOnlyList<FunctionParameter> paramNodes,
        BlockStatement body,
        int sourceLength,
        bool isArrow = false,
        bool isGenerator = false)
    {
        PushFrame(isFunction: true);

        // Defaults: emit at function entry, *before* the body
        // hoist/block setup, so the param binding already lives
        // in the function env (the VM bound it from the caller's
        // args). Rest params are handled by the VM itself via
        // the template's RestParamIndex — no emission here.
        EmitParameterDefaults(paramNodes);

        // Hoist vars + nested function declarations to function
        // scope. Let/const are skipped here — they are handled
        // by the block scope established by CompileBlock.
        HoistDeclarations(body.Body);

        // Compile the body through CompileBlock so it gets the
        // standard PushEnv / HoistBlockLetConst / PopEnv
        // treatment. Function-body let/const live in a scope
        // one level below the function env, which is harmless
        // and keeps the block-scoping path unified.
        CompileBlock(body);

        // Implicit `return undefined;` at the end of every
        // function body. If the body already returned, this is
        // unreachable bytecode — harmless.
        _chunk.Emit(OpCode.PushUndefined);
        _chunk.Emit(OpCode.Return);

        var chunk = PopFrame();

        // Gather param names and locate the rest param (if any).
        // The parser guarantees at most one rest param and that
        // it is last, so RestParamIndex is either -1 or the last
        // index.
        var paramNames = new List<string>(paramNodes.Count);
        int restIndex = -1;
        for (int i = 0; i < paramNodes.Count; i++)
        {
            var p = paramNodes[i];
            if (p.Target is not Identifier idName)
            {
                throw new JsCompileException(
                    "Parameter destructuring is not supported in this slice",
                    p.Start);
            }
            paramNames.Add(idName.Name);
            if (p.IsRest) restIndex = i;
        }
        return new JsFunctionTemplate(
            chunk,
            paramNames,
            name,
            sourceLength,
            isArrow,
            restIndex,
            isGenerator);
    }

    /// <summary>
    /// For each parameter that carries a default value, emit
    /// bytecode at function entry that checks whether the
    /// current binding is strictly equal to <c>undefined</c>,
    /// and if so replaces it with the default expression's
    /// value. The default expression is lowered to normal
    /// compiled bytecode — it sees the enclosing closure
    /// exactly as a body expression would, and earlier
    /// parameters are visible because they are already bound
    /// in the function env at this point.
    ///
    /// Rest parameters do not participate — the VM binds them
    /// to a fresh array of leftover args at call time.
    /// </summary>
    private void EmitParameterDefaults(IReadOnlyList<FunctionParameter> paramNodes)
    {
        foreach (var p in paramNodes)
        {
            if (p.IsRest || p.Default is null) continue;
            if (p.Target is not Identifier id) continue;

            int nameIdx = _chunk.AddName(id.Name);
            // Load the current value of the param binding.
            _chunk.EmitWithU16(OpCode.LoadGlobal, nameIdx);
            _chunk.Emit(OpCode.PushUndefined);
            _chunk.Emit(OpCode.StrictEq);
            int skip = _chunk.EmitJump(OpCode.JumpIfFalse);
            // Undefined → evaluate default, store it, discard.
            CompileExpression(p.Default);
            _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
            _chunk.Emit(OpCode.Pop);
            _chunk.PatchJump(skip);
        }
    }

    /// <summary>
    /// Compile an ES2015 template literal. Lowers to a sequence
    /// of string concatenations: push the first quasi, then for
    /// each interpolation, compile the expression, <c>Add</c>
    /// (which performs string concatenation when either operand
    /// is a string), push the next quasi, <c>Add</c> again.
    /// Non-string expression values are coerced to string by
    /// the existing <c>DoAdd</c> path because one operand is
    /// always a string.
    /// </summary>
    private void CompileTemplateLiteral(TemplateLiteral tl)
    {
        // There's always at least one quasi (the Quasis/Expressions
        // invariant in the AST guarantees Count == Expressions.Count + 1).
        int idx0 = _chunk.AddConstant(tl.Quasis[0]);
        _chunk.EmitWithU16(OpCode.PushConst, idx0);

        for (int i = 0; i < tl.Expressions.Count; i++)
        {
            CompileExpression(tl.Expressions[i]);
            _chunk.Emit(OpCode.Add);
            int idxN = _chunk.AddConstant(tl.Quasis[i + 1]);
            _chunk.EmitWithU16(OpCode.PushConst, idxN);
            _chunk.Emit(OpCode.Add);
        }
    }

    /// <summary>
    /// Compile an ES2015 arrow function expression. For the
    /// concise expression body form, we synthesize a tiny
    /// <c>{ return expr; }</c> wrapper so the compile path
    /// reuses the regular function-body machinery. The
    /// resulting template is tagged <c>IsArrow = true</c> so
    /// the VM's <see cref="OpCode.MakeFunction"/> knows to
    /// capture the current <c>this</c> and the call machinery
    /// knows to skip the <c>arguments</c> binding.
    /// </summary>
    private void CompileArrowFunctionExpression(ArrowFunctionExpression ae)
    {
        BlockStatement block;
        if (ae.Body is Expression expr)
        {
            var ret = new ReturnStatement(expr.Start, expr.End, expr);
            block = new BlockStatement(
                expr.Start,
                expr.End,
                new List<Statement> { ret });
        }
        else
        {
            block = (BlockStatement)ae.Body;
        }
        var template = CompileFunctionTemplate(
            name: null,
            ae.Params,
            block,
            ae.End - ae.Start,
            isArrow: true);
        int idx = _chunk.AddConstant(template);
        _chunk.EmitWithU16(OpCode.MakeFunction, idx);
    }

    // -------------------------------------------------------------------
    // Statements
    // -------------------------------------------------------------------

    private void CompileStatement(Statement stmt)
    {
        switch (stmt)
        {
            case EmptyStatement:
                return;

            case BlockStatement b:
                CompileBlock(b);
                return;

            case ExpressionStatement es:
                CompileExpression(es.Expression);
                // Top-level completion tracking: every expression
                // statement's value becomes the candidate completion
                // value for the program.
                _chunk.Emit(OpCode.StoreCompletion);
                return;

            case VariableDeclaration vd:
                CompileVariableInitializers(vd);
                return;

            case IfStatement ifs:
                CompileIf(ifs);
                return;

            case WhileStatement ws:
                CompileWhile(ws);
                return;

            case DoWhileStatement dws:
                CompileDoWhile(dws);
                return;

            case ForStatement fs:
                CompileFor(fs);
                return;

            case BreakStatement bs:
                {
                    var target = ResolveBreakTarget(bs.Label?.Name, bs.Start);
                    int addr = _chunk.EmitJump(OpCode.Jump);
                    target.BreakJumps.Add(addr);
                }
                return;

            case ContinueStatement cs:
                {
                    var target = ResolveContinueTarget(cs.Label?.Name, cs.Start);
                    int addr = _chunk.EmitJump(OpCode.Jump);
                    target.ContinueJumps.Add(addr);
                }
                return;

            case DebuggerStatement:
                // No-op — no debugger is attached.
                return;

            case FunctionDeclaration:
                // Already emitted during the hoisting pass.
                return;

            case ClassDeclaration cd:
                // Build the class value on the stack, then store
                // it under the declared name. The name was
                // pre-declared by HoistBlockScopedDeclarations so
                // the StoreGlobal here lifts it out of the TDZ.
                CompileClassAssembly(cd.Id.Name, cd.SuperClass, cd.Body);
                {
                    int nameIdx = _chunk.AddName(cd.Id.Name);
                    _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
                    _chunk.Emit(OpCode.Pop);
                }
                return;

            case ReturnStatement rs:
                if (!InFunction())
                {
                    throw new JsCompileException(
                        "'return' outside of function", rs.Start);
                }
                if (rs.Argument is null)
                {
                    _chunk.Emit(OpCode.PushUndefined);
                }
                else
                {
                    CompileExpression(rs.Argument);
                }
                _chunk.Emit(OpCode.Return);
                return;

            case ThrowStatement ts:
                CompileExpression(ts.Argument);
                _chunk.Emit(OpCode.Throw);
                return;

            case TryStatement tr:
                CompileTry(tr);
                return;

            case SwitchStatement sw:
                CompileSwitch(sw);
                return;

            case ForInStatement fi:
                CompileForIn(fi);
                return;

            case ForOfStatement fo:
                CompileForOf(fo);
                return;

            case LabeledStatement l:
                CompileLabeledStatement(l);
                return;

            case WithStatement w:
                throw new JsCompileException(
                    "'with' is not supported (deferred indefinitely)", w.Start);

            default:
                throw new JsCompileException(
                    $"Unsupported statement: {stmt.GetType().Name}", stmt.Start);
        }
    }

    private void CompileVariableInitializers(VariableDeclaration vd)
    {
        foreach (var d in vd.Declarations)
        {
            if (d.Init is not null)
            {
                CompileExpression(d.Init);
            }
            else if (vd.Kind != VariableDeclarationKind.Var)
            {
                // `let x;` — the declaration clears the TDZ by
                // explicitly storing undefined. (A `var` without
                // an initializer is already undefined from the
                // hoist pass, so we skip.)
                _chunk.Emit(OpCode.PushUndefined);
            }
            else
            {
                continue;
            }
            // Bind the init value (now on TOS) to the declarator
            // target. For a simple identifier this is just a
            // StoreGlobal+Pop; for object/array patterns it is a
            // recursive walk that emits Dup+GetProperty chains.
            CompilePatternBinding(d.Id);
        }
    }

    // -------------------------------------------------------------------
    // Destructuring patterns (slice 3b-4)
    // -------------------------------------------------------------------
    //
    // Patterns are a purely compile-time lowering to the existing
    // Dup / GetProperty / GetPropertyComputed / StoreGlobal opcodes.
    // The VM never sees a pattern node directly. Each pattern-binding
    // emit consumes the single source value that sits at TOS when
    // CompilePatternBinding is called, and leaves the stack net-zero.
    //
    // Default values (e.g. `{a = 1}`) are evaluated lazily — the
    // generated code tests the extracted slot against `undefined`
    // via a strict-equal and only evaluates the default expression
    // if needed.
    //
    // Limitations intentionally deferred from this slice:
    //   - rest elements  (`...rest`) in patterns
    //   - function parameter destructuring
    //   - assignment-expression destructuring (non-declaration LHS)
    //   - destructuring inside for..in / for..of heads
    // These are picked up in later phase-3b slices.

    /// <summary>
    /// Walk a binding target (simple identifier or destructuring
    /// pattern) and collect the set of variable names it
    /// introduces. Used by all hoisting call sites that need to
    /// emit <see cref="OpCode.DeclareGlobal"/> or
    /// <see cref="OpCode.DeclareLet"/> for each bound name before
    /// the initializer runs.
    /// </summary>
    private static IEnumerable<string> CollectPatternNames(JsNode target)
    {
        var names = new List<string>();
        WalkPatternNames(target, names);
        return names;
    }

    private static void WalkPatternNames(JsNode target, List<string> names)
    {
        switch (target)
        {
            case Identifier id:
                names.Add(id.Name);
                break;
            case ObjectPattern op:
                foreach (var p in op.Properties)
                {
                    WalkPatternNames(p.Value, names);
                }
                break;
            case ArrayPattern ap:
                foreach (var e in ap.Elements)
                {
                    if (e is not null) WalkPatternNames(e.Target, names);
                }
                break;
            default:
                throw new JsCompileException(
                    $"Unsupported binding target: {target.GetType().Name}",
                    target.Start);
        }
    }

    /// <summary>
    /// Emit code that binds the value currently on top of the
    /// VM stack to the given pattern target, consuming the
    /// value. For an <see cref="Identifier"/> this is the simple
    /// StoreGlobal+Pop sequence; for object and array patterns
    /// it recursively destructures the source value.
    /// </summary>
    private void CompilePatternBinding(JsNode target)
    {
        switch (target)
        {
            case Identifier id:
                {
                    int nameIdx = _chunk.AddName(id.Name);
                    _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
                    _chunk.Emit(OpCode.Pop);
                    return;
                }
            case ObjectPattern op:
                CompileObjectPatternBinding(op);
                return;
            case ArrayPattern ap:
                CompileArrayPatternBinding(ap);
                return;
            default:
                throw new JsCompileException(
                    $"Unsupported binding target: {target.GetType().Name}",
                    target.Start);
        }
    }

    private void CompileObjectPatternBinding(ObjectPattern pattern)
    {
        // Source value sits at TOS. For each property:
        //   Dup                     // [src, src]
        //   GetProperty <key>       // [src, val]
        //   (default handling)
        //   CompilePatternBinding   // consumes val, leaves [src]
        // Then Pop to drop the source.
        foreach (var prop in pattern.Properties)
        {
            _chunk.Emit(OpCode.Dup);
            int keyIdx = _chunk.AddName(prop.Key);
            _chunk.EmitWithU16(OpCode.GetProperty, keyIdx);
            EmitDefaultIfUndefined(prop.Default);
            CompilePatternBinding(prop.Value);
        }
        _chunk.Emit(OpCode.Pop);
    }

    private void CompileArrayPatternBinding(ArrayPattern pattern)
    {
        // Source value sits at TOS. For each non-null element:
        //   Dup                     // [src, src]
        //   PushConst <index>       // [src, src, i]
        //   GetPropertyComputed     // [src, val]
        //   (default handling)
        //   CompilePatternBinding   // consumes val, leaves [src]
        // Null elements are elisions — skip them but still advance
        // the source index.
        //
        // A rest element (`...target`) captures a fresh array of
        // the tail starting at its index. Lowered as:
        //   CreateArray 0           // [src, restArr]
        //   for each tail index j:
        //     Swap                  // [restArr, src]
        //     Dup                   // [restArr, src, src]
        //     PushConst <j>         // [restArr, src, src, j]
        //     GetPropertyComputed   // [restArr, src, val]
        //     ... we want [src, restArr, val] — manage with Swap
        // Simpler: resolve the tail length at runtime by loading
        // the source's `length` and looping. But we have no loop
        // opcode here. Given the element count is known-small in
        // practice (tests use short arrays), we lower rest by
        // calling Array.prototype.slice on the source.
        for (int i = 0; i < pattern.Elements.Count; i++)
        {
            var e = pattern.Elements[i];
            if (e is null) continue;
            if (e.IsRest)
            {
                // [src] → [src, src.slice(i)]
                //
                // We emit a call to Array.prototype.slice via a
                // method-call layout: [src, slice, src, i] then
                // Call 1 → [src, tailArr]. Then bind tailArr to
                // the target (which consumes it) leaving [src].
                _chunk.Emit(OpCode.Dup);                                  // [src, src]
                int sliceIdx = _chunk.AddName("slice");
                _chunk.Emit(OpCode.Dup);                                  // [src, src, src]
                _chunk.EmitWithU16(OpCode.GetProperty, sliceIdx);         // [src, src, slice]
                _chunk.Emit(OpCode.Swap);                                 // [src, slice, src]
                int iConstIdx = _chunk.AddConstant((double)i);
                _chunk.EmitWithU16(OpCode.PushConst, iConstIdx);          // [src, slice, src, i]
                _chunk.EmitWithU8(OpCode.Call, 1);                        // [src, tailArr]
                CompilePatternBinding(e.Target);                          // consumes tailArr, leaves [src]
                continue;
            }
            _chunk.Emit(OpCode.Dup);
            int constIdx = _chunk.AddConstant((double)i);
            _chunk.EmitWithU16(OpCode.PushConst, constIdx);
            _chunk.Emit(OpCode.GetPropertyComputed);
            EmitDefaultIfUndefined(e.Default);
            CompilePatternBinding(e.Target);
        }
        _chunk.Emit(OpCode.Pop);
    }

    /// <summary>
    /// If <paramref name="defaultExpr"/> is non-null, emit code
    /// that inspects the value currently at TOS and, if it is
    /// strictly equal to <c>undefined</c>, replaces it with
    /// the evaluated default expression. Leaves the chosen
    /// value at TOS either way. No-op if there is no default.
    /// </summary>
    private void EmitDefaultIfUndefined(Expression? defaultExpr)
    {
        if (defaultExpr is null) return;
        // Stack: [val]
        _chunk.Emit(OpCode.Dup);                   // [val, val]
        _chunk.Emit(OpCode.PushUndefined);         // [val, val, undef]
        _chunk.Emit(OpCode.StrictEq);              // [val, isUndef]
        int notUndef = _chunk.EmitJump(OpCode.JumpIfFalse); // consumes bool
        // val === undefined branch: discard val, push default.
        _chunk.Emit(OpCode.Pop);                   // []
        CompileExpression(defaultExpr);            // [defval]
        int afterDefault = _chunk.EmitJump(OpCode.Jump);
        _chunk.PatchJump(notUndef);                // land here: [val]
        _chunk.PatchJump(afterDefault);            // both branches converge with chosen value at TOS
    }

    // -------------------------------------------------------------------
    // Control flow
    // -------------------------------------------------------------------

    private void CompileIf(IfStatement stmt)
    {
        CompileExpression(stmt.Test);
        int elseJump = _chunk.EmitJump(OpCode.JumpIfFalse);
        CompileStatement(stmt.Consequent);

        if (stmt.Alternate is null)
        {
            _chunk.PatchJump(elseJump);
        }
        else
        {
            int endJump = _chunk.EmitJump(OpCode.Jump);
            _chunk.PatchJump(elseJump);
            CompileStatement(stmt.Alternate);
            _chunk.PatchJump(endJump);
        }
    }

    private void CompileWhile(WhileStatement stmt)
    {
        var label = TakePendingLabel();
        int loopStart = _chunk.Position;
        CompileExpression(stmt.Test);
        int exitJump = _chunk.EmitJump(OpCode.JumpIfFalse);

        var ctx = new BreakTarget { IsLoop = true, Label = label };
        _breakTargets.Push(ctx);
        CompileStatement(stmt.Body);
        _breakTargets.Pop();

        // Continue jumps land at the top of the loop (re-test).
        foreach (var addr in ctx.ContinueJumps)
        {
            _chunk.PatchJumpTo(addr, loopStart);
        }
        _chunk.EmitLoopJump(OpCode.Jump, loopStart);
        _chunk.PatchJump(exitJump);
        foreach (var addr in ctx.BreakJumps) _chunk.PatchJump(addr);
    }

    private void CompileDoWhile(DoWhileStatement stmt)
    {
        var label = TakePendingLabel();
        int loopStart = _chunk.Position;

        var ctx = new BreakTarget { IsLoop = true, Label = label };
        _breakTargets.Push(ctx);
        CompileStatement(stmt.Body);
        _breakTargets.Pop();

        // Continue jumps land at the condition check.
        int continueTarget = _chunk.Position;
        foreach (var addr in ctx.ContinueJumps)
        {
            _chunk.PatchJumpTo(addr, continueTarget);
        }

        CompileExpression(stmt.Test);
        _chunk.EmitLoopJump(OpCode.JumpIfTrue, loopStart);
        foreach (var addr in ctx.BreakJumps) _chunk.PatchJump(addr);
    }

    private void CompileFor(ForStatement stmt)
    {
        var label = TakePendingLabel();

        // If the init is a let/const declaration, wrap the whole
        // loop in a fresh env and pre-declare the init names.
        // The binding is shared across iterations (slice 3b-1
        // does not implement per-iteration scope freshness —
        // that needs closure-capture support for the outer for
        // body, deferred to a later slice).
        bool loopScope =
            stmt.Init is VariableDeclaration initDecl &&
            initDecl.Kind != VariableDeclarationKind.Var;

        if (loopScope)
        {
            _chunk.Emit(OpCode.PushEnv);
            var vdInit = (VariableDeclaration)stmt.Init!;
            foreach (var d in vdInit.Declarations)
            {
                foreach (var name in CollectPatternNames(d.Id))
                {
                    int idx = _chunk.AddName(name);
                    _chunk.EmitWithU16(OpCode.DeclareLet, idx);
                }
            }
        }

        // Init.
        if (stmt.Init is VariableDeclaration vd)
        {
            CompileVariableInitializers(vd);
        }
        else if (stmt.Init is Expression e)
        {
            CompileExpression(e);
            _chunk.Emit(OpCode.Pop);
        }

        int loopStart = _chunk.Position;
        int exitJump = -1;
        if (stmt.Test is not null)
        {
            CompileExpression(stmt.Test);
            exitJump = _chunk.EmitJump(OpCode.JumpIfFalse);
        }

        var ctx = new BreakTarget { IsLoop = true, Label = label };
        _breakTargets.Push(ctx);
        CompileStatement(stmt.Body);
        _breakTargets.Pop();

        // Continue target is the update clause.
        int continueTarget = _chunk.Position;
        foreach (var addr in ctx.ContinueJumps)
        {
            _chunk.PatchJumpTo(addr, continueTarget);
        }
        if (stmt.Update is not null)
        {
            CompileExpression(stmt.Update);
            _chunk.Emit(OpCode.Pop);
        }
        _chunk.EmitLoopJump(OpCode.Jump, loopStart);

        if (exitJump != -1) _chunk.PatchJump(exitJump);
        foreach (var addr in ctx.BreakJumps) _chunk.PatchJump(addr);

        if (loopScope)
        {
            _chunk.Emit(OpCode.PopEnv);
        }
    }

    // -------------------------------------------------------------------
    // Expressions
    // -------------------------------------------------------------------

    private void CompileExpression(Expression expr)
    {
        switch (expr)
        {
            case Literal lit:
                CompileLiteral(lit);
                return;

            case Identifier id:
                {
                    int idx = _chunk.AddName(id.Name);
                    _chunk.EmitWithU16(OpCode.LoadGlobal, idx);
                }
                return;

            case ThisExpression:
                _chunk.Emit(OpCode.LoadThis);
                return;

            case UnaryExpression u:
                CompileUnary(u);
                return;

            case UpdateExpression upd:
                CompileUpdate(upd);
                return;

            case BinaryExpression b:
                CompileExpression(b.Left);
                CompileExpression(b.Right);
                _chunk.Emit(BinaryOpCode(b.Operator));
                return;

            case MemberExpression m:
                CompileMemberRead(m);
                return;

            case ArrayExpression ae:
                CompileArrayExpression(ae);
                return;

            case ObjectExpression oe:
                CompileObjectExpression(oe);
                return;

            case LogicalExpression l:
                CompileLogical(l);
                return;

            case AssignmentExpression a:
                CompileAssignment(a);
                return;

            case ConditionalExpression c:
                CompileConditional(c);
                return;

            case SequenceExpression s:
                for (int i = 0; i < s.Expressions.Count; i++)
                {
                    CompileExpression(s.Expressions[i]);
                    if (i < s.Expressions.Count - 1) _chunk.Emit(OpCode.Pop);
                }
                return;

            case CallExpression ce:
                CompileCall(ce);
                return;
            case NewExpression ne:
                CompileNew(ne);
                return;
            case FunctionExpression fe:
                CompileFunctionExpression(fe);
                return;
            case ArrowFunctionExpression ae:
                CompileArrowFunctionExpression(ae);
                return;
            case TemplateLiteral tl:
                CompileTemplateLiteral(tl);
                return;
            case ClassExpression ce2:
                CompileClassAssembly(ce2.Id?.Name, ce2.SuperClass, ce2.Body);
                return;
            case YieldExpression ye:
                // yield arg: push the argument (or undefined),
                // then YieldValue halts the generator. On
                // resume, YieldResume pushes the sent value as
                // the result of the yield expression.
                if (ye.Argument is null)
                {
                    _chunk.Emit(OpCode.PushUndefined);
                }
                else
                {
                    CompileExpression(ye.Argument);
                }
                _chunk.Emit(OpCode.YieldValue);
                _chunk.Emit(OpCode.YieldResume);
                return;
            case Super sup:
                // Bare `super` by itself is not valid — only as
                // the object of a member access or as the callee
                // of a call. Those cases are intercepted by
                // CompileCall / member access compilation. Any
                // other reference is a compile-time error.
                throw new JsCompileException(
                    "'super' must be followed by a member access or call",
                    sup.Start);

            default:
                throw new JsCompileException(
                    $"Unsupported expression: {expr.GetType().Name}", expr.Start);
        }
    }

    private void CompileLiteral(Literal lit)
    {
        switch (lit.Kind)
        {
            case LiteralKind.Null:
                _chunk.Emit(OpCode.PushNull);
                return;
            case LiteralKind.Boolean:
                _chunk.Emit((bool)lit.Value! ? OpCode.PushTrue : OpCode.PushFalse);
                return;
            case LiteralKind.Number:
            case LiteralKind.String:
                {
                    int idx = _chunk.AddConstant(lit.Value);
                    _chunk.EmitWithU16(OpCode.PushConst, idx);
                }
                return;
        }
    }

    private void CompileUnary(UnaryExpression u)
    {
        // typeof identifier is special — per spec it must not throw
        // a ReferenceError on an undeclared name. We detect this
        // case and emit a typeof-safe sequence that treats
        // undeclared globals as undefined.
        if (u.Operator == UnaryOperator.TypeOf && u.Argument is Identifier idArg)
        {
            int idx = _chunk.AddName(idArg.Name);
            _chunk.EmitWithU16(OpCode.LoadGlobalOrUndefined, idx);
            _chunk.Emit(OpCode.TypeOf);
            return;
        }

        if (u.Operator == UnaryOperator.Delete)
        {
            // delete on an identifier — non-strict ES5 allows it and
            // returns false for declared bindings, true for anything
            // that was never a binding.
            if (u.Argument is Identifier idDel)
            {
                int idx = _chunk.AddName(idDel.Name);
                _chunk.EmitWithU16(OpCode.DeleteGlobal, idx);
                return;
            }
            // delete on a member access — the common case: remove
            // an own property from an object.
            if (u.Argument is MemberExpression mDel)
            {
                CompileExpression(mDel.Object);
                if (mDel.Computed)
                {
                    CompileExpression(mDel.Property);
                    _chunk.Emit(OpCode.DeletePropertyComputed);
                }
                else
                {
                    int nameIdx = _chunk.AddName(((Identifier)mDel.Property).Name);
                    _chunk.EmitWithU16(OpCode.DeleteProperty, nameIdx);
                }
                return;
            }
            // delete of a non-reference is a no-op that returns true.
            CompileExpression(u.Argument);
            _chunk.Emit(OpCode.Pop);
            _chunk.Emit(OpCode.PushTrue);
            return;
        }

        CompileExpression(u.Argument);
        switch (u.Operator)
        {
            case UnaryOperator.Minus: _chunk.Emit(OpCode.Negate); return;
            case UnaryOperator.Plus: _chunk.Emit(OpCode.UnaryPlus); return;
            case UnaryOperator.LogicalNot: _chunk.Emit(OpCode.LogicalNot); return;
            case UnaryOperator.BitwiseNot: _chunk.Emit(OpCode.BitNot); return;
            case UnaryOperator.TypeOf: _chunk.Emit(OpCode.TypeOf); return;
            case UnaryOperator.Void:
                _chunk.Emit(OpCode.Pop);
                _chunk.Emit(OpCode.PushUndefined);
                return;
            default:
                throw new JsCompileException($"Unsupported unary operator: {u.Operator}", u.Start);
        }
    }

    private void CompileUpdate(UpdateExpression upd)
    {
        var addOrSub = upd.Operator == UpdateOperator.Increment ? OpCode.Add : OpCode.Sub;

        if (upd.Argument is Identifier id)
        {
            int nameIdx = _chunk.AddName(id.Name);
            if (upd.Prefix)
            {
                // ++x: load, add 1, store (store leaves new on stack).
                _chunk.EmitWithU16(OpCode.LoadGlobal, nameIdx);
                EmitPushOne();
                _chunk.Emit(addOrSub);
                _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
            }
            else
            {
                // x++: load, UnaryPlus to coerce to Number (ES5 §11.3.1:
                // the result of postfix is the numeric version of the
                // old value), dup, add 1, store, pop — leaves old.
                _chunk.EmitWithU16(OpCode.LoadGlobal, nameIdx);
                _chunk.Emit(OpCode.UnaryPlus);
                _chunk.Emit(OpCode.Dup);
                EmitPushOne();
                _chunk.Emit(addOrSub);
                _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
                _chunk.Emit(OpCode.Pop);
            }
            return;
        }

        if (upd.Argument is MemberExpression m)
        {
            if (!m.Computed)
            {
                int nameIdx = _chunk.AddName(((Identifier)m.Property).Name);
                if (upd.Prefix)
                {
                    // ++obj.x  →  [obj] [obj old] [obj oldN] [obj oldN 1] [obj new] set → [new]
                    CompileExpression(m.Object);
                    _chunk.Emit(OpCode.Dup);
                    _chunk.EmitWithU16(OpCode.GetProperty, nameIdx);
                    _chunk.Emit(OpCode.UnaryPlus);
                    EmitPushOne();
                    _chunk.Emit(addOrSub);
                    _chunk.EmitWithU16(OpCode.SetProperty, nameIdx);
                }
                else
                {
                    // obj.x++  →  compute new via scratch to save old.
                    // [obj] [obj obj] [obj old] [obj oldN]
                    // StoreScratch → [obj]  (scratch = oldN)
                    // LoadScratch → [obj oldN]
                    // [obj oldN 1] [obj new]
                    // SetProperty → [new]
                    // Pop → []
                    // LoadScratch → [oldN]
                    CompileExpression(m.Object);
                    _chunk.Emit(OpCode.Dup);
                    _chunk.EmitWithU16(OpCode.GetProperty, nameIdx);
                    _chunk.Emit(OpCode.UnaryPlus);
                    _chunk.Emit(OpCode.StoreScratch);
                    _chunk.Emit(OpCode.LoadScratch);
                    EmitPushOne();
                    _chunk.Emit(addOrSub);
                    _chunk.EmitWithU16(OpCode.SetProperty, nameIdx);
                    _chunk.Emit(OpCode.Pop);
                    _chunk.Emit(OpCode.LoadScratch);
                }
                return;
            }

            // Computed member update — obj[key]++
            if (upd.Prefix)
            {
                // ++obj[k]
                CompileExpression(m.Object);
                CompileExpression(m.Property);
                _chunk.Emit(OpCode.Dup2);
                _chunk.Emit(OpCode.GetPropertyComputed);
                _chunk.Emit(OpCode.UnaryPlus);
                EmitPushOne();
                _chunk.Emit(addOrSub);
                _chunk.Emit(OpCode.SetPropertyComputed);
            }
            else
            {
                // obj[k]++  — same scratch pattern as obj.x++.
                CompileExpression(m.Object);
                CompileExpression(m.Property);
                _chunk.Emit(OpCode.Dup2);
                _chunk.Emit(OpCode.GetPropertyComputed);
                _chunk.Emit(OpCode.UnaryPlus);
                _chunk.Emit(OpCode.StoreScratch);
                _chunk.Emit(OpCode.LoadScratch);
                EmitPushOne();
                _chunk.Emit(addOrSub);
                _chunk.Emit(OpCode.SetPropertyComputed);
                _chunk.Emit(OpCode.Pop);
                _chunk.Emit(OpCode.LoadScratch);
            }
            return;
        }

        throw new JsCompileException(
            "Invalid target for ++/--",
            upd.Start);
    }

    private void EmitPushOne()
    {
        int oneIdx = _chunk.AddConstant(1.0);
        _chunk.EmitWithU16(OpCode.PushConst, oneIdx);
    }

    private void CompileLogical(LogicalExpression l)
    {
        // &&: evaluate left; if falsy, leave it on the stack and skip
        //     the right; otherwise pop it and evaluate the right.
        // ||: same pattern but inverted.
        CompileExpression(l.Left);
        var jumpOp = l.Operator == LogicalOperator.And
            ? OpCode.JumpIfFalseKeep
            : OpCode.JumpIfTrueKeep;
        int skip = _chunk.EmitJump(jumpOp);
        _chunk.Emit(OpCode.Pop);
        CompileExpression(l.Right);
        _chunk.PatchJump(skip);
    }

    private void CompileAssignment(AssignmentExpression a)
    {
        switch (a.Left)
        {
            case Identifier id:
                CompileAssignmentToIdentifier(a, id);
                return;
            case MemberExpression m:
                CompileAssignmentToMember(a, m);
                return;
            default:
                throw new JsCompileException(
                    "Invalid assignment target", a.Start);
        }
    }

    private void CompileAssignmentToIdentifier(AssignmentExpression a, Identifier id)
    {
        int nameIdx = _chunk.AddName(id.Name);

        if (a.Operator == AssignmentOperator.Assign)
        {
            CompileExpression(a.Right);
            _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
            return;
        }

        // Compound: load current, compile right, combine, store.
        _chunk.EmitWithU16(OpCode.LoadGlobal, nameIdx);
        CompileExpression(a.Right);
        _chunk.Emit(CompoundAssignmentOpCode(a.Operator));
        _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
    }

    private void CompileAssignmentToMember(AssignmentExpression a, MemberExpression m)
    {
        if (!m.Computed)
        {
            // obj.name = value  or  obj.name += value
            var name = ((Identifier)m.Property).Name;
            int nameIdx = _chunk.AddName(name);

            if (a.Operator == AssignmentOperator.Assign)
            {
                CompileExpression(m.Object);
                CompileExpression(a.Right);
                _chunk.EmitWithU16(OpCode.SetProperty, nameIdx);
                return;
            }

            // Compound: compile obj, dup for read-then-write,
            // read old, compile rhs, combine, set.
            CompileExpression(m.Object);
            _chunk.Emit(OpCode.Dup);
            _chunk.EmitWithU16(OpCode.GetProperty, nameIdx);
            CompileExpression(a.Right);
            _chunk.Emit(CompoundAssignmentOpCode(a.Operator));
            _chunk.EmitWithU16(OpCode.SetProperty, nameIdx);
            return;
        }

        // obj[key] = value  or  obj[key] += value
        if (a.Operator == AssignmentOperator.Assign)
        {
            CompileExpression(m.Object);
            CompileExpression(m.Property);
            CompileExpression(a.Right);
            _chunk.Emit(OpCode.SetPropertyComputed);
            return;
        }

        // Compound — need obj and key twice.
        CompileExpression(m.Object);
        CompileExpression(m.Property);
        _chunk.Emit(OpCode.Dup2);
        _chunk.Emit(OpCode.GetPropertyComputed);
        CompileExpression(a.Right);
        _chunk.Emit(CompoundAssignmentOpCode(a.Operator));
        _chunk.Emit(OpCode.SetPropertyComputed);
    }

    private void CompileConditional(ConditionalExpression c)
    {
        CompileExpression(c.Test);
        int elseJump = _chunk.EmitJump(OpCode.JumpIfFalse);
        CompileExpression(c.Consequent);
        int endJump = _chunk.EmitJump(OpCode.Jump);
        _chunk.PatchJump(elseJump);
        CompileExpression(c.Alternate);
        _chunk.PatchJump(endJump);
    }

    private void CompileMemberRead(MemberExpression m)
    {
        // `super.foo` / `super[k]` as an expression (not a call
        // callee): emit LoadSuper in place of the object value,
        // then do the normal property resolution.
        if (m.Object is Super)
        {
            _chunk.Emit(OpCode.LoadSuper);
        }
        else
        {
            CompileExpression(m.Object);
        }
        if (m.Computed)
        {
            CompileExpression(m.Property);
            _chunk.Emit(OpCode.GetPropertyComputed);
        }
        else
        {
            var name = ((Identifier)m.Property).Name;
            int nameIdx = _chunk.AddName(name);
            _chunk.EmitWithU16(OpCode.GetProperty, nameIdx);
        }
    }

    private void CompileArrayExpression(ArrayExpression ae)
    {
        // Fast path: no spread — keep the existing
        // Push...Push + CreateArray<n> layout.
        bool hasSpread = false;
        foreach (var e in ae.Elements)
        {
            if (e is SpreadElement) { hasSpread = true; break; }
        }

        if (!hasSpread)
        {
            // Holes (null entries) are approximated as undefined —
            // phase-3a slice 4a does not distinguish a true array hole
            // from an explicit undefined slot. Tests cover the common
            // cases; the spec distinction matters only for `in` on the
            // index and for sparse iteration, neither of which is
            // reachable yet.
            foreach (var e in ae.Elements)
            {
                if (e is null)
                {
                    _chunk.Emit(OpCode.PushUndefined);
                }
                else
                {
                    CompileExpression(e);
                }
            }
            _chunk.EmitWithU16(OpCode.CreateArray, ae.Elements.Count);
            return;
        }

        // Spread path: build an empty array, then append each
        // element one at a time (ArrayAppend for regular or
        // hole elements, ArrayAppendSpread for spreads).
        _chunk.EmitWithU16(OpCode.CreateArray, 0);
        foreach (var e in ae.Elements)
        {
            if (e is null)
            {
                _chunk.Emit(OpCode.PushUndefined);
                _chunk.Emit(OpCode.ArrayAppend);
            }
            else if (e is SpreadElement sp)
            {
                CompileExpression(sp.Argument);
                _chunk.Emit(OpCode.ArrayAppendSpread);
            }
            else
            {
                CompileExpression(e);
                _chunk.Emit(OpCode.ArrayAppend);
            }
        }
    }

    private void CompileObjectExpression(ObjectExpression oe)
    {
        _chunk.Emit(OpCode.CreateObject);
        foreach (var prop in oe.Properties)
        {
            if (prop.Kind != PropertyKind.Init)
            {
                throw new JsCompileException(
                    "Getter / setter accessor properties are not supported in this slice (phase 3a slice 6)",
                    prop.Start);
            }
            var name = PropertyKeyToName(prop.Key);
            int nameIdx = _chunk.AddName(name);
            CompileExpression(prop.Value);
            _chunk.EmitWithU16(OpCode.InitProperty, nameIdx);
        }
    }

    /// <summary>
    /// Compile a call expression. For a plain identifier callee
    /// (<c>f(...)</c>) <c>this</c> is <c>undefined</c>. For a
    /// method call (<c>obj.method(...)</c> or <c>obj[k](...)</c>)
    /// <c>this</c> is the object before the dot — compiled by
    /// fetching the method off a duplicated object reference and
    /// then swapping the result with the object, so the
    /// <see cref="OpCode.Call"/> opcode sees the canonical
    /// <c>[fn, this, args...]</c> stack layout.
    /// </summary>
    private void CompileCall(CallExpression ce)
    {
        // ES2015 super-call forms. These need special stack
        // setup because `this` must be the caller's `this`,
        // not whatever `super` resolves to.
        //
        //   super(args)       → load super (=parent.prototype),
        //                       read its `constructor`, bind
        //                       caller's `this`, then call with
        //                       the args.
        //   super.foo(args)   → load super, read `foo` off it,
        //                       bind caller's `this`, call.
        if (ce.Callee is Super)
        {
            _chunk.Emit(OpCode.LoadSuper);
            int ctorIdx = _chunk.AddName("constructor");
            _chunk.EmitWithU16(OpCode.GetProperty, ctorIdx);
            _chunk.Emit(OpCode.LoadThis);
            EmitArgsAndDispatchCall(ce);
            return;
        }
        if (ce.Callee is MemberExpression superMember && superMember.Object is Super)
        {
            _chunk.Emit(OpCode.LoadSuper);
            if (superMember.Computed)
            {
                CompileExpression(superMember.Property);
                _chunk.Emit(OpCode.GetPropertyComputed);
            }
            else
            {
                int nameIdx = _chunk.AddName(((Identifier)superMember.Property).Name);
                _chunk.EmitWithU16(OpCode.GetProperty, nameIdx);
            }
            _chunk.Emit(OpCode.LoadThis);
            EmitArgsAndDispatchCall(ce);
            return;
        }

        // Emit the `[fn, this]` header the same way for both
        // the normal and the spread path.
        if (ce.Callee is MemberExpression m)
        {
            CompileExpression(m.Object);
            _chunk.Emit(OpCode.Dup);
            if (m.Computed)
            {
                CompileExpression(m.Property);
                _chunk.Emit(OpCode.GetPropertyComputed);
            }
            else
            {
                int nameIdx = _chunk.AddName(((Identifier)m.Property).Name);
                _chunk.EmitWithU16(OpCode.GetProperty, nameIdx);
            }
            _chunk.Emit(OpCode.Swap);
        }
        else
        {
            CompileExpression(ce.Callee);
            _chunk.Emit(OpCode.PushUndefined);
        }

        EmitArgsAndDispatchCall(ce);
    }

    /// <summary>
    /// Emit the argument list and dispatch opcode for a call
    /// whose <c>[fn, this]</c> prefix is already on the stack.
    /// Chooses between <see cref="OpCode.Call"/> (fixed arity,
    /// fast path) and <see cref="OpCode.CallSpread"/> (runtime
    /// flattened) based on whether any argument is a spread
    /// element.
    /// </summary>
    private void EmitArgsAndDispatchCall(CallExpression ce)
    {
        bool hasSpread = HasSpreadArgument(ce.Arguments);
        if (!hasSpread)
        {
            foreach (var arg in ce.Arguments)
            {
                CompileExpression(arg);
            }
            if (ce.Arguments.Count > byte.MaxValue)
            {
                throw new JsCompileException(
                    "More than 255 call arguments are not supported", ce.Start);
            }
            _chunk.EmitWithU8(OpCode.Call, ce.Arguments.Count);
            return;
        }
        EmitSpreadArgsArray(ce.Arguments);
        _chunk.Emit(OpCode.CallSpread);
    }

    private void CompileNew(NewExpression ne)
    {
        CompileExpression(ne.Callee);
        bool hasSpread = HasSpreadArgument(ne.Arguments);
        if (!hasSpread)
        {
            foreach (var arg in ne.Arguments)
            {
                CompileExpression(arg);
            }
            if (ne.Arguments.Count > byte.MaxValue)
            {
                throw new JsCompileException(
                    "More than 255 constructor arguments are not supported", ne.Start);
            }
            _chunk.EmitWithU8(OpCode.New, ne.Arguments.Count);
            return;
        }
        EmitSpreadArgsArray(ne.Arguments);
        _chunk.Emit(OpCode.NewSpread);
    }

    private static bool HasSpreadArgument(IReadOnlyList<Expression> args)
    {
        foreach (var a in args)
        {
            if (a is SpreadElement) return true;
        }
        return false;
    }

    /// <summary>
    /// Emit bytecode that, given the current call-prefix on
    /// the stack, pushes a single <see cref="JsArray"/> holding
    /// all the arguments in order — with spread elements
    /// flattened in place. Used by <see cref="CompileCall"/>
    /// and <see cref="CompileNew"/> when any argument is a
    /// spread element.
    /// </summary>
    private void EmitSpreadArgsArray(IReadOnlyList<Expression> args)
    {
        _chunk.EmitWithU16(OpCode.CreateArray, 0);
        foreach (var a in args)
        {
            if (a is SpreadElement sp)
            {
                CompileExpression(sp.Argument);
                _chunk.Emit(OpCode.ArrayAppendSpread);
            }
            else
            {
                CompileExpression(a);
                _chunk.Emit(OpCode.ArrayAppend);
            }
        }
    }

    private void CompileFunctionExpression(FunctionExpression fe)
    {
        var template = CompileFunctionTemplate(
            fe.Id?.Name,
            fe.Params,
            fe.Body,
            fe.End - fe.Start,
            isGenerator: fe.IsGenerator);
        int idx = _chunk.AddConstant(template);
        _chunk.EmitWithU16(OpCode.MakeFunction, idx);
    }

    // -------------------------------------------------------------------
    // Classes (ES2015)
    // -------------------------------------------------------------------
    //
    // Lowering strategy:
    //
    //   1. If there's an `extends` clause, evaluate the parent
    //      expression, leaving it on the stack.
    //   2. Materialize the constructor function. If the class
    //      body has no `constructor()` entry, synthesize one —
    //      an empty body for non-extending classes, or a
    //      pass-through `function(...args){super(...args)}`
    //      for extending classes (which is itself compiled
    //      through this file, including the super-call lowering).
    //   3. For `extends`: Swap so parent is on top of class,
    //      then `SetupSubclass` wires the prototype chain and
    //      pops the parent. `LinkConstructorSuper` copies the
    //      chained prototype into the constructor's
    //      HomeSuper slot.
    //   4. Duplicate the class on the stack so method
    //      installation opcodes can consume the top copy while
    //      leaving one for the final store.
    //   5. For each non-constructor method, materialize it and
    //      use `InstallMethod` / `InstallStaticMethod` to
    //      install it as non-enumerable on `class.prototype` or
    //      `class`. These opcodes also set the method's
    //      HomeSuper from the chained prototype.
    //   6. Pop the duplicate. The single remaining class value
    //      is left on TOS — the caller (class declaration
    //      statement or class expression in an expression
    //      position) does whatever storage it needs.
    //
    /// <summary>
    /// Emit the class-assembly sequence for a class
    /// declaration or class expression. Leaves the final
    /// class value on top of the stack.
    /// </summary>
    private void CompileClassAssembly(
        string? className,
        Expression? superClass,
        ClassBody body)
    {
        // Locate user-provided constructor, if any.
        MethodDefinition? userCtor = null;
        foreach (var m in body.Methods)
        {
            if (m.Kind == MethodDefinitionKind.Constructor && !m.IsStatic)
            {
                if (userCtor is not null)
                {
                    throw new JsCompileException(
                        "A class may only have one constructor",
                        m.Start);
                }
                userCtor = m;
            }
            else if (m.Kind != MethodDefinitionKind.Method)
            {
                throw new JsCompileException(
                    "Getter / setter class methods are not supported in this slice",
                    m.Start);
            }
        }

        // Build the constructor template. When no user
        // constructor is given, synthesize one:
        //   with extends:  constructor(...args){ super(...args); }
        //   without:       constructor(){}
        FunctionExpression ctorFn;
        if (userCtor is not null)
        {
            ctorFn = userCtor.Value;
        }
        else
        {
            ctorFn = BuildDefaultConstructor(superClass is not null, body.Start, body.End);
        }

        // 1. Evaluate parent expression (if any).
        bool hasExtends = superClass is not null;
        if (hasExtends)
        {
            CompileExpression(superClass!);
        }

        // 2. Materialize constructor function. For extending
        //    classes, it needs the parent's prototype chain
        //    hooked up before super() resolves — but since the
        //    call-time lookup of HomeSuper is lazy, we can link
        //    the prototype chain after MakeFunction.
        var ctorTemplate = CompileFunctionTemplate(
            name: className,
            paramNodes: ctorFn.Params,
            body: ctorFn.Body,
            sourceLength: body.End - body.Start);
        int templateIdx = _chunk.AddConstant(ctorTemplate);
        _chunk.EmitWithU16(OpCode.MakeFunction, templateIdx);

        // Stack: [Parent, ClassFn]   if extends
        //        [ClassFn]            otherwise

        // 3. Hook up the prototype chain.
        if (hasExtends)
        {
            _chunk.Emit(OpCode.Swap);                    // [ClassFn, Parent]
            _chunk.Emit(OpCode.SetupSubclass);           // [ClassFn]  (pops parent)
            _chunk.Emit(OpCode.LinkConstructorSuper);    // [ClassFn]  (ClassFn.HomeSuper = Parent.prototype)
        }

        // 4. Duplicate for the method-installation loop.
        // Stack: [ClassFn, ClassFn]
        _chunk.Emit(OpCode.Dup);

        // 5. Install each method (other than constructor).
        foreach (var m in body.Methods)
        {
            if (m.Kind == MethodDefinitionKind.Constructor) continue;

            var methodTemplate = CompileFunctionTemplate(
                name: m.Key.Name,
                paramNodes: m.Value.Params,
                body: m.Value.Body,
                sourceLength: m.End - m.Start);
            int mIdx = _chunk.AddConstant(methodTemplate);
            _chunk.EmitWithU16(OpCode.MakeFunction, mIdx);

            int nameIdx = _chunk.AddName(m.Key.Name);
            _chunk.EmitWithU16(
                m.IsStatic ? OpCode.InstallStaticMethod : OpCode.InstallMethod,
                nameIdx);
        }

        // 6. Drop the duplicate; leave one class value on the stack.
        _chunk.Emit(OpCode.Pop);
    }

    /// <summary>
    /// Synthesize a default constructor for a class with no
    /// explicit <c>constructor</c> entry. For a class without
    /// <c>extends</c>, the body is empty. For a subclass, the
    /// body is <c>super(...args)</c> so the parent's
    /// constructor sees the same argument list.
    /// </summary>
    private FunctionExpression BuildDefaultConstructor(bool hasExtends, int start, int end)
    {
        if (!hasExtends)
        {
            return new FunctionExpression(
                start: start,
                end: end,
                id: null,
                @params: new List<FunctionParameter>(),
                body: new BlockStatement(start, end, new List<Statement>()));
        }
        // function(...args) { super(...args); }
        var argsIdent = new Identifier(start, end, "args");
        var restParam = new FunctionParameter(
            start: start,
            end: end,
            target: argsIdent,
            @default: null,
            isRest: true);
        var superCall = new CallExpression(
            start: start,
            end: end,
            callee: new Super(start, end),
            arguments: new List<Expression>
            {
                new SpreadElement(start, end, argsIdent),
            });
        var exprStmt = new ExpressionStatement(start, end, superCall);
        var bodyBlock = new BlockStatement(
            start,
            end,
            new List<Statement> { exprStmt });
        return new FunctionExpression(
            start: start,
            end: end,
            id: null,
            @params: new List<FunctionParameter> { restParam },
            body: bodyBlock);
    }

    // -------------------------------------------------------------------
    // for..in
    // -------------------------------------------------------------------

    /// <summary>
    /// Compile a <c>for..in</c> loop. Only identifier (and
    /// <c>var identifier</c>) LHS are supported in this slice —
    /// member-targeted <c>for (a.b in obj)</c> is legal ES5 but
    /// rare in practice and would need extra stack juggling for
    /// the store, so it's deferred.
    /// </summary>
    /// <summary>
    /// Compile a <c>for..of</c> loop over an iterable. Uses
    /// the ES2015 iterator protocol via the
    /// <see cref="OpCode.ForOfStart"/> /
    /// <see cref="OpCode.ForOfNext"/> opcodes, which invoke
    /// <c>Symbol.iterator</c> and <c>next()</c> through the
    /// normal VM call machinery.
    ///
    /// Supported left-hand sides: a plain identifier
    /// (<c>for (x of arr)</c>), a <c>var</c>/<c>let</c>/<c>const</c>
    /// declaration (<c>for (let x of arr)</c>). Destructuring
    /// and member-access LHS are deferred.
    /// </summary>
    private void CompileForOf(ForOfStatement stmt)
    {
        var label = TakePendingLabel();

        // Resolve the binding name and whether this
        // introduces a fresh binding that needs its own env.
        string? bindingName = stmt.Left switch
        {
            Identifier id => id.Name,
            VariableDeclaration vd when vd.Declarations.Count == 1
                && vd.Declarations[0].Id is Identifier ident
                => ident.Name,
            _ => null,
        };
        if (bindingName is null)
        {
            throw new JsCompileException(
                "Only identifier and 'var/let/const identifier' targets are supported in 'for..of' in this slice",
                stmt.Start);
        }

        bool loopScope =
            stmt.Left is VariableDeclaration leftVd &&
            leftVd.Kind != VariableDeclarationKind.Var;

        if (loopScope)
        {
            _chunk.Emit(OpCode.PushEnv);
            int declIdx = _chunk.AddName(bindingName);
            _chunk.EmitWithU16(OpCode.DeclareLet, declIdx);
        }

        int nameIdx = _chunk.AddName(bindingName);

        // Obtain the iterator and push it on the stack. The
        // iterator stays on TOS for the duration of the
        // loop, with [iter, value] at the body entry.
        CompileExpression(stmt.Right);
        _chunk.Emit(OpCode.ForOfStart);

        int loopStart = _chunk.Position;
        int exitJump = _chunk.EmitJump(OpCode.ForOfNext);

        // ForOfNext left [iter, value] on the stack.
        // Store value into the binding name, discard the
        // scratch value, continue into the body.
        _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
        _chunk.Emit(OpCode.Pop);

        var ctx = new BreakTarget { IsLoop = true, Label = label };
        _breakTargets.Push(ctx);
        CompileStatement(stmt.Body);
        _breakTargets.Pop();

        // Continue jumps back to the top of the loop (re-call
        // next()).
        foreach (var addr in ctx.ContinueJumps)
        {
            _chunk.PatchJumpTo(addr, loopStart);
        }
        _chunk.EmitLoopJump(OpCode.Jump, loopStart);

        // ForOfNext patches past the body on done; it already
        // popped the iterator.
        _chunk.PatchJump(exitJump);
        foreach (var addr in ctx.BreakJumps) _chunk.PatchJump(addr);

        if (loopScope)
        {
            _chunk.Emit(OpCode.PopEnv);
        }
    }

    private void CompileForIn(ForInStatement stmt)
    {
        var label = TakePendingLabel();

        // Resolve the binding name from the left-hand side.
        string? bindingName = stmt.Left switch
        {
            Identifier id => id.Name,
            VariableDeclaration vd when vd.Declarations.Count == 1
                && vd.Declarations[0].Id is Identifier ident
                => ident.Name,
            _ => null,
        };
        if (bindingName is null)
        {
            throw new JsCompileException(
                "Only identifier and 'var identifier' targets are supported in 'for..in' in this slice",
                stmt.Start);
        }
        int nameIdx = _chunk.AddName(bindingName);

        // Evaluate the source object and start the iterator.
        CompileExpression(stmt.Right);
        _chunk.Emit(OpCode.ForInStart);

        int loopStart = _chunk.Position;
        int exitJump = _chunk.EmitJump(OpCode.ForInNext);

        // ForInNext leaves [iter, key] on the stack on success.
        // Bind the key and discard it from the stack.
        _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
        _chunk.Emit(OpCode.Pop);

        var ctx = new BreakTarget { IsLoop = true, Label = label };
        _breakTargets.Push(ctx);
        CompileStatement(stmt.Body);
        _breakTargets.Pop();

        // Continue jumps back to ForInNext to advance.
        foreach (var addr in ctx.ContinueJumps)
        {
            _chunk.PatchJumpTo(addr, loopStart);
        }
        _chunk.EmitLoopJump(OpCode.Jump, loopStart);

        // ForInNext patches past the loop body when iteration is
        // done; it already popped the iterator for us.
        _chunk.PatchJump(exitJump);
        foreach (var addr in ctx.BreakJumps) _chunk.PatchJump(addr);
    }

    // -------------------------------------------------------------------
    // switch
    // -------------------------------------------------------------------

    /// <summary>
    /// Compile a <c>switch</c> statement. Layout:
    /// <list type="number">
    /// <item>Evaluate discriminant, leaving it on the stack.</item>
    /// <item>Dispatch table: for each non-default case, <c>Dup</c>,
    /// compile test, <c>StrictEq</c>, <c>JumpIfTrue</c> to the
    /// entry label. <c>JumpIfTrue</c> pops the bool but leaves the
    /// duplicated discriminant — the next <c>Dup</c> sets up for
    /// the following test.</item>
    /// <item>After all tests, <c>Pop</c> the discriminant and
    /// <c>Jump</c> to the default entry (or the exit if no
    /// default).</item>
    /// <item>For each case (in source order), emit an entry label
    /// that does <c>Pop</c> + <c>Jump</c> to the body label, then
    /// lay out the bodies sequentially so fall-through happens by
    /// adjacency.</item>
    /// </list>
    /// </summary>
    private void CompileSwitch(SwitchStatement stmt)
    {
        var label = TakePendingLabel();

        CompileExpression(stmt.Discriminant);
        // Stack: [d]

        int caseCount = stmt.Cases.Count;
        // Jump-to-entry addresses for each case, keyed by case
        // index. Default has no test so no entry jump.
        var caseEntryJumps = new int[caseCount];
        int defaultIndex = -1;
        for (int i = 0; i < caseCount; i++)
        {
            var c = stmt.Cases[i];
            if (c.Test is null)
            {
                defaultIndex = i;
                caseEntryJumps[i] = -1;
                continue;
            }
            _chunk.Emit(OpCode.Dup);                  // [d, d]
            CompileExpression(c.Test);                // [d, d, t]
            _chunk.Emit(OpCode.StrictEq);             // [d, bool]
            caseEntryJumps[i] = _chunk.EmitJump(OpCode.JumpIfTrue);
        }

        // No test matched — discard the discriminant and jump to
        // the default entry (if any) or past the whole switch.
        // Both paths pop the discriminant here, so the default
        // entry (which is reached only from here or from fall-
        // through) starts with a clean stack.
        _chunk.Emit(OpCode.Pop);                      // []
        int noMatchJump = _chunk.EmitJump(OpCode.Jump);

        // Now lay out per-case entry labels. Non-default entries
        // are reached via JumpIfTrue, which left the discriminant
        // on the stack, so they start with a Pop. Default is
        // reached only via the no-match path (already popped) or
        // via fall-through (also already clean), so default
        // entry skips the Pop.
        var entryAddrs = new int[caseCount];
        var entryBodyJumps = new int[caseCount];
        for (int i = 0; i < caseCount; i++)
        {
            entryAddrs[i] = _chunk.Position;
            if (i != defaultIndex)
            {
                _chunk.Emit(OpCode.Pop);             // []
            }
            entryBodyJumps[i] = _chunk.EmitJump(OpCode.Jump);
        }

        // Patch the dispatch-table jumps to their entry labels.
        for (int i = 0; i < caseCount; i++)
        {
            if (caseEntryJumps[i] >= 0)
            {
                _chunk.PatchJumpTo(caseEntryJumps[i], entryAddrs[i]);
            }
        }
        // If there was a default, patch the no-match jump to its
        // entry; otherwise patch it to the exit (end of switch).
        // We don't yet know the exit position for the default-less
        // case, so defer that until after bodies are laid out.

        var ctx = new BreakTarget { IsLoop = false, Label = label };
        _breakTargets.Push(ctx);

        // Body labels + statements.
        var bodyAddrs = new int[caseCount];
        for (int i = 0; i < caseCount; i++)
        {
            bodyAddrs[i] = _chunk.Position;
            _chunk.PatchJumpTo(entryBodyJumps[i], bodyAddrs[i]);
            foreach (var s in stmt.Cases[i].Consequent)
            {
                CompileStatement(s);
            }
        }

        _breakTargets.Pop();

        // Exit of switch — patch break jumps and the no-default
        // no-match path.
        foreach (var addr in ctx.BreakJumps) _chunk.PatchJump(addr);
        if (defaultIndex >= 0)
        {
            _chunk.PatchJumpTo(noMatchJump, entryAddrs[defaultIndex]);
        }
        else
        {
            _chunk.PatchJump(noMatchJump);
        }
    }

    // -------------------------------------------------------------------
    // try / catch / finally
    // -------------------------------------------------------------------

    /// <summary>
    /// Dispatch to one of the three try-statement shapes. Each
    /// shape has a tailored code layout to minimize redundant
    /// finally-body duplication; see the individual methods.
    /// </summary>
    private void CompileTry(TryStatement stmt)
    {
        bool hasCatch = stmt.Handler is not null;
        bool hasFinally = stmt.Finalizer is not null;
        if (hasCatch && hasFinally)
        {
            CompileTryCatchFinally(stmt);
        }
        else if (hasCatch)
        {
            CompileTryCatch(stmt);
        }
        else
        {
            CompileTryFinally(stmt);
        }
    }

    /// <summary>
    /// Layout:
    /// <code>
    /// PushCatchHandler catch_label
    /// &lt;try body&gt;
    /// PopHandler
    /// Jump end
    /// catch_label:
    ///     PushEnv
    ///     DeclareVar e
    ///     StoreName e
    ///     Pop                    (discard value left by Store)
    ///     &lt;catch body&gt;
    ///     PopEnv
    /// end:
    /// </code>
    /// </summary>
    private void CompileTryCatch(TryStatement stmt)
    {
        var handler = stmt.Handler!;
        int catchJump = _chunk.EmitJump(OpCode.PushCatchHandler);

        CompileStatement(stmt.Block);
        _chunk.Emit(OpCode.PopHandler);
        int endJump = _chunk.EmitJump(OpCode.Jump);

        _chunk.PatchJump(catchJump);
        EmitCatchParamBind(handler.Param.Name);
        CompileStatement(handler.Body);
        _chunk.Emit(OpCode.PopEnv);
        _chunk.PatchJump(endJump);
    }

    /// <summary>
    /// Layout:
    /// <code>
    /// PushFinallyHandler finally_label
    /// &lt;try body&gt;
    /// PopHandler
    /// &lt;finally body&gt;          (normal path)
    /// Jump end
    /// finally_label:
    ///     &lt;finally body&gt;      (exception path — separate copy)
    ///     EndFinally             (re-throw if pending)
    /// end:
    /// </code>
    /// The finally body is emitted twice for simplicity. A
    /// future optimization could use a small subroutine
    /// convention if profiling shows finally-heavy code matters.
    /// </summary>
    private void CompileTryFinally(TryStatement stmt)
    {
        int handlerJump = _chunk.EmitJump(OpCode.PushFinallyHandler);

        CompileStatement(stmt.Block);
        _chunk.Emit(OpCode.PopHandler);
        CompileStatement(stmt.Finalizer!);
        int endJump = _chunk.EmitJump(OpCode.Jump);

        _chunk.PatchJump(handlerJump);
        CompileStatement(stmt.Finalizer!);
        _chunk.Emit(OpCode.EndFinally);

        _chunk.PatchJump(endJump);
    }

    /// <summary>
    /// Layout:
    /// <code>
    /// PushCatchHandler catch_label
    /// &lt;try body&gt;
    /// PopHandler
    /// &lt;finally body&gt;          (normal-after-try path)
    /// Jump end_1
    /// catch_label:
    ///     PushFinallyHandler finally_label
    ///     PushEnv; DeclareVar e; StoreName e; Pop
    ///     &lt;catch body&gt;
    ///     PopEnv
    ///     PopHandler             (the nested finally handler)
    ///     &lt;finally body&gt;      (normal-after-catch path)
    ///     Jump end_2
    /// finally_label:
    ///     &lt;finally body&gt;      (exception path — throw from catch body)
    ///     EndFinally
    /// end:
    /// </code>
    /// Three copies of the finally body — one for each exit.
    /// Installing the <see cref="OpCode.PushFinallyHandler"/>
    /// inside the catch body ensures a throw during catch still
    /// runs the finally.
    /// </summary>
    private void CompileTryCatchFinally(TryStatement stmt)
    {
        var handler = stmt.Handler!;
        int catchJump = _chunk.EmitJump(OpCode.PushCatchHandler);

        CompileStatement(stmt.Block);
        _chunk.Emit(OpCode.PopHandler);
        CompileStatement(stmt.Finalizer!);
        int endJump1 = _chunk.EmitJump(OpCode.Jump);

        _chunk.PatchJump(catchJump);
        int finallyExcJump = _chunk.EmitJump(OpCode.PushFinallyHandler);
        EmitCatchParamBind(handler.Param.Name);
        CompileStatement(handler.Body);
        _chunk.Emit(OpCode.PopEnv);
        _chunk.Emit(OpCode.PopHandler);
        CompileStatement(stmt.Finalizer!);
        int endJump2 = _chunk.EmitJump(OpCode.Jump);

        _chunk.PatchJump(finallyExcJump);
        CompileStatement(stmt.Finalizer!);
        _chunk.Emit(OpCode.EndFinally);

        _chunk.PatchJump(endJump1);
        _chunk.PatchJump(endJump2);
    }

    /// <summary>
    /// Emit the catch-parameter binding sequence. Pushes a fresh
    /// env so the parameter is block-scoped to the catch body,
    /// then declares the name in that env and stores the thrown
    /// value (pushed by the VM when the handler activated) into
    /// it. Callers must emit <see cref="OpCode.PopEnv"/> at the
    /// end of the catch body to leave the outer env.
    /// </summary>
    private void EmitCatchParamBind(string name)
    {
        _chunk.Emit(OpCode.PushEnv);
        int idx = _chunk.AddName(name);
        _chunk.EmitWithU16(OpCode.DeclareGlobal, idx);
        _chunk.EmitWithU16(OpCode.StoreGlobal, idx);
        _chunk.Emit(OpCode.Pop);
    }

    // -------------------------------------------------------------------
    // Labeled statements
    // -------------------------------------------------------------------

    /// <summary>
    /// Compile a labeled statement. If the inner statement is a
    /// loop or switch, set the pending-label slot and delegate to
    /// that statement's compiler — it will attach the label to
    /// its own <see cref="BreakTarget"/>. Otherwise, push a
    /// label-only break-target (useful for
    /// <c>foo: { break foo; ... }</c>), compile the inner body,
    /// and patch break jumps at the end.
    /// </summary>
    private void CompileLabeledStatement(LabeledStatement ls)
    {
        switch (ls.Body)
        {
            case WhileStatement:
            case DoWhileStatement:
            case ForStatement:
            case ForInStatement:
            case SwitchStatement:
                _pendingLabel = ls.Label.Name;
                CompileStatement(ls.Body);
                return;
        }

        // Non-loop labeled statement — break-only target.
        var ctx = new BreakTarget { IsLoop = false, Label = ls.Label.Name };
        _breakTargets.Push(ctx);
        CompileStatement(ls.Body);
        _breakTargets.Pop();
        foreach (var addr in ctx.BreakJumps) _chunk.PatchJump(addr);
    }

    /// <summary>
    /// Normalize an object-literal property key to its string form.
    /// The parser accepts identifiers (including reserved words as
    /// ES5 bare keys), string literals, and number literals; the
    /// compiler and VM only deal with string names, so we coerce
    /// once here.
    /// </summary>
    private static string PropertyKeyToName(Expression key) => key switch
    {
        Identifier id => id.Name,
        Literal { Kind: LiteralKind.String, Value: string s } => s,
        Literal { Kind: LiteralKind.Number, Value: double n } => JsValue.ToJsString(n),
        _ => throw new JsCompileException($"Unsupported property key: {key.GetType().Name}", key.Start),
    };

    // -------------------------------------------------------------------
    // Operator tables
    // -------------------------------------------------------------------

    private static OpCode BinaryOpCode(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => OpCode.Add,
        BinaryOperator.Subtract => OpCode.Sub,
        BinaryOperator.Multiply => OpCode.Mul,
        BinaryOperator.Divide => OpCode.Div,
        BinaryOperator.Modulo => OpCode.Mod,
        BinaryOperator.Equal => OpCode.LooseEq,
        BinaryOperator.NotEqual => OpCode.LooseNotEq,
        BinaryOperator.StrictEqual => OpCode.StrictEq,
        BinaryOperator.StrictNotEqual => OpCode.StrictNotEq,
        BinaryOperator.LessThan => OpCode.Lt,
        BinaryOperator.LessThanEqual => OpCode.Le,
        BinaryOperator.GreaterThan => OpCode.Gt,
        BinaryOperator.GreaterThanEqual => OpCode.Ge,
        BinaryOperator.BitwiseAnd => OpCode.BitAnd,
        BinaryOperator.BitwiseOr => OpCode.BitOr,
        BinaryOperator.BitwiseXor => OpCode.BitXor,
        BinaryOperator.LeftShift => OpCode.Shl,
        BinaryOperator.RightShift => OpCode.Shr,
        BinaryOperator.UnsignedRightShift => OpCode.Ushr,
        BinaryOperator.In => OpCode.In,
        BinaryOperator.InstanceOf => OpCode.Instanceof,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    private static OpCode CompoundAssignmentOpCode(AssignmentOperator op) => op switch
    {
        AssignmentOperator.AddAssign => OpCode.Add,
        AssignmentOperator.SubtractAssign => OpCode.Sub,
        AssignmentOperator.MultiplyAssign => OpCode.Mul,
        AssignmentOperator.DivideAssign => OpCode.Div,
        AssignmentOperator.ModuloAssign => OpCode.Mod,
        AssignmentOperator.LeftShiftAssign => OpCode.Shl,
        AssignmentOperator.RightShiftAssign => OpCode.Shr,
        AssignmentOperator.UnsignedRightShiftAssign => OpCode.Ushr,
        AssignmentOperator.BitwiseAndAssign => OpCode.BitAnd,
        AssignmentOperator.BitwiseOrAssign => OpCode.BitOr,
        AssignmentOperator.BitwiseXorAssign => OpCode.BitXor,
        AssignmentOperator.Assign => throw new InvalidOperationException("Not a compound op"),
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };
}

/// <summary>
/// Thrown by <see cref="JsCompiler"/> when the input AST uses a form
/// the current slice cannot compile. Carries a source offset so the
/// caller can render a location. The message identifies which slice
/// will add support.
/// </summary>
public sealed class JsCompileException : Exception
{
    public int Offset { get; }

    public JsCompileException(string message, int offset) : base(message)
    {
        Offset = offset;
    }
}
