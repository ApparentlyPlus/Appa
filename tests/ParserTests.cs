namespace Appa.Tests;

using Appa;

/// <summary>
/// Inline string-literal coverage of the parser: AST shape for representative top-level
/// declarations, statements, and expressions.
/// </summary>
public class ParserTests
{
    [Fact]
    public void BareImportNotPath()
    {
        var prog = SingleFileCompile.Parse("import Collections;");
        var import = Assert.IsType<ImportDecl>(prog.Items[0]);
        Assert.Equal("Collections", import.Name);
        Assert.False(import.IsPath);
    }

    [Fact]
    public void QuotedImportIsPath()
    {
        var prog = SingleFileCompile.Parse("import \"shared/util.g\";");
        var import = Assert.IsType<ImportDecl>(prog.Items[0]);
        Assert.Equal("shared/util.g", import.Name);
        Assert.True(import.IsPath);
    }

    [Fact]
    public void EnvironmentIsMarker()
    {
        var prog = SingleFileCompile.Parse("@environment");
        Assert.IsType<EnvironmentDecl>(prog.Items[0]);
    }

    [Fact]
    public void FreeFuncSignature()
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
    public void GenericFuncParams()
    {
        var prog = SingleFileCompile.Parse("func Identity[T](T x) { return x; }");
        var func = Assert.IsType<FuncDecl>(prog.Items[0]);
        Assert.Equal(["T"], func.GenericParams);
    }

    [Fact]
    public void ClassDeclMembers()
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
    public void KernelBlockGroups()
    {
        var prog = SingleFileCompile.Parse("realm kernel { entry func Main() { } }");
        var ctx = Assert.IsType<ContextDecl>(prog.Items[0]);
        Assert.Equal(Realm.Kernel, ctx.Kind);
        Assert.Single(ctx.Items);
        Assert.IsType<FuncDecl>(ctx.Items[0]);
    }

