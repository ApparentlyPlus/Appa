namespace Appa.Tests;

using Appa;

/// <summary>
/// Regression coverage for the final robustness sweep. Each case is one defect found by probing
/// rather than by a failing test: a warning that fired on correct code, a cascade that reported one
/// mistake several times, or a form the grammar accepted in one position and not the mirror one.
/// </summary>
public class FinalSweepTests
{
    private static (DiagnosticBag Diag, IrModule? Module) Check(string src) => SingleFileCompile.Check(src);

    private static void AssertClean(string src)
    {
        var (diag, _) = Check(src);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
    }

    private static void AssertNoDiagnostic(string code, string src)
    {
        var (diag, _) = Check(src);
        Assert.DoesNotContain(diag.All, d => d.Code == code);
    }

    private static Diagnostic AssertOne(string code, string src)
    {
        var (diag, _) = Check(src);
        var hits = diag.All.Where(d => d.Code == code).ToList();
        Assert.True(hits.Count == 1,
            $"expected exactly one {code}, got {hits.Count}. All: " +
            string.Join("; ", diag.All.Select(d => $"{d.Code} {d.Message}")));
        return hits[0];
    }

    #region Catch handlers are part of the body

    private const string _throwing = "throws int func R(int x) { if (x < 0) { throw; } return x; }\n";

    /// <summary>
    /// The unused-local walk did not descend into a catch handler, so a variable read only there
    /// was reported unused - a warning on correct code, at the exact spot the handler exists for.
    /// </summary>
    [Fact]
    public void UseInsideCatchHandlerCounts()
    {
        AssertNoDiagnostic(Codes.UnusedVariable, _throwing + """
            realm kernel { entry func Main() {
                let int s = 5;
                let int t = R(-1) catch { assign s + 1; };
                debug($"{t}");
            } }
            """);
    }

    /// <summary>
    /// Likewise for the loop-break search: a 'break' in a handler does leave the enclosing loop, so
    /// the code after the loop is reachable and must not be reported dead.
    /// </summary>
    [Fact]
    public void BreakInsideCatchHandlerExitsTheLoop()
    {
        AssertNoDiagnostic(Codes.UnreachableCode, _throwing + """
            realm kernel { entry func Main() {
                while (true) { let int t = R(-1) catch { break; }; debug($"{t}"); }
                let int after = 1;
                debug($"{after}");
            } }
            """);
    }

    #endregion

    #region One mistake, one message

    [Fact]
    public void NewOnUnionSaysHowToBuildOne()
    {
        var d = AssertOne(Codes.NewOnNonClass,
            "union U { A(int n), B } realm kernel { entry func Main() { let U u = new U(); } }");
        Assert.Contains("union", d.Message);
        Assert.Contains(d.Hints, h => h.Contains("Variant"));
        // The old failure path handed back the class type, producing "cannot assign 'U' to 'u' of
        // type 'U'" on top - a second error whose message contradicted itself.
        AssertNoDiagnostic(Codes.TypeMismatch,
            "union U { A(int n), B } realm kernel { entry func Main() { let U u = new U(); } }");
    }

    [Fact]
    public void NewOnEnumSaysHowToNameOne()
    {
        var d = AssertOne(Codes.NewOnNonClass,
            "enum E { X } realm kernel { entry func Main() { let E e = new E(); } }");
        Assert.Contains("enum", d.Message);
        Assert.Contains(d.Hints, h => h.Contains("Member"));
    }

    /// <summary>
    /// A rejected instantiation is never stamped, so every later pass saw a type that did not
    /// exist and said so again. The Monomorphizer's reason is the only one worth printing.
    /// </summary>
    [Fact]
    public void RejectedInstantiationIsReportedOnce()
    {
        AssertOne(Codes.WrongArgCount, """
            class Box[T] { public T v; }
            realm kernel { entry func Main() { let Box[int, int] b = new Box[int, int](); } }
            """);
        AssertNoDiagnostic(Codes.UndefinedType, """
            class Box[T] { public T v; }
            realm kernel { entry func Main() { let Box[int, int] b = new Box[int, int](); } }
            """);
    }

