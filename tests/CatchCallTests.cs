namespace Appa.Tests;

using Appa;

/// <summary>
/// Coverage for `f() catch { ... assign v; }` - handling a throwing call in place so its value
/// stays in the enclosing scope. The emitted-C assertions pin the shape the ARC pass produces,
/// because that shape is the feature a try block would not give.
/// </summary>
public class CatchCallTests
{
    private const string Throwing = "throws int func P(int x) { if (x < 0) { throw; } return x; }\n";

    private static void AssertClean(string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
    }

    private static void AssertError(string code, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.True(diag.HasErrors, $"expected {code} but no errors were produced");
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == code);
    }

    private static string EmitMain(string src)
    {
        var files = SingleFileCompile.Emit(src);
        Assert.NotEmpty(files);
        return string.Join("\n", files.Select(f => f.Content));
    }

    #region Accepted forms

    [Fact]
    public void SatisfiesThrowsRule()
    {
        AssertClean(Throwing +
            "realm kernel { entry func Main() { let int a = P(1) catch { assign 0; }; } }");
    }

    /// <summary>
    /// The reason the construct exists: the declaration outlives the handler, so later statements
    /// in the same scope can still see it. Inside a try block this variable would be unreachable.
    /// </summary>
    [Fact]
    public void DeclStaysOuter()
    {
        AssertClean(Throwing +
            """
            realm kernel { entry func Main() {
                let int a = P(1) catch { assign 0; };
                let int b = a + 1;
                let int c = a + b;
            } }
            """);
    }

    /// <summary>
    /// A handler may bail out instead of supplying a value, as long as every path leaves. Inside a
    /// throws function, `throw` propagates the failure onward.
    /// </summary>
    [Fact]
    public void MayGiveUpByThrowing()
    {
        AssertClean(Throwing +
            """
            throws int func Chain(int x) {
                let int v = P(x) catch { throw; };
                return v + 1;
            }
            realm kernel { entry func Main() { try { let int q = Chain(1); } catch { } } }
            """);
    }

    [Fact]
    public void MayGiveUpByReturning()
    {
        AssertClean(Throwing +
            """
            int func Safe(int x) {
                let int v = P(x) catch { return -1; };
                return v;
            }
            realm kernel { entry func Main() { let int z = Safe(3); } }
            """);
    }

    [Fact]
    public void EveryPathAssigns()
    {
        AssertClean(Throwing +
            """
            realm kernel { entry func Main() {
                let int a = P(1) catch {
                    if (true) { assign 1; } else { assign 2; }
                };
            } }
            """);
    }

    [Fact]
    public void HandlersNest()
    {
        AssertClean(Throwing +
            """
            realm kernel { entry func Main() {
                let int a = P(-1) catch {
                    let int inner = P(-1) catch { assign 7; };
                    assign inner * 2;
                };
            } }
            """);
    }

    [Fact]
    public void StatementNeedsNoAssign()
    {
        AssertClean(Throwing +
            "realm kernel { entry func Main() { P(-1) catch { let int logged = 1; }; } }");
    }

    [Fact]
    public void AssignWidens()
    {
        AssertClean(Throwing +
            "realm kernel { entry func Main() { let int64 a = P(1) catch { assign 0; }; } }");
    }

    #endregion

    #region Rejected forms

    [Fact]
    public void FallthroughRejected()
    {
        AssertError(Codes.CatchHandlerNoAssign, Throwing +
            "realm kernel { entry func Main() { let int a = P(1) catch { let int z = 0; }; } }");
    }

    [Fact]
    public void MissingPathRejected()
    {
        AssertError(Codes.CatchHandlerNoAssign, Throwing +
            """
            realm kernel { entry func Main() {
                let int a = P(1) catch { if (true) { assign 1; } };
            } }
            """);
    }

    [Fact]
    public void AssignOutsideRejected()
    {
        AssertError(Codes.AssignOutsideCatch,
            "realm kernel { entry func Main() { assign 5; } }");
    }

    [Fact]
    public void AssignAsStatementRejected()
    {
        AssertError(Codes.AssignOutsideCatch, Throwing +
            "realm kernel { entry func Main() { P(1) catch { assign 3; }; } }");
    }

    [Fact]
    public void NonThrowingCallRejected()
    {
        AssertError(Codes.ThrowsOutsideTry,
            """
            int func Plain(int x) { return x; }
            realm kernel { entry func Main() { let int a = Plain(1) catch { assign 0; }; } }
            """);
    }

    [Fact]
    public void NonCallRejected()
    {
        AssertError(Codes.Syntax,
            "realm kernel { entry func Main() { let int a = 5 catch { assign 0; }; } }");
    }

    [Fact]
    public void WrongTypeAssignRejected()
    {
        AssertError(Codes.TypeMismatch, Throwing +
            "realm kernel { entry func Main() { let int a = P(1) catch { assign \"nope\"; }; } }");
    }

    [Fact]
    public void ArgumentsNotCovered()
    {
        AssertError(Codes.ThrowsOutsideTry, Throwing +
            """
            throws int func Q(int x) { if (x < 0) { throw; } return x; }
            realm kernel { entry func Main() { let int a = Q(P(1)) catch { assign 0; }; } }
            """);
    }

    /// <summary>
    /// A field initializer is spliced into the allocator, which has nowhere to put a failure
    /// branch. A bare throwing call is already rejected there, but a `catch` satisfies that check
    /// by design - so this position needs its own guard.
    /// </summary>
    [Fact]
    public void FieldInitRejected()
    {
        AssertError(Codes.ThrowsOutsideTry, Throwing +
            "class B { public int v = P(1) catch { assign 0; }; }\n" +
            "realm kernel { entry func Main() { let B b = new B(); } }");
    }

    [Theory]
    [InlineData("class B { int v; func _init(int x) { self.v = x; } }\nrealm kernel { entry func Main() { let B b = new B(P(1) catch { assign 0; }); } }")]
    [InlineData("class L { public void func Add(int x) { } }\nrealm kernel { entry func Main() { let L l = new L { P(1) catch { assign 0; } }; } }")]
    public void NestedCatchRejected(string body)
    {
        AssertError(Codes.ThrowsOutsideTry, Throwing + body);
    }

    /// <summary>
    /// A `break` inside a loop *within* the handler exits that loop, not the handler, so it does
    /// not count as leaving. Without this the analysis would accept a handler that falls through.
    /// </summary>
    [Fact]
    public void BreakStaysInHandlerLoop()
    {
        AssertError(Codes.CatchHandlerNoAssign, Throwing +
            "realm kernel { entry func Main() { let int v = P(1) catch { while (true) { break; } }; } }");
    }

    /// <summary>
    /// The construct is a statement like any other, so it works unchanged inside a loop body, an
    /// unsafe block, a nested block, and even inside a try - where the handler wins and the
    /// enclosing catch is never reached.
    /// </summary>
    [Theory]
    [InlineData("for (let int i = 0; i < 2; i = i + 1) { let int v = P(i) catch { assign 9; }; }")]
    [InlineData("unsafe { let int v = P(1) catch { assign 9; }; }")]
    [InlineData("{ let int v = P(1) catch { assign 9; }; }")]
    [InlineData("try { let int v = P(1) catch { assign 9; }; } catch { }")]
    public void WorksEverywhere(string body)
    {
        AssertClean(Throwing + "realm kernel { entry func Main() { " + body + " } }");
    }

    /// <summary>
    /// A handler is an ordinary block inside an expression, and instantiation substitutes the AST
    /// node kind by node kind. Without an explicit case it falls through the default and a type
    /// parameter inside survives unreplaced, failing as "unknown type 'T'".
    /// </summary>
    [Fact]
    public void TypeParamsSubstitute()
    {
        AssertClean(Throwing +
            """
            T func Wrap[T](T fb) {
                let int a = P(-1) catch { let T shadow = fb; assign 0; };
                return fb;
            }
            realm kernel { entry func Main() { let int w = Wrap(7); } }
            """);
    }

    [Fact]
    public void TypeParamsSubstituteInMethod()
    {
        AssertClean(Throwing +
            """
            class Holder[T] {
                T v;
                func _init(T x) { self.v = x; }
                public T func Pick() {
                    let int a = P(-1) catch { let T inner = self.v; assign 0; };
                    return self.v;
                }
            }
            realm kernel { entry func Main() { let Holder[int] h = new Holder[int](3); let int p = h.Pick(); } }
            """);
    }

    [Fact]
    public void WorksInMembers()
    {
        AssertClean(Throwing +
            """
            class Ops {
                public int func M() { let int v = P(-1) catch { assign -5; }; return v; }
                public operator int func +(Ops o) { let int v = P(-1) catch { assign 42; }; return v; }
            }
            realm kernel { entry func Main() { let Ops o = new Ops(); let int m = o.M(); let int s = o + o; } }
            """);
    }

    #endregion

    #region Lowered shape

    /// <summary>
    /// Returns the fully-lowered body of the kernel entry function. SingleFileCompile has no
    /// environment, so it emits no translation unit to string-match against - but the module it
    /// returns has already been through ARC lowering, which is the pass under test here.
    /// </summary>
    private static List<IrStmt> LoweredMain(string src)
    {
        var (diag, module) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
        Assert.NotNull(module);
        var entry = module.FreeFunctions.Single(f => f.IsEntry);
        Assert.NotNull(entry.Body);
        return entry.Body.Stmts;
    }

    /// <summary>
    /// The structural heart of the feature: the variable is declared in the enclosing block, ahead
    /// of the Result temp, and a two-armed branch stores into it. A try block would have put that
    /// declaration inside its own scope, which is what this construct exists to avoid.
    /// </summary>
    [Fact]
    public void LowersToDeclThenBranch()
    {
        var body = LoweredMain(Throwing +
            "realm kernel { entry func Main() { let int a = P(1) catch { assign 0; }; let int b = a; } }");

        var decl = Assert.IsType<IrDeclVar>(body[0]);
        Assert.Equal("a", decl.Name);
        Assert.Null(decl.Init);

        var res = Assert.IsType<IrDeclVar>(body[1]);
        Assert.Equal("__res_a", res.Name);
        Assert.IsType<IrResultType>(res.Type);

        var branch = Assert.IsType<IrIf>(body[2]);
        Assert.NotNull(branch.Else);

        var ok = Assert.IsType<IrAssign>(branch.Else!.Stmts.Single());
        Assert.Equal("a", Assert.IsType<IrVar>(ok.Target).Name);
        Assert.Equal("value", Assert.IsType<IrFieldLoad>(ok.Value).Field);
        Assert.Equal("b", Assert.IsType<IrDeclVar>(body[3]).Name);
    }

    [Fact]
    public void AssignStoresIntoDecl()
    {
        var body = LoweredMain(Throwing +
            "realm kernel { entry func Main() { let int a = P(1) catch { assign 9; }; } }");

        var branch = Assert.IsType<IrIf>(body[2]);
        var store = Assert.IsType<IrAssign>(branch.Then.Stmts.Single());
        Assert.Equal("a", Assert.IsType<IrVar>(store.Target).Name);
        Assert.Equal(9, Assert.IsType<IrLitInt>(store.Value).Value);
    }

    /// <summary>
    /// A managed target is declared with no initializer so the emitter NULL-initializes it. That is
    /// what makes the give-up path safe: a handler leaving through `throw` never assigns, but the
    /// variable is already an owner by then, and releasing null is a no-op in the runtime.
    /// </summary>
    [Fact]
    public void ManagedTargetUninitialized()
    {
        var body = LoweredMain(
            """
            class Box { int v; }
            throws Box func Make(int x) { if (x < 0) { throw; } return new Box(); }
            realm kernel { entry func Main() { let Box b = Make(1) catch { assign new Box(); }; } }
            """);

        var decl = Assert.IsType<IrDeclVar>(body[0]);
        Assert.Equal("b", decl.Name);
        Assert.Null(decl.Init);
        Assert.IsType<IrClassRef>(decl.Type);
    }

    /// <summary>
    /// In statement position the call's +1 reference is owned by nobody, so the success arm has to
    /// release it. The failure arm must not: the Result's value was never set on that path.
    /// </summary>
    [Fact]
    public void DiscardedResultReleases()
    {
        var body = LoweredMain(
            """
            class Box { int v; }
            throws Box func Make(int x) { if (x < 0) { throw; } return new Box(); }
            realm kernel { entry func Main() { Make(1) catch { let int logged = 1; }; } }
            """);

        var branch = Assert.IsType<IrIf>(body[1]);
        Assert.NotNull(branch.Else);

        var release = Assert.IsType<IrExprStmt>(branch.Else!.Stmts.Single());
        var call = Assert.IsType<IrStaticCall>(release.Expr);
        Assert.Equal("value", Assert.IsType<IrFieldLoad>(call.Args.Single()).Field);

        // The failure arm holds the handler, and releases nothing.
        Assert.DoesNotContain(branch.Then.Stmts, st => st is IrExprStmt { Expr: IrStaticCall });
    }

    #endregion

    #region A throwing call that produces nothing

    /// <summary>
    /// 'throws' with no return type says the same thing 'throws void' does: the call can fail, and
    /// on success it hands back nothing.
    /// </summary>
    [Theory]
    [InlineData("throws func F() { return; }")]
    [InlineData("throws void func F() { return; }")]
    [InlineData("throws func F() { throw; }")]
    [InlineData("throws void func F() { throw; }")]
    public void ValuelessThrowsSpellingsAgree(string decl)
    {
        AssertClean(decl + " realm kernel { entry func Main() { try { F(); } catch { } } }");
    }

    /// <summary>
    /// Its handler is pure control flow, and needs no 'assign' to be complete.
    /// </summary>
    [Fact]
    public void ValuelessHandlerNeedsNoAssign()
    {
        AssertClean("""
            throws func F() { throw; }
            void func Recover() { }
            realm kernel { entry func Main() { F() catch { Recover(); }; } }
            """);
    }

    /// <summary>
    /// And an 'assign' in one is told what is actually wrong rather
    /// than being measured against 'void' and reported as a type mismatch.
    /// </summary>
    [Fact]
    public void AssignInValuelessHandlerExplained()
    {
        var (diag, _) = SingleFileCompile.Check(
            "throws func F() { throw; } realm kernel { entry func Main() { F() catch { assign 0; }; } }");

        var d = Assert.Single(diag.All, x => x.Severity == Severity.Error);
        Assert.Equal(Codes.AssignOutsideCatch, d.Code);
        Assert.Contains("produces no value", d.Message);
    }

    /// <summary>
    /// The message about a discarded result still belongs to a call that has one.
    /// </summary>
    [Fact]
    public void DiscardedValueStillReported()
    {
        var (diag, _) = SingleFileCompile.Check(
            Throwing + "realm kernel { entry func Main() { P(1) catch { assign 0; }; } }");

        var d = Assert.Single(diag.All, x => x.Severity == Severity.Error);
        Assert.Equal(Codes.AssignOutsideCatch, d.Code);
        Assert.Contains("result is discarded", d.Message);
    }

    #endregion
}
