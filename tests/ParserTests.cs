namespace Appa.Tests;

using Appa;

/// <summary>
/// Inline string-literal coverage of the parser: AST shape for representative
/// top-level declarations, statements, and expressions.
/// </summary>
public class ParserTests
{
    /// <summary>
    /// A bare-name import (no quotes) parses as a non-path ImportDecl.
    /// </summary>
    [Fact]
    public void BareImportIsNotAPath()
    {
        var prog = SingleFileCompile.Parse("import Collections;");
        var import = Assert.IsType<ImportDecl>(prog.Items[0]);
        Assert.Equal("Collections", import.Name);
        Assert.False(import.IsPath);
    }

    /// <summary>
    /// A quoted import parses as a path ImportDecl, with the surrounding quotes stripped.
    /// </summary>
    [Fact]
    public void QuotedImportIsAPath()
    {
        var prog = SingleFileCompile.Parse("import \"shared/util.g\";");
        var import = Assert.IsType<ImportDecl>(prog.Items[0]);
        Assert.Equal("shared/util.g", import.Name);
        Assert.True(import.IsPath);
    }

    /// <summary>
    /// @environment parses as a marker EnvironmentDecl with no payload.
    /// </summary>
    [Fact]
    public void EnvironmentAnnotationParsesAsMarkerDecl()
    {
        var prog = SingleFileCompile.Parse("@environment");
        Assert.IsType<EnvironmentDecl>(prog.Items[0]);
    }

    /// <summary>
    /// A free function declaration captures its name, parameters, and return type.
    /// </summary>
    [Fact]
    public void FreeFuncDeclCapturesNameParamsAndReturnType()
    {
        var prog = SingleFileCompile.Parse("int func Add(int a, int b) { return a + b; }");
        var func = Assert.IsType<FuncDecl>(prog.Items[0]);
        Assert.Equal("Add", func.Name);
        Assert.Equal("int", func.ReturnType);
        Assert.Equal(2, func.Params.Length);
        Assert.Equal("a", func.Params[0].Name);
        Assert.Equal("b", func.Params[1].Name);
    }

    /// <summary>
    /// entry func marks a function as a thread entry point.
    /// </summary>
    [Fact]
    public void EntryFuncSetsIsEntry()
    {
        var prog = SingleFileCompile.Parse("entry func Main() { }");
        var func = Assert.IsType<FuncDecl>(prog.Items[0]);
        Assert.True(func.IsEntry);
    }

    /// <summary>
    /// A generic function declaration collects its type parameters between the name
    /// and the parameter list.
    /// </summary>
    [Fact]
    public void GenericFuncDeclCollectsGenericParams()
    {
        var prog = SingleFileCompile.Parse("func Identity[T](T x) { return x; }");
        var func = Assert.IsType<FuncDecl>(prog.Items[0]);
        Assert.Equal(["T"], func.GenericParams);
    }

    /// <summary>
    /// A class declaration collects its name and member declarations.
    /// </summary>
    [Fact]
    public void ClassDeclCollectsNameAndMembers()
    {
        var prog = SingleFileCompile.Parse("class Point { int X; int Y; }");
        var cls = Assert.IsType<ClassDecl>(prog.Items[0]);
        Assert.Equal("Point", cls.Name);
        Assert.False(cls.IsModule);
        Assert.Equal(2, cls.Members.Length);
        Assert.All(cls.Members, m => Assert.IsType<FieldDecl>(m));
    }

    /// <summary>
    /// A module declaration is a class with IsModule set - members are implicitly static.
    /// </summary>
    [Fact]
    public void ModuleDeclSetsIsModule()
    {
        var prog = SingleFileCompile.Parse("module Util { }");
        var cls = Assert.IsType<ClassDecl>(prog.Items[0]);
        Assert.True(cls.IsModule);
    }

    /// <summary>
    /// A kernel { } block groups its contents into a ContextDecl with Kind "kernel".
    /// </summary>
    [Fact]
    public void KernelBlockGroupsItsItems()
    {
        var prog = SingleFileCompile.Parse("kernel { entry func Main() { } }");
        var ctx = Assert.IsType<ContextDecl>(prog.Items[0]);
        Assert.Equal("kernel", ctx.Kind);
        Assert.Single(ctx.Items);
        Assert.IsType<FuncDecl>(ctx.Items[0]);
    }

