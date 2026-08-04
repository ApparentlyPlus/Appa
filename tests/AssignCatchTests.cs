namespace Appa.Tests;

using Appa;

/// <summary>
/// Coverage for a throwing call in assignment position - 'x = f() catch { assign v; }', and the
/// plain 'x = f()' that propagates or sits in a try. All three were rejected outright before, so
/// the documented workaround for the scope problem catch exists to solve did not itself compile.
/// The ARC cases run, because every way they break still produces valid C.
/// </summary>
public class AssignCatchTests
{
    private const string Throwing = "throws int func R(int x) { if (x < 0) { throw; } return x; }\n";

    private static void AssertClean(string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
    }

    private static Diagnostic AssertOne(string code, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        var hits = diag.All.Where(d => d.Code == code).ToList();
        Assert.True(hits.Count == 1, $"expected one {code}, got {hits.Count}: " +
            string.Join("; ", diag.All.Select(d => $"{d.Code} {d.Message}")));
        return hits[0];
    }

    #region Accepted forms

    [Theory]
    [InlineData("let int x = 0; x = R(-1) catch { assign 42; }; let int y = x;")]
    [InlineData("let [2]int a = [0,0]; a[1] = R(-1) catch { assign 6; }; let int y = a[1];")]
    [InlineData("let int x = 0; while (true) { x = R(-1) catch { break; }; } let int y = x;")]
    [InlineData("let int x = 0; for (let int i = 0; i < 2; i = i + 1) { x = R(-1) catch { continue; }; }")]
    public void HandlerOnAnAssignmentIsAccepted(string body) =>
        AssertClean(Throwing + $"realm kernel {{ entry func Main() {{ {body} }} }}");

    [Fact]
    public void HandlerOnAFieldAssignmentIsAccepted() =>
        AssertClean(Throwing + """
            realm kernel {
                class C { public int n; }
                entry func Main() { let C c = new C(); c.n = R(-1) catch { assign 8; }; }
            }
            """);

    /// <summary>
    /// The two forms with no handler: propagating out of a throws function, and inside a try. The
    /// second is what the book offers as the way to keep the value in the enclosing scope.
    /// </summary>
    [Fact]
    public void UnhandledThrowingCallMayBeAssigned()
    {
        AssertClean(Throwing + "throws int func F() { let int x = 0; x = R(-1); return x; }\n" +
                    "realm kernel { entry func Main() { } }");
        AssertClean(Throwing + """
            realm kernel { entry func Main() { let int x = 0; try { x = R(-1); } catch { x = 5; } let int y = x; } }
            """);
    }

    #endregion

    #region Rejected forms

    /// <summary>
    /// A compound assignment reads its target as well as writing it, so 'assign' has two candidate
    /// meanings and neither is obvious. Rejected with the form named, exactly once.
    /// </summary>
    [Fact]
    public void CompoundAssignmentIsRejectedOnce()
    {
        var d = AssertOne(Codes.ThrowsOutsideTry, Throwing +
            "realm kernel { entry func Main() { let int x = 0; x += R(-1) catch { assign 1; }; } }");
        Assert.Contains("+=", d.Message);
    }

    [Fact]
    public void HandlerStillRejectedInsideALargerExpression()
    {
        AssertOne(Codes.ThrowsOutsideTry, Throwing +
            "void func T(int a) { } realm kernel { entry func Main() { T(R(-1) catch { assign 1; }); } }");
    }

    /// <summary>
    /// The same completeness rule a declaration gets: a handler that can fall out the bottom has
    /// not supplied the value it was attached to.
    /// </summary>
    [Fact]
    public void HandlerOnAnAssignmentMustSupplyAValue()
    {
        AssertOne(Codes.CatchHandlerNoAssign, Throwing +
            "realm kernel { entry func Main() { let int x = 0; x = R(-1) catch { if (true) { assign 1; } }; } }");
    }

    /// <summary>
    /// The handler's value is checked against what the call produces, the same as on a declaration.
    /// Target and call agree here, so the only mistake is the 'assign' itself.
    /// </summary>
    [Fact]
    public void AssignedValueIsTypeChecked()
    {
        var d = AssertOne(Codes.TypeMismatch, "import String;\nthrows String func RS() { throw; }\n" +
            "realm kernel { entry func Main() { let String s = \"\"; s = RS() catch { assign 1; }; } }");
        Assert.Contains("'assign'", d.Message);
    }

    /// <summary>
    /// A call whose result cannot land in the target is reported against the target, not left to
    /// surface as a Result type mismatch somewhere downstream.
    /// </summary>
    [Fact]
    public void UnhandledCallResultIsCheckedAgainstTheTarget()
    {
        var (diag, _) = SingleFileCompile.Check("import String;\nthrows String func RS() { throw; }\n" +
            "throws void func F() { let int x = 0; x = RS(); }\nrealm kernel { entry func Main() { } }");
        Assert.Contains(diag.All, d => d.Code == Codes.TypeMismatch && d.Message.Contains("throwing call produces"));
    }

    #endregion

    #region Execution

    private static (string?, string?) Environment() => (HostedRun.FindGataCheckout(), HostedRun.FindCompiler());

    /// <summary>
    /// Reference counting through the new paths. Every arm that stores has to release what the
    /// target held and own what it takes, and the self-assigning handler is the sharp one: releasing
    /// the old value before storing would free the value being stored.
    /// </summary>
    [Fact]
    public void AssignmentHandlersKeepRefcountsExact()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import String;

            class Res {
                public String tag;
                public func _init(String t) { self.tag = t; }
                func _deinit() { Console.PrintLine($"drop {self.tag}"); }
            }
            throws Res func RR(int x, String t) { if (x < 0) { throw; } return new Res(t); }

            realm userspace { entry func Main() {
                let Res a = new Res("first");
                Console.PrintLine("-- handler runs");
                a = RR(-1, "unused") catch { assign new Res("fallback"); };
                Console.PrintLine("-- call succeeds");
                a = RR(1, "fromcall") catch { assign new Res("unused2"); };
                Console.PrintLine("-- self assign");
                a = RR(-1, "unused3") catch { assign a; };
                Console.PrintLine($"held {a.tag}");
                Console.PrintLine("-- end");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("""
            -- handler runs
            drop first
            -- call succeeds
            drop fallback
            -- self assign
            held fromcall
            -- end
            drop fromcall

            """.Replace("\r\n", "\n"), r.Output);
    }

    /// <summary>
    /// The target is written once and read once however it is spelled, so an index with a side
    /// effect in it must not run twice - the value arm and the handler arm both store to it.
    /// </summary>
    [Fact]
    public void AssignmentTargetIsEvaluatedExactlyOnce()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            native { static int _c = 0; static int _bump(void) { _c++; return 0; } static int _n(void) { return _c; } }
            @extern int func _bump();
            @extern int func _n();
            throws int func R(int x) { if (x < 0) { throw; } return x; }

            realm userspace { entry func Main() {
                let [2]int a = [0, 0];
                a[_bump()] = R(-1) catch { assign 5; };
                Console.PrintLine($"fail calls={_n()} v={a[0]}");
                a[_bump()] = R(9) catch { assign 5; };
                Console.PrintLine($"ok calls={_n()} v={a[0]}");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("fail calls=1 v=5\nok calls=2 v=9\n", r.Output);
    }

    #endregion
}