    /// <summary>
    /// A stamped instance lives in the template's file, so a bad type argument reports there with
    /// nothing naming the instantiation the author actually wrote.
    /// </summary>
    [Fact]
    public void BadTypeArgumentNamesTheInstantiation()
    {
        var d = AssertOne(Codes.UndefinedType, """
            union Opt[T] { Some(T v), None }
            realm kernel { entry func Main() { let Opt[Nope] o = Opt[Nope].None(); } }
            """);
        Assert.Contains(d.Hints, h => h.Contains("comes from the instantiation") && h.Contains("Opt[Nope]"));
    }

    #endregion

    #region Explicit instantiation in expression position

    /// <summary>
    /// 'U[int].A(1)' registered its arguments as instantiation requests but not itself, so it
    /// compiled only when something else happened to name 'U[int]' as a type.
    /// </summary>
    [Fact]
    public void ExplicitInstantiationRequestsItsOwnStamp()
    {
        AssertClean("""
            union U[T] { A(T v), B }
            realm kernel { entry func Main() { let bool same = U[int].A(1) == U[int].A(1); } }
            """);
    }

    /// <summary>
    /// A stamped generic class is an ordinary class, so a static call on one resolves like any
    /// other. It used to be rejected as "not a union".
    /// </summary>
    [Fact]
    public void StaticCallOnStampedGenericResolves()
    {
        AssertClean("""
            class Box[T] { public T v; public static int func Zero() { return 0; } }
            realm kernel { entry func Main() { let int z = Box[int].Zero(); } }
            """);
    }

    /// <summary>
    /// A scope qualifier and an explicit type argument list are each supported, and were not
    /// supported together in expression position - a parse error, though the type position took it.
    /// </summary>
    [Theory]
    [InlineData("union U[T] { A(T v), B }\nrealm kernel { entry func Main() { let U[int] u = ::U[int].A(1); } }")]
    [InlineData("realm kernel { union U[T] { A(T v), B }\n entry func Main() { let U[int] u = kernel.U[int].A(1); } }")]
    [InlineData("class Box[T] { public T v; public static int func Z() { return 0; } }\n" +
                "realm kernel { entry func Main() { let int z = ::Box[int].Z(); } }")]
    public void QualifiedExplicitInstantiationResolves(string src) => AssertClean(src);

    /// <summary>
    /// The scoped generic base kept its unqualified name through substitution, so a template
    /// declared inside a realm matched no registered template.
    /// </summary>
    [Fact]
    public void ScopedGenericUnionResolvesByExplicitInstantiation()
    {
        AssertClean("realm kernel { union U[T] { A(T v), B }\n" +
                    " entry func Main() { let U[int] u = U[int].B(); } }");
    }

    /// <summary>
    /// Reaching sideways stays rejected: the qualifier is a disambiguator, not a visibility rule.
    /// </summary>
    [Fact]
    public void QualifiedInstantiationStillObeysTheScopeRules()
    {
        var (diag, _) = Check("realm kernel { union U[T] { A(T v), B } entry func Main() { } }\n" +
                              "realm userspace { entry func Go() { let int n = kernel.U[int].A(1) == null ? 1 : 0; } }");
        Assert.Contains(diag.All, d => d.Code is Codes.ScopeNotEnclosing or Codes.UnknownInScope
                                              or Codes.TypeMismatch or Codes.EntryOutsideKernel);
    }

    #endregion

    #region New warnings

    /// <summary>
    /// Relational operators never derive from one another, so half a family is a type error at
    /// every call site of the other half - reported at the declaration, where it is fixable.
    /// </summary>
    [Fact]
    public void PartialRelationalSetWarns()
    {
        var d = AssertOne(Codes.PartialOperatorSet, """
            class V { public int n; public operator bool func <(V o) { return self.n < o.n; } }
            realm kernel { entry func Main() { } }
            """);
        Assert.Contains("'<'", d.Message);
        Assert.Contains("'>'", d.Message);
    }

