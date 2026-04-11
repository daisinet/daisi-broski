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
    private readonly Chunk _chunk = new();

    // Compile-time loop context stack. Each entry records pending
    // break / continue jump operand addresses so we can patch them
    // when the loop's exit / continue targets are known.
    private readonly Stack<LoopContext> _loops = new();

    private sealed class LoopContext
    {
        public readonly List<int> BreakJumps = new();
        public readonly List<int> ContinueJumps = new();
    }

    public Chunk Compile(Program program)
    {
        HoistVars(program);
        foreach (var stmt in program.Body)
        {
            CompileStatement(stmt);
        }
        _chunk.Emit(OpCode.Halt);
        return _chunk;
    }

    // -------------------------------------------------------------------
    // var hoisting
    // -------------------------------------------------------------------

    private void HoistVars(Program program)
    {
        foreach (var stmt in program.Body)
        {
            HoistVarsInStatement(stmt);
        }
    }

    private void HoistVarsInStatement(Statement stmt)
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
            case BlockStatement b:
                foreach (var s in b.Body) HoistVarsInStatement(s);
                break;
            case IfStatement i:
                HoistVarsInStatement(i.Consequent);
                if (i.Alternate is not null) HoistVarsInStatement(i.Alternate);
                break;
            case WhileStatement w:
                HoistVarsInStatement(w.Body);
                break;
            case DoWhileStatement dw:
                HoistVarsInStatement(dw.Body);
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
                HoistVarsInStatement(f.Body);
                break;
            case LabeledStatement l:
                HoistVarsInStatement(l.Body);
                break;
            // Statements that contain no nested statements (empty,
            // expression, break, continue, debugger, return, throw,
            // variable with no nested body) are covered by the
            // VariableDeclaration case or have nothing to hoist.
        }
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
                if (bs.Label is not null)
                {
                    throw new JsCompileException(
                        "Labeled 'break' is not supported in this slice", bs.Start);
                }
                if (_loops.Count == 0)
                {
                    throw new JsCompileException("'break' outside of a loop", bs.Start);
                }
                {
                    int addr = _chunk.EmitJump(OpCode.Jump);
                    _loops.Peek().BreakJumps.Add(addr);
                }
                return;

            case ContinueStatement cs:
                if (cs.Label is not null)
                {
                    throw new JsCompileException(
                        "Labeled 'continue' is not supported in this slice", cs.Start);
                }
                if (_loops.Count == 0)
                {
                    throw new JsCompileException("'continue' outside of a loop", cs.Start);
                }
                {
                    int addr = _chunk.EmitJump(OpCode.Jump);
                    _loops.Peek().ContinueJumps.Add(addr);
                }
                return;

            case DebuggerStatement:
                // No-op — no debugger is attached.
                return;

            case FunctionDeclaration fd:
                throw new JsCompileException(
                    "Function declarations are not supported in this slice (phase 3a slice 4)",
                    fd.Start);

            case ReturnStatement rs:
                throw new JsCompileException(
                    "'return' is only valid inside a function (phase 3a slice 4)", rs.Start);

            case ThrowStatement ts:
                throw new JsCompileException(
                    "'throw' requires the exception machinery (phase 3a slice 5)", ts.Start);

            case TryStatement tr:
                throw new JsCompileException(
                    "'try' requires the exception machinery (phase 3a slice 5)", tr.Start);

            case SwitchStatement sw:
                throw new JsCompileException(
                    "'switch' is not supported in this slice (phase 3a slice 4)", sw.Start);

            case ForInStatement fi:
                throw new JsCompileException(
                    "'for..in' requires object enumeration (phase 3a slice 4)", fi.Start);

            case WithStatement w:
                throw new JsCompileException(
                    "'with' is not supported (phase 3a slice 5, if ever)", w.Start);

            case LabeledStatement l:
                throw new JsCompileException(
                    "Labeled statements are not supported in this slice", l.Start);

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
        int loopStart = _chunk.Position;
        CompileExpression(stmt.Test);
        int exitJump = _chunk.EmitJump(OpCode.JumpIfFalse);

        var ctx = new LoopContext();
        _loops.Push(ctx);
        CompileStatement(stmt.Body);
        _loops.Pop();

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
        int loopStart = _chunk.Position;

        var ctx = new LoopContext();
        _loops.Push(ctx);
        CompileStatement(stmt.Body);
        _loops.Pop();

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

        var ctx = new LoopContext();
        _loops.Push(ctx);
        CompileStatement(stmt.Body);
        _loops.Pop();

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
                // At program-level, `this` is the global object. We
                // don't yet represent a global object as a JsValue,
                // so push undefined. Will be revisited when slice 4
                // introduces proper environment records.
                _chunk.Emit(OpCode.PushUndefined);
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

            case ArrayExpression ae:
                throw new JsCompileException(
                    "Array literals are not supported in this slice (phase 3a slice 4)", ae.Start);
            case ObjectExpression oe:
                throw new JsCompileException(
                    "Object literals are not supported in this slice (phase 3a slice 4)", oe.Start);
            case MemberExpression me:
                throw new JsCompileException(
                    "Member access is not supported in this slice (phase 3a slice 4)", me.Start);
            case CallExpression ce:
                throw new JsCompileException(
                    "Function calls are not supported in this slice (phase 3a slice 4)", ce.Start);
            case NewExpression ne:
                throw new JsCompileException(
                    "'new' is not supported in this slice (phase 3a slice 4)", ne.Start);
            case FunctionExpression fe:
                throw new JsCompileException(
                    "Function expressions are not supported in this slice (phase 3a slice 4)", fe.Start);

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
            // that was never a binding. Member-access delete is slice 4.
            if (u.Argument is Identifier idDel)
            {
                int idx = _chunk.AddName(idDel.Name);
                _chunk.EmitWithU16(OpCode.DeleteGlobal, idx);
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
        if (upd.Argument is not Identifier id)
        {
            throw new JsCompileException(
                "Only identifier targets are supported for ++/-- in this slice", upd.Start);
        }
        int nameIdx = _chunk.AddName(id.Name);
        var addOrSub = upd.Operator == UpdateOperator.Increment ? OpCode.Add : OpCode.Sub;

        if (upd.Prefix)
        {
            // ++x: load, add 1 (via a const), store (store leaves new on stack).
            _chunk.EmitWithU16(OpCode.LoadGlobal, nameIdx);
            int oneIdx = _chunk.AddConstant(1.0);
            _chunk.EmitWithU16(OpCode.PushConst, oneIdx);
            _chunk.Emit(addOrSub);
            _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
        }
        else
        {
            // x++: load, UnaryPlus to coerce to Number (ES5 §11.3.1:
            // the result of postfix is the numeric version of the old
            // value, so `var s = '1'; s++;` leaves 1 not '1' in the
            // completion), dup, add 1, store, pop — leaves old Number.
            _chunk.EmitWithU16(OpCode.LoadGlobal, nameIdx);
            _chunk.Emit(OpCode.UnaryPlus);
            _chunk.Emit(OpCode.Dup);
            int oneIdx = _chunk.AddConstant(1.0);
            _chunk.EmitWithU16(OpCode.PushConst, oneIdx);
            _chunk.Emit(addOrSub);
            _chunk.EmitWithU16(OpCode.StoreGlobal, nameIdx);
            _chunk.Emit(OpCode.Pop);
        }
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
        if (a.Left is not Identifier id)
        {
            throw new JsCompileException(
                "Only identifier targets are supported for assignment in this slice", a.Start);
        }
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
        BinaryOperator.InstanceOf or BinaryOperator.In =>
            throw new JsCompileException(
                $"'{op}' requires object semantics (phase 3a slice 4)", 0),
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
