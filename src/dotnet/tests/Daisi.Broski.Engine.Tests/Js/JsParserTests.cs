using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsParserTests
{
    // -------- small helpers --------

    private static Program Parse(string src) => new JsParser(src).ParseProgram();

    private static T ParseSingleStatement<T>(string src) where T : Statement
    {
        var prog = Parse(src);
        Assert.Single(prog.Body);
        return Assert.IsType<T>(prog.Body[0]);
    }

    private static Expression ParseSingleExpression(string src)
    {
        var stmt = ParseSingleStatement<ExpressionStatement>(src);
        return stmt.Expression;
    }

    // -------- literals and primary expressions --------

    [Fact]
    public void Number_literal_parses()
    {
        var expr = ParseSingleExpression("42;");
        var lit = Assert.IsType<Literal>(expr);
        Assert.Equal(LiteralKind.Number, lit.Kind);
        Assert.Equal(42.0, (double)lit.Value!);
    }

    [Fact]
    public void String_literal_parses_with_decoded_value()
    {
        var expr = ParseSingleExpression("'hello\\nworld';");
        var lit = Assert.IsType<Literal>(expr);
        Assert.Equal(LiteralKind.String, lit.Kind);
        Assert.Equal("hello\nworld", lit.Value);
    }

    [Fact]
    public void Boolean_and_null_literals_parse()
    {
        var t = Assert.IsType<Literal>(ParseSingleExpression("true;"));
        Assert.Equal(LiteralKind.Boolean, t.Kind);
        Assert.Equal(true, t.Value);

        var f = Assert.IsType<Literal>(ParseSingleExpression("false;"));
        Assert.Equal(LiteralKind.Boolean, f.Kind);
        Assert.Equal(false, f.Value);

        var n = Assert.IsType<Literal>(ParseSingleExpression("null;"));
        Assert.Equal(LiteralKind.Null, n.Kind);
        Assert.Null(n.Value);
    }

    [Fact]
    public void Identifier_expression()
    {
        var id = Assert.IsType<Identifier>(ParseSingleExpression("foo;"));
        Assert.Equal("foo", id.Name);
    }

    [Fact]
    public void This_expression()
    {
        Assert.IsType<ThisExpression>(ParseSingleExpression("this;"));
    }

    [Fact]
    public void Parenthesized_expression_unwraps()
    {
        // (x + 1) should parse as a BinaryExpression, not a wrapper.
        var expr = ParseSingleExpression("(x + 1);");
        var bin = Assert.IsType<BinaryExpression>(expr);
        Assert.Equal(BinaryOperator.Add, bin.Operator);
    }

    // -------- array and object literals --------

    [Fact]
    public void Empty_array_literal()
    {
        var arr = Assert.IsType<ArrayExpression>(ParseSingleExpression("[];"));
        Assert.Empty(arr.Elements);
    }

    [Fact]
    public void Array_literal_with_elements_and_trailing_comma()
    {
        var arr = Assert.IsType<ArrayExpression>(ParseSingleExpression("[1, 2, 3,];"));
        Assert.Equal(3, arr.Elements.Count);
        Assert.All(arr.Elements, e => Assert.IsType<Literal>(e));
    }

    [Fact]
    public void Array_literal_with_holes()
    {
        var arr = Assert.IsType<ArrayExpression>(ParseSingleExpression("[1, , 3];"));
        Assert.Equal(3, arr.Elements.Count);
        Assert.NotNull(arr.Elements[0]);
        Assert.Null(arr.Elements[1]);
        Assert.NotNull(arr.Elements[2]);
    }

    [Fact]
    public void Empty_object_literal()
    {
        var obj = Assert.IsType<ObjectExpression>(ParseSingleExpression("({});"));
        Assert.Empty(obj.Properties);
    }

    [Fact]
    public void Object_literal_with_identifier_string_and_number_keys()
    {
        var obj = Assert.IsType<ObjectExpression>(ParseSingleExpression("({foo: 1, 'bar': 2, 3: 'three'});"));
        Assert.Equal(3, obj.Properties.Count);
        Assert.IsType<Identifier>(obj.Properties[0].Key);
        Assert.IsType<Literal>(obj.Properties[1].Key);
        Assert.IsType<Literal>(obj.Properties[2].Key);
    }

    [Fact]
    public void Object_literal_allows_reserved_word_keys()
    {
        var obj = Assert.IsType<ObjectExpression>(ParseSingleExpression("({if: 1, for: 2, delete: 3});"));
        Assert.Equal(3, obj.Properties.Count);
        foreach (var prop in obj.Properties)
        {
            Assert.IsType<Identifier>(prop.Key);
        }
    }

    [Fact]
    public void Object_literal_with_trailing_comma()
    {
        var obj = Assert.IsType<ObjectExpression>(ParseSingleExpression("({a: 1, b: 2,});"));
        Assert.Equal(2, obj.Properties.Count);
    }

    [Fact]
    public void Object_literal_with_getter_and_setter()
    {
        var obj = Assert.IsType<ObjectExpression>(ParseSingleExpression(
            "({get x() { return 1; }, set x(v) { this._x = v; }});"));
        Assert.Equal(2, obj.Properties.Count);
        Assert.Equal(PropertyKind.Get, obj.Properties[0].Kind);
        Assert.Equal(PropertyKind.Set, obj.Properties[1].Kind);
        Assert.IsType<FunctionExpression>(obj.Properties[0].Value);
    }

    // -------- operator precedence & associativity --------

    [Fact]
    public void Multiplication_binds_tighter_than_addition()
    {
        // a + b * c should be (a + (b * c)).
        var expr = ParseSingleExpression("a + b * c;");
        var bin = Assert.IsType<BinaryExpression>(expr);
        Assert.Equal(BinaryOperator.Add, bin.Operator);
        var right = Assert.IsType<BinaryExpression>(bin.Right);
        Assert.Equal(BinaryOperator.Multiply, right.Operator);
    }

    [Fact]
    public void Addition_is_left_associative()
    {
        // a - b - c should be ((a - b) - c).
        var expr = ParseSingleExpression("a - b - c;");
        var outer = Assert.IsType<BinaryExpression>(expr);
        Assert.Equal(BinaryOperator.Subtract, outer.Operator);
        var inner = Assert.IsType<BinaryExpression>(outer.Left);
        Assert.Equal(BinaryOperator.Subtract, inner.Operator);
    }

    [Fact]
    public void Assignment_is_right_associative()
    {
        // a = b = c should be (a = (b = c)).
        var expr = ParseSingleExpression("a = b = c;");
        var outer = Assert.IsType<AssignmentExpression>(expr);
        var inner = Assert.IsType<AssignmentExpression>(outer.Right);
        var inId = Assert.IsType<Identifier>(inner.Left);
        Assert.Equal("b", inId.Name);
    }

    [Fact]
    public void Conditional_is_right_associative()
    {
        // a ? b : c ? d : e should be a ? b : (c ? d : e).
        var expr = ParseSingleExpression("a ? b : c ? d : e;");
        var outer = Assert.IsType<ConditionalExpression>(expr);
        Assert.IsType<ConditionalExpression>(outer.Alternate);
    }

    [Fact]
    public void Logical_and_binds_tighter_than_or()
    {
        // a || b && c should be (a || (b && c)).
        var expr = ParseSingleExpression("a || b && c;");
        var outer = Assert.IsType<LogicalExpression>(expr);
        Assert.Equal(LogicalOperator.Or, outer.Operator);
        var inner = Assert.IsType<LogicalExpression>(outer.Right);
        Assert.Equal(LogicalOperator.And, inner.Operator);
    }

    [Fact]
    public void Equality_binds_looser_than_relational()
    {
        // a < b == c should be ((a < b) == c).
        var expr = ParseSingleExpression("a < b == c;");
        var outer = Assert.IsType<BinaryExpression>(expr);
        Assert.Equal(BinaryOperator.Equal, outer.Operator);
        var inner = Assert.IsType<BinaryExpression>(outer.Left);
        Assert.Equal(BinaryOperator.LessThan, inner.Operator);
    }

    [Fact]
    public void Strict_equality_parses()
    {
        var expr = ParseSingleExpression("a === b;");
        var bin = Assert.IsType<BinaryExpression>(expr);
        Assert.Equal(BinaryOperator.StrictEqual, bin.Operator);
    }

    [Fact]
    public void Instanceof_and_in_are_relational()
    {
        var a = ParseSingleExpression("x instanceof Array;");
        Assert.Equal(BinaryOperator.InstanceOf, Assert.IsType<BinaryExpression>(a).Operator);

        var b = ParseSingleExpression("'k' in obj;");
        Assert.Equal(BinaryOperator.In, Assert.IsType<BinaryExpression>(b).Operator);
    }

    [Fact]
    public void Unary_operators_parse()
    {
        Assert.Equal(UnaryOperator.LogicalNot, Assert.IsType<UnaryExpression>(ParseSingleExpression("!x;")).Operator);
        Assert.Equal(UnaryOperator.BitwiseNot, Assert.IsType<UnaryExpression>(ParseSingleExpression("~x;")).Operator);
        Assert.Equal(UnaryOperator.Minus, Assert.IsType<UnaryExpression>(ParseSingleExpression("-x;")).Operator);
        Assert.Equal(UnaryOperator.Plus, Assert.IsType<UnaryExpression>(ParseSingleExpression("+x;")).Operator);
        Assert.Equal(UnaryOperator.TypeOf, Assert.IsType<UnaryExpression>(ParseSingleExpression("typeof x;")).Operator);
        Assert.Equal(UnaryOperator.Void, Assert.IsType<UnaryExpression>(ParseSingleExpression("void x;")).Operator);
        Assert.Equal(UnaryOperator.Delete, Assert.IsType<UnaryExpression>(ParseSingleExpression("delete x.y;")).Operator);
    }

    [Fact]
    public void Prefix_and_postfix_update_operators()
    {
        var pre = Assert.IsType<UpdateExpression>(ParseSingleExpression("++x;"));
        Assert.True(pre.Prefix);
        Assert.Equal(UpdateOperator.Increment, pre.Operator);

        var post = Assert.IsType<UpdateExpression>(ParseSingleExpression("x--;"));
        Assert.False(post.Prefix);
        Assert.Equal(UpdateOperator.Decrement, post.Operator);
    }

    [Fact]
    public void Assignment_to_literal_is_a_syntax_error()
    {
        Assert.Throws<JsParseException>(() => Parse("1 = 2;"));
    }

    [Fact]
    public void Compound_assignment_operators()
    {
        var e = Assert.IsType<AssignmentExpression>(ParseSingleExpression("x += 1;"));
        Assert.Equal(AssignmentOperator.AddAssign, e.Operator);

        var e2 = Assert.IsType<AssignmentExpression>(ParseSingleExpression("x >>>= y;"));
        Assert.Equal(AssignmentOperator.UnsignedRightShiftAssign, e2.Operator);
    }

    [Fact]
    public void Comma_operator_creates_sequence_expression()
    {
        // Must be in a position where comma isn't a list separator.
        var seq = Assert.IsType<SequenceExpression>(ParseSingleExpression("(a, b, c);"));
        Assert.Equal(3, seq.Expressions.Count);
    }

    // -------- member access, calls, new --------

    [Fact]
    public void Dot_member_access_chains()
    {
        var expr = ParseSingleExpression("a.b.c;");
        var outer = Assert.IsType<MemberExpression>(expr);
        Assert.False(outer.Computed);
        Assert.Equal("c", Assert.IsType<Identifier>(outer.Property).Name);
        var inner = Assert.IsType<MemberExpression>(outer.Object);
        Assert.Equal("b", Assert.IsType<Identifier>(inner.Property).Name);
    }

    [Fact]
    public void Computed_member_access_parses()
    {
        var expr = ParseSingleExpression("a[b + 1];");
        var m = Assert.IsType<MemberExpression>(expr);
        Assert.True(m.Computed);
        Assert.IsType<BinaryExpression>(m.Property);
    }

    [Fact]
    public void Dot_access_allows_keyword_as_property_name()
    {
        var expr = ParseSingleExpression("obj.if;");
        var m = Assert.IsType<MemberExpression>(expr);
        Assert.Equal("if", Assert.IsType<Identifier>(m.Property).Name);
    }

    [Fact]
    public void Call_expression_with_arguments()
    {
        var expr = ParseSingleExpression("foo(1, 2, 3);");
        var call = Assert.IsType<CallExpression>(expr);
        Assert.Equal(3, call.Arguments.Count);
        Assert.Equal("foo", Assert.IsType<Identifier>(call.Callee).Name);
    }

    [Fact]
    public void Call_on_member_chain()
    {
        var expr = ParseSingleExpression("a.b.c(1);");
        var call = Assert.IsType<CallExpression>(expr);
        Assert.IsType<MemberExpression>(call.Callee);
    }

    [Fact]
    public void New_expression_without_arguments()
    {
        var expr = ParseSingleExpression("new Foo;");
        var n = Assert.IsType<NewExpression>(expr);
        Assert.Empty(n.Arguments);
    }

    [Fact]
    public void New_expression_with_arguments()
    {
        var expr = ParseSingleExpression("new Foo(1, 2);");
        var n = Assert.IsType<NewExpression>(expr);
        Assert.Equal(2, n.Arguments.Count);
    }

    [Fact]
    public void New_with_member_access_callee()
    {
        var expr = ParseSingleExpression("new a.b.C(x);");
        var n = Assert.IsType<NewExpression>(expr);
        Assert.IsType<MemberExpression>(n.Callee);
    }

    // -------- variable declarations --------

    [Fact]
    public void Var_declaration_single()
    {
        var stmt = ParseSingleStatement<VariableDeclaration>("var x = 1;");
        Assert.Equal(VariableDeclarationKind.Var, stmt.Kind);
        Assert.Single(stmt.Declarations);
        Assert.Equal("x", Assert.IsType<Identifier>(stmt.Declarations[0].Id).Name);
        Assert.NotNull(stmt.Declarations[0].Init);
    }

    [Fact]
    public void Var_declaration_multiple_with_and_without_init()
    {
        var stmt = ParseSingleStatement<VariableDeclaration>("var a, b = 1, c;");
        Assert.Equal(3, stmt.Declarations.Count);
        Assert.Null(stmt.Declarations[0].Init);
        Assert.NotNull(stmt.Declarations[1].Init);
        Assert.Null(stmt.Declarations[2].Init);
    }

    [Fact]
    public void Let_and_const_are_accepted()
    {
        // Treated as Let/Const kinds now; block-scoping semantics come later.
        var l = ParseSingleStatement<VariableDeclaration>("let x = 1;");
        Assert.Equal(VariableDeclarationKind.Let, l.Kind);

        var c = ParseSingleStatement<VariableDeclaration>("const x = 1;");
        Assert.Equal(VariableDeclarationKind.Const, c.Kind);
    }

    // -------- control flow --------

    [Fact]
    public void If_statement_without_else()
    {
        var stmt = ParseSingleStatement<IfStatement>("if (x) y();");
        Assert.IsType<ExpressionStatement>(stmt.Consequent);
        Assert.Null(stmt.Alternate);
    }

    [Fact]
    public void If_else_statement()
    {
        var stmt = ParseSingleStatement<IfStatement>("if (x) { a(); } else { b(); }");
        Assert.IsType<BlockStatement>(stmt.Consequent);
        Assert.IsType<BlockStatement>(stmt.Alternate);
    }

    [Fact]
    public void Dangling_else_binds_to_nearest_if()
    {
        var outer = ParseSingleStatement<IfStatement>("if (a) if (b) x(); else y();");
        var inner = Assert.IsType<IfStatement>(outer.Consequent);
        Assert.NotNull(inner.Alternate);
        Assert.Null(outer.Alternate);
    }

    [Fact]
    public void While_statement()
    {
        var stmt = ParseSingleStatement<WhileStatement>("while (x < 10) x++;");
        Assert.IsType<BinaryExpression>(stmt.Test);
        Assert.IsType<ExpressionStatement>(stmt.Body);
    }

    [Fact]
    public void Do_while_statement()
    {
        var stmt = ParseSingleStatement<DoWhileStatement>("do { x++; } while (x < 10);");
        Assert.IsType<BlockStatement>(stmt.Body);
    }

    [Fact]
    public void For_loop_c_style()
    {
        var stmt = ParseSingleStatement<ForStatement>("for (var i = 0; i < 10; i++) foo();");
        Assert.IsType<VariableDeclaration>(stmt.Init);
        Assert.IsType<BinaryExpression>(stmt.Test);
        Assert.IsType<UpdateExpression>(stmt.Update);
    }

    [Fact]
    public void For_loop_empty_header()
    {
        var stmt = ParseSingleStatement<ForStatement>("for (;;) break;");
        Assert.Null(stmt.Init);
        Assert.Null(stmt.Test);
        Assert.Null(stmt.Update);
    }

    [Fact]
    public void For_in_loop_with_var()
    {
        var stmt = ParseSingleStatement<ForInStatement>("for (var k in obj) foo(k);");
        var decl = Assert.IsType<VariableDeclaration>(stmt.Left);
        Assert.Single(decl.Declarations);
        Assert.Equal("k", Assert.IsType<Identifier>(decl.Declarations[0].Id).Name);
    }

    [Fact]
    public void For_in_loop_with_plain_identifier()
    {
        var stmt = ParseSingleStatement<ForInStatement>("for (k in obj) foo(k);");
        Assert.IsType<Identifier>(stmt.Left);
    }

    [Fact]
    public void Break_and_continue_without_labels()
    {
        var loop = ParseSingleStatement<WhileStatement>("while (true) { break; }");
        var body = Assert.IsType<BlockStatement>(loop.Body);
        var brk = Assert.IsType<BreakStatement>(body.Body[0]);
        Assert.Null(brk.Label);
    }

    [Fact]
    public void Labeled_break_statement()
    {
        var labeled = ParseSingleStatement<LabeledStatement>("outer: while (true) { break outer; }");
        Assert.Equal("outer", labeled.Label.Name);
        var whileStmt = Assert.IsType<WhileStatement>(labeled.Body);
        var block = Assert.IsType<BlockStatement>(whileStmt.Body);
        var brk = Assert.IsType<BreakStatement>(block.Body[0]);
        Assert.Equal("outer", brk.Label!.Name);
    }

    [Fact]
    public void Switch_with_cases_and_default()
    {
        var sw = ParseSingleStatement<SwitchStatement>(
            "switch (x) { case 1: a(); break; case 2: b(); break; default: c(); }");
        Assert.Equal(3, sw.Cases.Count);
        Assert.NotNull(sw.Cases[0].Test);
        Assert.NotNull(sw.Cases[1].Test);
        Assert.Null(sw.Cases[2].Test);
    }

    [Fact]
    public void Switch_case_fallthrough_keeps_statements_with_preceding_case()
    {
        var sw = ParseSingleStatement<SwitchStatement>("switch (x) { case 1: case 2: a(); break; }");
        Assert.Equal(2, sw.Cases.Count);
        Assert.Empty(sw.Cases[0].Consequent);
        Assert.Equal(2, sw.Cases[1].Consequent.Count);
    }

    // -------- functions --------

    [Fact]
    public void Function_declaration_parses()
    {
        var fn = ParseSingleStatement<FunctionDeclaration>("function foo(a, b) { return a + b; }");
        Assert.Equal("foo", fn.Id.Name);
        Assert.Equal(2, fn.Params.Count);
        Assert.Single(fn.Body.Body);
    }

    [Fact]
    public void Anonymous_function_expression()
    {
        var expr = ParseSingleExpression("(function () { return 1; });");
        var fn = Assert.IsType<FunctionExpression>(expr);
        Assert.Null(fn.Id);
    }

    [Fact]
    public void Named_function_expression()
    {
        var expr = ParseSingleExpression("(function fact(n) { return n; });");
        var fn = Assert.IsType<FunctionExpression>(expr);
        Assert.Equal("fact", fn.Id!.Name);
    }

    [Fact]
    public void Nested_function_declaration_inside_body()
    {
        var outer = ParseSingleStatement<FunctionDeclaration>(
            "function outer() { function inner() { return 1; } return inner(); }");
        Assert.Equal(2, outer.Body.Body.Count);
        Assert.IsType<FunctionDeclaration>(outer.Body.Body[0]);
    }

    // -------- try / catch / throw --------

    [Fact]
    public void Try_catch_finally()
    {
        var stmt = ParseSingleStatement<TryStatement>(
            "try { a(); } catch (e) { b(e); } finally { c(); }");
        Assert.NotNull(stmt.Handler);
        Assert.NotNull(stmt.Finalizer);
        Assert.Equal("e", stmt.Handler!.Param.Name);
    }

    [Fact]
    public void Try_finally_only()
    {
        var stmt = ParseSingleStatement<TryStatement>("try { a(); } finally { b(); }");
        Assert.Null(stmt.Handler);
        Assert.NotNull(stmt.Finalizer);
    }

    [Fact]
    public void Throw_statement()
    {
        var stmt = ParseSingleStatement<ThrowStatement>("throw new Error('bad');");
        Assert.IsType<NewExpression>(stmt.Argument);
    }

    [Fact]
    public void Throw_with_newline_is_a_syntax_error()
    {
        // Restricted production: throw + LineTerminator is illegal.
        Assert.Throws<JsParseException>(() => Parse("throw\nnew Error();"));
    }

    // -------- ASI (automatic semicolon insertion) --------

    [Fact]
    public void ASI_at_end_of_program()
    {
        // No trailing semicolon: still parses.
        var prog = Parse("var x = 1");
        Assert.Single(prog.Body);
        Assert.IsType<VariableDeclaration>(prog.Body[0]);
    }

    [Fact]
    public void ASI_before_close_brace()
    {
        var fn = ParseSingleStatement<FunctionDeclaration>("function f() { return 1 }");
        var ret = Assert.IsType<ReturnStatement>(fn.Body.Body[0]);
        Assert.NotNull(ret.Argument);
    }

    [Fact]
    public void ASI_across_line_break()
    {
        var prog = Parse("var a = 1\nvar b = 2");
        Assert.Equal(2, prog.Body.Count);
    }

    [Fact]
    public void Return_with_newline_returns_undefined()
    {
        // `return\nfoo` is parsed as `return; foo;` per ASI rules.
        var fn = ParseSingleStatement<FunctionDeclaration>("function f() { return\nfoo; }");
        Assert.Equal(2, fn.Body.Body.Count);
        var ret = Assert.IsType<ReturnStatement>(fn.Body.Body[0]);
        Assert.Null(ret.Argument);
    }

    [Fact]
    public void Postfix_plus_plus_with_newline_does_not_apply()
    {
        // `a\n++b` should parse as `a; ++b;`, not `a++ ; b`.
        var prog = Parse("a\n++b");
        Assert.Equal(2, prog.Body.Count);
        var first = Assert.IsType<ExpressionStatement>(prog.Body[0]);
        Assert.IsType<Identifier>(first.Expression);
        var second = Assert.IsType<ExpressionStatement>(prog.Body[1]);
        var upd = Assert.IsType<UpdateExpression>(second.Expression);
        Assert.True(upd.Prefix);
    }

    // -------- with, debugger, empty --------

    [Fact]
    public void With_statement()
    {
        var stmt = ParseSingleStatement<WithStatement>("with (obj) foo();");
        Assert.IsType<Identifier>(stmt.Object);
    }

    [Fact]
    public void Debugger_statement()
    {
        ParseSingleStatement<DebuggerStatement>("debugger;");
    }

    [Fact]
    public void Empty_statement()
    {
        ParseSingleStatement<EmptyStatement>(";");
    }

    // -------- error reporting --------

    [Fact]
    public void Unclosed_paren_throws_with_offset()
    {
        var ex = Assert.Throws<JsParseException>(() => Parse("foo(1, 2"));
        Assert.True(ex.Offset >= 0);
    }

    [Fact]
    public void Unexpected_token_throws()
    {
        Assert.Throws<JsParseException>(() => Parse("var = 1;"));
    }
}