    /// <summary>
    /// A user { } block with a process/thread topology parses into nested
    /// ProcessDecl/ThreadDecl nodes.
    /// </summary>
    [Fact]
    public void UserBlockParsesProcessAndThreadTopology()
    {
        var prog = SingleFileCompile.Parse("""
            user {
                foreground process App {
                    thread Main {
                        entry func Run() { }
                    }
                }
            }
            """);
        var ctx = Assert.IsType<ContextDecl>(prog.Items[0]);
        Assert.Equal("user", ctx.Kind);
        var proc = Assert.IsType<ProcessDecl>(ctx.Items[0]);
        Assert.Equal("App", proc.Name);
        Assert.Equal("foreground", proc.Mode);
        Assert.Single(proc.Threads);
        Assert.Equal("Main", proc.Threads[0].Name);
        Assert.NotNull(proc.Threads[0].Entry);
    }

    /// <summary>
    /// An if/else statement carries both branches.
    /// </summary>
    [Fact]
    public void IfElseCarriesBothBranches()
    {
        var prog = SingleFileCompile.Parse("func F() { if (true) { } else { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var ifStmt = Assert.IsType<IfStmt>(block.Stmts[0]);
        Assert.NotNull(ifStmt.Else);
    }

    /// <summary>
    /// A C-style for loop parses its init/cond/step trio into a ForStmt.
    /// </summary>
    [Fact]
    public void CStyleForLoopParsesInitCondStep()
    {
        var prog = SingleFileCompile.Parse("func F() { for (let int i = 0; i < 10; i++) { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var forStmt = Assert.IsType<ForStmt>(block.Stmts[0]);
        Assert.NotNull(forStmt.Init);
        Assert.NotNull(forStmt.Cond);
        Assert.NotNull(forStmt.Step);
    }

    /// <summary>
    /// A for-in loop over a collection parses as a ForInStmt binding the loop variable.
    /// </summary>
    [Fact]
    public void ForInLoopBindsLoopVariable()
    {
        var prog = SingleFileCompile.Parse("func F() { for x in items { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var forIn = Assert.IsType<ForInStmt>(block.Stmts[0]);
        Assert.Equal("x", forIn.Var);
    }

    /// <summary>
    /// A let statement with an explicit type and initializer parses both fields.
    /// </summary>
    [Fact]
    public void LetStmtWithTypeAndInitializer()
    {
        var prog = SingleFileCompile.Parse("func F() { let int x = 5; }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var let = Assert.IsType<LetStmt>(block.Stmts[0]);
        Assert.Equal("int", let.Type);
        Assert.Equal("x", let.Name);
        Assert.NotNull(let.Init);
    }

    /// <summary>
    /// A binary expression captures the operator and both operands.
    /// </summary>
    [Fact]
    public void BinExprCapturesOperatorAndOperands()
    {
        var prog = SingleFileCompile.Parse("func F() { let int x = 1 + 2; }");
        var func = (FuncDecl)prog.Items[0];
        var let = (LetStmt)((BlockBody)func.Body).Block.Stmts[0];
        var bin = Assert.IsType<BinExpr>(let.Init);
        Assert.Equal(BinOp.Add, bin.Op);
        Assert.IsType<IntLitExpr>(bin.Left);
        Assert.IsType<IntLitExpr>(bin.Right);
    }

    /// <summary>
    /// A try/catch statement carries both the try block and the catch block.
    /// </summary>
    [Fact]
    public void TryCatchCarriesBothBlocks()
    {
        var prog = SingleFileCompile.Parse("func F() { try { } catch { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        Assert.IsType<TryCatchStmt>(block.Stmts[0]);
    }

    /// <summary>
    /// A defer statement wraps its action statement.
    /// </summary>
    [Fact]
    public void DeferStmtWrapsAction()
    {
        var prog = SingleFileCompile.Parse("func F() { defer Close(); }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var defer = Assert.IsType<DeferStmt>(block.Stmts[0]);
        Assert.IsType<ExprStmt>(defer.Action);
    }

    /// <summary>
    /// An interpolated string expression alternates literal segments and embedded
    /// expressions in source order.
    /// </summary>
    [Fact]
    public void InterpolatedStringAlternatesLiteralAndExprParts()
    {
        var prog = SingleFileCompile.Parse("func F() { let s = $\"count={n}\"; }");
        var func = (FuncDecl)prog.Items[0];
        var let = (LetStmt)((BlockBody)func.Body).Block.Stmts[0];
        var interp = Assert.IsType<InterpStrExpr>(let.Init);
        Assert.Equal(2, interp.Parts.Length);
        Assert.IsType<StrLitExpr>(interp.Parts[0]);
        Assert.IsType<IdentExpr>(interp.Parts[1]);
    }

    /// <summary>
    /// A syntax error (missing semicolon) throws ParseException rather than
    /// producing a malformed tree.
    /// </summary>
    [Fact]
    public void MissingSemicolonThrowsParseException()
    {
        Assert.Throws<ParseException>(() => SingleFileCompile.Parse("func F() { let int x = 5 }"));
    }
}