    [Fact]
    public void CompleteRelationalSetIsSilent()
    {
        AssertNoDiagnostic(Codes.PartialOperatorSet, """
            class V { public int n;
                public operator bool func <(V o) { return self.n < o.n; }
                public operator bool func >(V o) { return self.n > o.n; } }
            realm kernel { entry func Main() { } }
            """);
    }

    /// <summary>
    /// Declaring neither is fine - the warning is about an incomplete family, not a missing one.
    /// </summary>
    [Fact]
    public void NoRelationalOperatorsIsSilent()
    {
        AssertNoDiagnostic(Codes.PartialOperatorSet,
            "class V { public int n; } realm kernel { entry func Main() { } }");
    }

    #endregion

    #region Structural holes

    /// <summary>
    /// A process is reached only through its threads, so one with none is created at boot and then
    /// does nothing. It used to emit a bare _env_proc_create and no diagnostic.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { entry func Main() { } foreground process P { } }")]
    [InlineData("realm kernel { entry func Main() { } }\nrealm userspace { background process P { } }")]
    public void ProcessWithoutThreadsIsRejected(string src)
    {
        var prog = SingleFileCompile.Parse(src);
        var sources = new SourceSet();
        sources.Add("<test>", src);
        var diag = new DiagnosticBag(sources);
        Pipeline.ValidateStructure([("<test>", prog)], Target.GatOS, diag);
        Assert.Contains(diag.All, d => d.Code == Codes.ProcessWithoutThreads);
    }

    [Fact]
    public void ProcessWithAThreadIsAccepted()
    {
        const string src = "realm kernel { entry func Main() { } " +
                           "foreground process P { thread T { entry func Run() { } } } }";
        var prog = SingleFileCompile.Parse(src);
        var sources = new SourceSet();
        sources.Add("<test>", src);
        var diag = new DiagnosticBag(sources);
        Pipeline.ValidateStructure([("<test>", prog)], Target.GatOS, diag);
        Assert.DoesNotContain(diag.All, d => d.Code == Codes.ProcessWithoutThreads);
    }

    /// <summary>
    /// A build-wide diagnostic belongs to no file, and rendered as a bare ": error[...]" header.
    /// </summary>
    [Fact]
    public void BuildWideDiagnosticsCarryALocationMarker()
    {
        const string src = "int func F() { return 1; }";
        var prog = SingleFileCompile.Parse(src);
        var sources = new SourceSet();
        sources.Add("<test>", src);
        var diag = new DiagnosticBag(sources);
        Pipeline.ValidateStructure([("<test>", prog)], Target.GatOS, diag);

        var d = Assert.Single(diag.All, x => x.Code == Codes.MissingEntryPoint);
        Assert.NotEqual("", d.Loc.File);
        Assert.NotEmpty(d.Hints);
    }

    #endregion

    #region Diagnostic bag

    /// <summary>
    /// Truncation puts the counters back too, or a dropped error would leave HasErrors set and the
    /// build would fail with nothing printed to explain it.
    /// </summary>
    [Fact]
    public void TruncateRestoresTheCounters()
    {
        var sources = new SourceSet();
        sources.Add("f.g", "x");
        var diag = new DiagnosticBag(sources);

        diag.Error("G001", "f.g", TextSpan.None, "kept");
        int mark = diag.All.Count;
        diag.Error("G002", "f.g", TextSpan.None, "dropped");
        diag.Warn("G003", "f.g", TextSpan.None, "dropped too");

        diag.TruncateTo(mark);

        Assert.Single(diag.All);
        Assert.Equal(1, diag.ErrorCount);
        Assert.Equal(0, diag.WarningCount);
        Assert.True(diag.HasErrors);
    }

    #endregion
}
