namespace Appa.Tests;

using Appa;

/// <summary>
/// Inline string-literal coverage of the parser: AST shape for representative top-level
/// declarations, statements, and expressions.
/// </summary>
public class ParserTests
{
    [Fact]
    public void BareImportIsNotAPath()
    {
        var prog = SingleFileCompile.Parse("import Collections;");
        var import = Assert.IsType<ImportDecl>(prog.Items[0]);
        Assert.Equal("Collections", import.Name);
        Assert.False(import.IsPath);
    }

    [Fact]
    public void QuotedImportIsAPath()
    {
        var prog = SingleFileCompile.Parse("import \"shared/util.g\";");
        var import = Assert.IsType<ImportDecl>(prog.Items[0]);
        Assert.Equal("shared/util.g", import.Name);
        Assert.True(import.IsPath);
    }

    [Fact]
    public void EnvironmentParsesAsAMarker()
    {
        var prog = SingleFileCompile.Parse("@environment");
        Assert.IsType<EnvironmentDecl>(prog.Items[0]);
    }

    [Fact]
    public void FreeFuncCapturesItsSignature()
    {
        var prog = SingleFileCompile.Parse("int func Add(int a, int b) { return a + b; }");
        var func = Assert.IsType<FuncDecl>(prog.Items[0]);
        Assert.Equal("Add", func.Name);
        Assert.Equal("int", func.ReturnType?.ToSpecString());
        Assert.Equal(2, func.Params.Length);
        Assert.Equal("a", func.Params[0].Name);
        Assert.Equal("b", func.Params[1].Name);
    }

    [Fact]
    public void EntryFuncSetsIsEntry()
    {
        var prog = SingleFileCompile.Parse("entry func Main() { }");
        var func = Assert.IsType<FuncDecl>(prog.Items[0]);
        Assert.True(func.IsEntry);
    }

    [Fact]
    public void GenericFuncCollectsItsParams()
    {
        var prog = SingleFileCompile.Parse("func Identity[T](T x) { return x; }");
        var func = Assert.IsType<FuncDecl>(prog.Items[0]);
        Assert.Equal(["T"], func.GenericParams);
    }

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

    [Fact]
    public void ModuleDeclSetsIsModule()
    {
        var prog = SingleFileCompile.Parse("module Util { }");
        var cls = Assert.IsType<ClassDecl>(prog.Items[0]);
        Assert.True(cls.IsModule);
    }

    [Fact]
    public void KernelBlockGroupsItsItems()
    {
        var prog = SingleFileCompile.Parse("realm kernel { entry func Main() { } }");
        var ctx = Assert.IsType<ContextDecl>(prog.Items[0]);
        Assert.Equal(Realm.Kernel, ctx.Kind);
        Assert.Single(ctx.Items);
        Assert.IsType<FuncDecl>(ctx.Items[0]);
    }

    [Fact]
    public void UserBlockParsesItsTopology()
    {
        var prog = SingleFileCompile.Parse("""
            realm userspace {
                foreground process App {
                    thread Main {
                        entry func Run() { }
                    }
                }
            }
            """);
        var ctx = Assert.IsType<ContextDecl>(prog.Items[0]);
        Assert.Equal(Realm.User, ctx.Kind);
        var proc = Assert.IsType<ProcessDecl>(ctx.Items[0]);
        Assert.Equal("App", proc.Name);
        Assert.Equal("foreground", proc.Mode);
        Assert.Single(proc.Threads);
        Assert.Equal("Main", proc.Threads[0].Name);
        Assert.NotNull(proc.Threads[0].Entry);
    }