    [Fact]
    public void UserBlockTopology()
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
    public void IfElseBranches()
    {
        var prog = SingleFileCompile.Parse("func F() { if (true) { } else { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var ifStmt = Assert.IsType<IfStmt>(block.Stmts[0]);
        Assert.NotNull(ifStmt.Else);
    }

    [Fact]
    public void CStyleForParts()
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
    public void ForInBindsVariable()
    {
        var prog = SingleFileCompile.Parse("func F() { for x in items { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        var forIn = Assert.IsType<ForInStmt>(block.Stmts[0]);
        Assert.Equal("x", forIn.Var);
    }

    [Fact]
    public void LetWithTypeAndInit()
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
    public void BinExprOperands()
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
    public void TryCatchBlocks()
    {
        var prog = SingleFileCompile.Parse("func F() { try { } catch { } }");
        var func = (FuncDecl)prog.Items[0];
        var block = ((BlockBody)func.Body).Block;
        Assert.IsType<TryCatchStmt>(block.Stmts[0]);
    }

    [Fact]
    public void DeferWrapsAction()
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
    public void GenericClassBaseName(string src, string name, string baseName)
    {
        var cd = Assert.IsType<ClassDecl>(SingleFileCompile.Parse(src).Items[0]);
        Assert.Equal(name, cd.Name);
        Assert.Equal(baseName, cd.BaseName);
    }

    [Theory]
    [InlineData("union Maybe[V] { Found(V v), Missing }", "Maybe_V", "Maybe")]
    [InlineData("union Flat { A, B }", "Flat", "Flat")]
    public void GenericUnionBaseName(string src, string name, string baseName)
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
    public void ManglerComposesNames()
    {
        var cd = Assert.IsType<ClassDecl>(SingleFileCompile.Parse("class Map[K, V] { K k; V v; }").Items[0]);
        Assert.Equal(Mangler.GenericInstance(cd.BaseName, cd.GenericParams), cd.Name);
    }

    /// <summary>
    /// A type reference is structured (base plus arguments) and its mangled spelling is derived,
    /// never parsed back. Nesting must compose the same way at every depth.
    /// </summary>
    [Fact]
    public void NestedRefsMangle()
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
    public void GenericDeclBaseName()
    {
        var prog = SingleFileCompile.Parse("class List[T] { T v; }");
        var use = Assert.Single(prog.GenericUses.Where(u => u.Base == "List"));
        Assert.Equal(["T"], use.Args);
    }

    #endregion

    #region Ambiguities from the LR conflict report

    private static Stmt[] Body(string src)
    {
        var func = (FuncDecl)SingleFileCompile.Parse(src).Items[0];
        return ((BlockBody)func.Body).Block.Stmts;
    }

    [Fact]
    public void AsThenStarMultiplies()
    {
        var let = (LetStmt)Body("func F() { let usize b = n as usize * 8; }")[0];
        var bin = Assert.IsType<BinExpr>(let.Init);
        Assert.Equal(BinOp.Mul, bin.Op);
        Assert.Equal("usize", Assert.IsType<CastExpr>(bin.Left).TargetType.ToSpecString());
        Assert.IsType<IntLitExpr>(bin.Right);
    }

    [Fact]
    public void AsThenStarDerefMultiplies()
    {
        var let = (LetStmt)Body("func F() { let int b = n as int * *p; }")[0];
        var bin = Assert.IsType<BinExpr>(let.Init);
        Assert.Equal(BinOp.Mul, bin.Op);
        Assert.Equal("int", Assert.IsType<CastExpr>(bin.Left).TargetType.ToSpecString());
        Assert.IsType<DerefExpr>(bin.Right);
    }

    [Theory]
    [InlineData("func F() { let q = p as int*; }")]
    [InlineData("func F() { let q = p as int**; }")]
    [InlineData("func F() { G(p as int*, 1); }")]
    [InlineData("func F() { let q = p as u8* + 1; }")]
    [InlineData("func F() { let b = p as int* == null; }")]
    public void AsKeepsPointerSuffix(string src)
    {
        var st = Body(src)[0];
        Expr e = st is LetStmt l ? l.Init! : ((CallExpr)((ExprStmt)st).E).Args[0];
        if (e is BinExpr b) e = b.Left;
        Assert.IsType<PtrSpec>(Assert.IsType<CastExpr>(e).TargetType);
    }

    [Fact]
    public void ForInNewLeavesBody()
    {
        var forIn = Assert.IsType<ForInStmt>(Body("func F() { for x in new C() { Use(x); } }")[0]);
        Assert.Empty(Assert.IsType<NewExpr>(forIn.Collection).CollectionInit);
        Assert.Single(forIn.Body.Stmts);
    }

    [Fact]
    public void ForInNewInsideParensKeepsInit()
    {
        var forIn = Assert.IsType<ForInStmt>(Body("func F() { for x in Wrap(new C() { 1, 2 }) { } }")[0]);
        var call = Assert.IsType<CallExpr>(forIn.Collection);
        Assert.Equal(2, Assert.IsType<NewExpr>(call.Args[0]).CollectionInit.Length);
    }

    [Fact]
    public void ForInBodyKeepsInit()
    {
        var forIn = Assert.IsType<ForInStmt>(Body("func F() { for x in xs { let ys = new C() { 1 }; } }")[0]);
        var let = Assert.IsType<LetStmt>(forIn.Body.Stmts[0]);
        Assert.Single(Assert.IsType<NewExpr>(let.Init).CollectionInit);
    }

    [Fact]
    public void CaseNewLeavesBody()
    {
        var sw = Assert.IsType<SwitchStmt>(Body("func F() { switch (b) { case new B() { } default { } } }")[0]);
        Assert.Empty(Assert.IsType<NewExpr>(Assert.Single(sw.Cases).Labels[0]).CollectionInit);
        Assert.NotNull(sw.Default);
    }

    [Fact]
    public void NewInitOutsideLoops()
    {
        var let = (LetStmt)Body("func F() { let xs = new C() { 1, 2 }; }")[0];
        Assert.Equal(2, Assert.IsType<NewExpr>(let.Init).CollectionInit.Length);
    }

    [Fact]
    public void ThreadEntryNameOptional()
    {
        var prog = SingleFileCompile.Parse("""
            realm userspace {
                foreground process App {
                    thread W { entry func() { } }
                }
            }
            """);
        var proc = Assert.IsType<ProcessDecl>(Assert.IsType<ContextDecl>(prog.Items[0]).Items[0]);
        Assert.Null(proc.Threads[0].Entry.ReturnType);
    }

    [Fact]
    public void ThreadEntryFuncReturnStillRejected()
    {
        var ex = Assert.Throws<ParseException>(() => SingleFileCompile.Parse("""
            realm userspace {
                foreground process App {
                    thread W { entry func(int) -> int func Run() { } }
                }
            }
            """));
        Assert.Contains("no return value", ex.Message);
    }

    #endregion
}
