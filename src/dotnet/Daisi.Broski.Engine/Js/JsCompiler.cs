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
        foreach (var stmt in program.Body)
        {
            CompileStatement(stmt);
        }
        _chunk.Emit(OpCode.Halt);
        return PopFrame();
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
                foreach (var d in vd.Declarations)
                {
                    int idx = _chunk.AddName(d.Id.Name);
                    _chunk.EmitWithU16(OpCode.DeclareGlobal, idx);
                }
                break;
            case FunctionDeclaration fd:
                {
                    // Compile the function body eagerly and emit
                    // the MakeFunction + binding write. The main
                    // compile pass will skip this node.
                    var template = CompileFunctionTemplate(
                        fd.Id.Name,
                        fd.Params,
                        fd.Body,
                        fd.End - fd.Start);
                    int templateIdx = _chunk.AddConstant(template);
                    int nameIdx = _chunk.AddName(fd.Id.Name);
                    _chunk.EmitWithU16(OpCode.DeclareGlobal, nameIdx);
                    _chunk.EmitWithU16(OpCode.MakeFunction, templateIdx);
                    _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
                    _chunk.Emit(OpCode.Pop);
                }
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
                if (f.Init is VariableDeclaration fvd)
                {
                    foreach (var d in fvd.Declarations)
                    {
                        int idx = _chunk.AddName(d.Id.Name);
                        _chunk.EmitWithU16(OpCode.DeclareGlobal, idx);
                    }
                }
                HoistInStatement(f.Body);
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
        IReadOnlyList<Identifier> paramNodes,
        BlockStatement body,
        int sourceLength)
    {
        PushFrame(isFunction: true);

        // Hoist the body — both vars AND any nested function
        // declarations inside the body.
        HoistDeclarations(body.Body);

        // Compile the body statements.
        foreach (var stmt in body.Body)
        {
            CompileStatement(stmt);
        }

        // Implicit `return undefined;` at the end of every
        // function body. If the body already returned, this is
        // unreachable bytecode — harmless.
        _chunk.Emit(OpCode.PushUndefined);
        _chunk.Emit(OpCode.Return);

        var chunk = PopFrame();
        var paramNames = new List<string>(paramNodes.Count);
        foreach (var p in paramNodes) paramNames.Add(p.Name);
        return new JsFunctionTemplate(chunk, paramNames, name, sourceLength);
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
                foreach (var s in b.Body) CompileStatement(s);
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
                throw new JsCompileException(
                    "'throw' requires the exception machinery (phase 3a slice 5)", ts.Start);

            case TryStatement tr:
                throw new JsCompileException(
                    "'try' requires the exception machinery (phase 3a slice 5)", tr.Start);

            case SwitchStatement sw:
                CompileSwitch(sw);
                return;

            case ForInStatement fi:
                CompileForIn(fi);
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
            if (d.Init is null) continue; // Already hoisted to undefined.
            CompileExpression(d.Init);
            int idx = _chunk.AddName(d.Id.Name);
            _chunk.EmitWithU16(OpCode.StoreGlobal, idx);
            _chunk.Emit(OpCode.Pop); // Statement position, discard the value.
        }
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
        CompileExpression(m.Object);
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
    }

    private void CompileNew(NewExpression ne)
    {
        CompileExpression(ne.Callee);
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
    }

    private void CompileFunctionExpression(FunctionExpression fe)
    {
        var template = CompileFunctionTemplate(
            fe.Id?.Name,
            fe.Params,
            fe.Body,
            fe.End - fe.Start);
        int idx = _chunk.AddConstant(template);
        _chunk.EmitWithU16(OpCode.MakeFunction, idx);
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
    private void CompileForIn(ForInStatement stmt)
    {
        var label = TakePendingLabel();

        // Resolve the binding name from the left-hand side.
        string? bindingName = stmt.Left switch
        {
            Identifier id => id.Name,
            VariableDeclaration vd when vd.Declarations.Count == 1
                => vd.Declarations[0].Id.Name,
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