    [Fact]
    public void IfElseCarriesBothBranches()
    {
        var prog = SingleFileCompile.Parse("func F() { if (true) { } else { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var ifStmt = Assert.IsType<IfStmt>(block.Stmts[0]);
        Assert.NotNull(ifStmt.Else);
    }

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

    [Fact]
    public void ForInLoopBindsLoopVariable()
    {
        var prog = SingleFileCompile.Parse("func F() { for x in items { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var forIn = Assert.IsType<ForInStmt>(block.Stmts[0]);
        Assert.Equal("x", forIn.Var);
    }

    [Fact]
    public void LetStmtWithTypeAndInitializer()
    {
        var prog = SingleFileCompile.Parse("func F() { let int x = 5; }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var let = Assert.IsType<LetStmt>(block.Stmts[0]);
        Assert.Equal("int", let.Type?.ToSpecString());
        Assert.Equal("x", let.Name);
        Assert.NotNull(let.Init);
    }

    [Fact]
    public void BinExprCapturesItsOperands()
    {
        var prog = SingleFileCompile.Parse("func F() { let int x = 1 + 2; }");
        var func = (FuncDecl)prog.Items[0];
        var let = (LetStmt)((BlockBody)func.Body).Block.Stmts[0];
        var bin = Assert.IsType<BinExpr>(let.Init);
        Assert.Equal(BinOp.Add, bin.Op);
        Assert.IsType<IntLitExpr>(bin.Left);
        Assert.IsType<IntLitExpr>(bin.Right);
    }

    [Fact]
    public void TryCatchCarriesBothBlocks()
    {
        var prog = SingleFileCompile.Parse("func F() { try { } catch { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        Assert.IsType<TryCatchStmt>(block.Stmts[0]);
    }

    [Fact]
    public void DeferStmtWrapsAction()
    {
        var prog = SingleFileCompile.Parse("func F() { defer Close(); }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var defer = Assert.IsType<DeferStmt>(block.Stmts[0]);
        Assert.IsType<ExprStmt>(defer.Action);
    }

    [Fact]
    public void InterpolationAlternatesParts()
    {
        var prog = SingleFileCompile.Parse("func F() { let s = $\"count={n}\"; }");
        var func = (FuncDecl)prog.Items[0];
        var let = (LetStmt)((BlockBody)func.Body).Block.Stmts[0];
        var interp = Assert.IsType<InterpStrExpr>(let.Init);
        Assert.Equal(2, interp.Parts.Length);
        Assert.IsType<StrLitExpr>(interp.Parts[0]);
        Assert.IsType<IdentExpr>(interp.Parts[1]);
    }

    [Fact]
    public void MissingSemicolonFails()
    {
        Assert.Throws<ParseException>(() => SingleFileCompile.Parse("func F() { let int x = 5 }"));
    }

    #region Generic names

    /// <summary>
    /// A generic declaration folds its parameter list into Name so a self-reference resolves, and
    /// keeps the written name in BaseName. Stripping "_T" off the tail instead fails open, and a
    /// template that cannot identify its base is never matched to its instantiations again.
    /// </summary>
    [Theory]
    [InlineData("class List[T] { T v; }", "List_T", "List")]
    [InlineData("class Map[K, V] { K k; V v; }", "Map_K_V", "Map")]
    [InlineData("class Plain { int n; }", "Plain", "Plain")]
    public void GenericClassCarriesItsBaseName(string src, string name, string baseName)
    {
        var cd = Assert.IsType<ClassDecl>(SingleFileCompile.Parse(src).Items[0]);
        Assert.Equal(name, cd.Name);
        Assert.Equal(baseName, cd.BaseName);
    }

    [Theory]
    [InlineData("union Maybe[V] { Found(V v), Missing }", "Maybe_V", "Maybe")]
    [InlineData("union Flat { A, B }", "Flat", "Flat")]
    public void GenericUnionCarriesItsBaseName(string src, string name, string baseName)
    {
        var ud = Assert.IsType<UnionDecl>(SingleFileCompile.Parse(src).Items[0]);
        Assert.Equal(name, ud.Name);
        Assert.Equal(baseName, ud.BaseName);
    }

    /// <summary>
    /// The parser and every later pass must spell an instantiation identically. They agree because
    /// they call the same composer, not because two independent concatenations happen to match.
    /// </summary>
    [Fact]
    public void GenericNamesComposeThroughTheMangler()
    {
        var cd = Assert.IsType<ClassDecl>(SingleFileCompile.Parse("class Map[K, V] { K k; V v; }").Items[0]);
        Assert.Equal(Mangler.GenericInstance(cd.BaseName, cd.GenericParams), cd.Name);
    }

    /// <summary>
    /// A type reference is structured - base plus arguments - and its mangled spelling is derived,
    /// never parsed back. Nesting must compose the same way at every depth.
    /// </summary>
    [Fact]
    public void NestedGenericRefsMangleStructurally()
    {
        var inner = new NamedSpec("List", [new NamedSpec("int")], TextSpan.None);
        var outer = new NamedSpec("Box", [inner], TextSpan.None);
        Assert.Equal("List_int", inner.Mangled);
        Assert.Equal("Box_List_int", outer.Mangled);
        Assert.Equal(outer.Mangled, Mangler.GenericInstance("Box", [inner.Mangled]));
    }

    /// <summary>
    /// The instantiation site a generic declaration registers names the base, so the Monomorphizer
    /// never has to recover it from the mangled form.
    /// </summary>
    [Fact]
    public void GenericDeclUsesItsBaseName()
    {
        var prog = SingleFileCompile.Parse("class List[T] { T v; }");
        var use = Assert.Single(prog.GenericUses.Where(u => u.Base == "List"));
        Assert.Equal(["T"], use.Args);
    }

    #endregion
}
