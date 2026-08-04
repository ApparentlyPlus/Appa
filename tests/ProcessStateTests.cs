namespace Appa.Tests;

using Appa;

/// <summary>
/// Process-scoped variables: the one storage in Gata that outlives a scope.
/// </summary>
public class ProcessStateTests
{
    /// <summary>
    /// Enough of the runtime for a class to be reference-counted, so a managed process variable can
    /// be checked without dragging in libgata.
    /// </summary>
    private const string ArcStub = """
        @intrinsic(obj_header)
        native type obj { void* __dtor; unsigned long __rc; }
        @intrinsic(alloc)
        void* func gata_alloc(usize n) native { return 0; }
        @intrinsic(obj_init)
        void func gata_obj_init(void* o, func(void*) -> void d) native { (void)o; (void)d; }
        @intrinsic(retain)
        void* func gata_retain(void* p) native { return p; }
        @intrinsic(release)
        void func gata_release(void* p) native { (void)p; }
        class Cell { public int v; func _init() { self.v = 0; } }
        """;

    /// <summary>
    /// A throwing function, for the initialiser positions that need one.\
    /// </summary>
    private const string Throwing =
        "throws int func Boom(int x) { if (x < 0) { throw; } return x; }\n";

    private static (DiagnosticBag Diag, string[] Errors) Check(string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        return (diag, [.. diag.All.Where(d => d.Severity == Severity.Error).Select(d => d.Code)]);
    }

    private static void AssertError(string code, string src)
    {
        var (_, errors) = Check(src);
        Assert.True(errors.Contains(code),
            $"expected {code}, got: {(errors.Length == 0 ? "no errors" : string.Join(", ", errors))}");
    }

    private static void AssertClean(string src)
    {
        var (diag, errors) = Check(src);
        Assert.True(errors.Length == 0, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
    }

    /// <summary>
    /// Wraps a process body in the smallest program that can hold one.
    /// </summary>
    private static string InProcess(string body, string prelude = "") =>
        $$"""
        {{prelude}}
        realm kernel {
            entry func Main() { }
            background process P {
                {{body}}
                thread T { entry func Run() { } }
            }
        }
        """;

    #region The initialiser is required

    /// <summary>
    /// The rule the feature turns on. A process variable is read by threads that did not run the
    /// line declaring it, so no definite-assignment analysis can decide whether a store happened
    /// first - which is why this is an error rather than the warning a local gets.
    /// </summary>
    [Theory]
    [InlineData("let int n;")]
    [InlineData("let Cell c;")]
    [InlineData("let int a = 1; let int b;")]
    public void ProcessVariableWithoutAnInitialiserIsRejected(string body)
    {
        AssertError(Codes.UninitialisedProcessVar, InProcess(body, ArcStub));
    }

    [Theory]
    [InlineData("let int n = 0;")]
    [InlineData("let Cell c = new Cell();")]
    [InlineData("let int a = 1; let int b = a + 1;")]
    public void ProcessVariableWithAnInitialiserIsAccepted(string body)
    {
        AssertClean(InProcess(body, ArcStub));
    }

    #endregion

    #region The initialiser is checked like any other

    [Fact]
    public void InitialiserTypeIsChecked()
    {
        AssertError(Codes.TypeMismatch, InProcess("""let int n = "text";"""));
    }

    /// <summary>
    /// A throwing call has no handler at this point - the initialiser runs before any thread, in
    /// generated code with nowhere to propagate to.
    /// </summary>
    [Fact]
    public void ThrowingInitialiserIsRejected()
    {
        AssertError(Codes.ThrowsOutsideTry,
            InProcess("let int n = F();", "throws int func F() { return 1; }"));
    }

    [Fact]
    public void DuplicateProcessVariableIsRejected()
    {
        AssertError(Codes.DuplicateName, InProcess("let int n = 1; let int n = 2;"));
    }

    #endregion

    #region Where a variable may be declared

    /// <summary>
    /// A process is the only construct that can hold one. The others each have a reason: a realm
    /// and a file have no instance to belong to, a module is a namespace, and a class field is
    /// already per-instance state.
    /// </summary>
    [Theory]
    [InlineData("let int n = 1; realm kernel { entry func Main() { } }")]
    [InlineData("realm kernel { let int n = 1; entry func Main() { } }")]
    [InlineData("module M { let int n = 1; } realm kernel { entry func Main() { } }")]
    [InlineData("class C { let int n = 1; } realm kernel { entry func Main() { } }")]
    public void AVariableOutsideAProcessIsRejected(string src)
    {
        AssertError(Codes.Syntax, src);
    }

    /// <summary>
    /// 'static' on a class field is the other spelling someone reaches for, and it has its own
    /// message already. Pinned here so the two answers stay distinguishable.
    /// </summary>
    [Fact]
    public void StaticFieldIsStillRejectedSeparately()
    {
        AssertError(Codes.BadDeclHeader,
            "class C { static int n; public int v; func _init() { self.v = 1; } } " +
            "realm kernel { entry func Main() { } }");
    }

    #endregion

    #region Visibility

    /// <summary>
    /// A process variable belongs to its process. Reaching one from the realm around it is the same
    /// rule that already governs a process's types and functions, so it reports the same way.
    /// </summary>
    [Fact]
    public void ProcessVariableIsNotVisibleOutsideItsProcess()
    {
        AssertError(Codes.ScopedNameNotVisible, """
            realm kernel {
                entry func Main() { let int x = n; }
                background process P { let int n = 1; thread T { entry func Run() { } } }
            }
            """);
    }

    /// <summary>
    /// Nor by naming the path explicitly: a qualifier disambiguates between enclosing scopes, and a
    /// process is not one of those from outside it.
    /// </summary>
    [Fact]
    public void QualifierCannotReachIntoAProcess()
    {
        AssertError(Codes.ScopeNotEnclosing, """
            realm kernel {
                entry func Main() { let int x = kernel.P.n; }
                background process P { let int n = 1; thread T { entry func Run() { } } }
            }
            """);
    }

    /// <summary>
    /// Two processes may use the same name; each gets its own storage. This is the case that would
    /// have collided in C had the name not carried the process.
    /// </summary>
    [Fact]
    public void TwoProcessesMayDeclareTheSameName()
    {
        AssertClean("""
            realm kernel {
                entry func Main() { }
                background process P { let int n = 1; thread T { entry func Run() { let int a = n; } } }
                background process Q { let int n = 2; thread T { entry func Run() { let int a = n; } } }
            }
            """);
    }

    /// <summary>
    /// The threads of the process see it, and so do the functions the process declares - which is
    /// what makes it state rather than a variable that happens to live longer.
    /// </summary>
    [Fact]
    public void ThreadsAndProcessFunctionsBothSeeIt()
    {
        AssertClean("""
            realm kernel {
                entry func Main() { }
                background process P {
                    let int n = 1;
                    int func Read() { return n; }
                    void func Write(int v) { n = v; }
                    thread T { entry func Run() { Write(Read() + 1); } }
                }
            }
            """);
    }

    #endregion

    #region Shadowing

    /// <summary>
    /// A local of the same name takes the name over silently, which is the dangerous direction: the
    /// thread reads and writes its own copy while believing it shares one.
    /// </summary>
    [Fact]
    public void LocalShadowingProcessVariableWarns()
    {
        var (diag, _) = Check("""
            realm kernel {
                entry func Main() { }
                background process P {
                    let int n = 1;
                    thread T { entry func Run() { let int n = 2; let int use = n; } }
                }
            }
            """);
        Assert.Contains(diag.All, d => d.Severity == Severity.Warning
                                       && d.Code == Codes.ShadowedVariable
                                       && d.Message.Contains("process variable"));
    }

    /// <summary>
    /// And the warning is about shadowing specifically, so an unrelated local does not draw it.
    /// </summary>
    [Fact]
    public void UnrelatedLocalDoesNotWarn()
    {
        var (diag, _) = Check("""
            realm kernel {
                entry func Main() { }
                background process P {
                    let int n = 1;
                    thread T { entry func Run() { let int other = n; let int use = other; } }
                }
            }
            """);
        Assert.DoesNotContain(diag.All, d => d.Code == Codes.ShadowedVariable);
    }

    #endregion

    #region An initialiser cannot read what has no value yet

    /// <summary>
    /// The variables are filled in declaration order by one generated function, so an initialiser
    /// can only read the ones above it. 
    /// </summary>
    [Theory]
    [InlineData("let int a = a + 1;")]                       // itself
    [InlineData("let Cell c = c;")]                          // itself, managed: stays null forever
    [InlineData("let int a = b; let int b = 1;")]            // one declared below
    [InlineData("let int a = b; let int b = a;")]            // mutually
    public void InitialiserReadingAnUninitialisedVariableIsRejected(string body)
    {
        AssertError(Codes.UseBeforeAssignment, InProcess(body, ArcStub));
    }

    /// <summary>
    /// The legal counterpart: reading one declared above is exactly what the ordering guarantees.
    /// </summary>
    [Fact]
    public void InitialiserMayReadOneDeclaredAbove()
    {
        AssertClean(InProcess("let int a = 2; let int b = a * 3;", ArcStub));
    }

    /// <summary>
    /// Reported as reading something with no value, not as an unknown name. Before the two-pass
    /// registration these came back "'kernel.P.b' is not defined", which names a variable the
    /// author can plainly see two lines below and offers nothing to do about it.
    /// </summary>
    [Fact]
    public void TheDiagnosticNamesTheVariableAsWritten()
    {
        var (diag, _) = Check(InProcess("let int a = b; let int b = 1;", ArcStub));
        var d = Assert.Single(diag.All, x => x.Code == Codes.UseBeforeAssignment);
        Assert.Contains("'b' is read before it is initialised", d.Message);
        Assert.DoesNotContain("kernel.P", d.Message);
    }

    [Fact]
    public void ReadingItselfSaysSo()
    {
        var (diag, _) = Check(InProcess("let int a = a + 1;", ArcStub));
        var d = Assert.Single(diag.All, x => x.Code == Codes.UseBeforeAssignment);
        Assert.Contains("its own initialiser", d.Message);
    }

    #endregion

    #region A handler must leave a value behind

    /// <summary>
    /// A throwing initialiser is legal only with a handler that supplies a value on every path -
    /// the same rule a local declaration gets.
    /// </summary>
    [Fact]
    public void CatchHandlerWithoutAnAssignIsRejected()
    {
        AssertError(Codes.CatchHandlerNoAssign, InProcess("let int a = Boom(1) catch { };", Throwing));
    }

    [Fact]
    public void CatchHandlerWithAnAssignIsAccepted()
    {
        AssertClean(InProcess("let int a = Boom(1) catch { assign 7; };", Throwing));
    }

    /// <summary>
    /// 'return' satisfies "leaves the handler" for a local, where it returns from a function the
    /// author wrote.
    /// </summary>
    [Theory]
    [InlineData("let int a = Boom(1) catch { return; }; let int b = 2;")]
    [InlineData("let int a = Boom(1) catch { return 5; };")]
    [InlineData("let int a = Boom(1) catch { if (1 > 0) { return; } assign 2; };")]
    public void CatchHandlerCannotReturn(string body)
    {
        AssertError(Codes.UninitialisedProcessVar, InProcess(body, Throwing));
    }

    /// <summary>
    /// One error, not two: the generated initialiser returns void, so checking 'return 5;' against
    /// it would also report a mismatch against a function the author never wrote.
    /// </summary>
    [Fact]
    public void ReturningAValueFromAHandlerReportsOnlyTheRealMistake()
    {
        var (_, errors) = Check(InProcess("let int a = Boom(1) catch { return 5; };", Throwing));
        Assert.Equal([Codes.UninitialisedProcessVar], errors);
    }

    /// <summary>
    /// The other two ways out of a handler stay rejected by the rules that already covered them,
    /// so the check above did not have to grow a list of statement kinds.
    /// </summary>
    [Theory]
    [InlineData("let int a = Boom(1) catch { break; };")]
    [InlineData("let int a = Boom(1) catch { continue; };")]
    public void CatchHandlerCannotBreakOrContinue(string body)
    {
        AssertError(Codes.BreakOutsideLoop, InProcess(body, Throwing));
    }

    /// <summary>
    /// 'throw' is the give-up path in a throws function; the generated initialiser is not one, so
    /// there is nowhere to propagate to.
    /// </summary>
    [Fact]
    public void CatchHandlerCannotThrow()
    {
        AssertError(Codes.ThrowsOutsideTry, InProcess("let int a = Boom(1) catch { throw; };", Throwing));
    }

    #endregion

    #region Function pointers

    /// <summary>
    /// A process variable holding a function pointer is called through its name, the same as a
    /// local holding one. Resolution used to consider only locals, so this came back "'f' is a
    /// process variable here, not a function" - which reads like a language rule, while copying it
    /// into a local first worked fine.
    /// </summary>
    [Fact]
    public void AFunctionPointerInProcessStateIsCallable()
    {
        AssertClean($$"""
            int func Twice(int x) { return x * 2; }
            realm kernel {
                entry func Main() { }
                background process P {
                    let func(int) -> int f = Twice;
                    thread T { entry func Run() { let int q = f(3); Sink(q); } }
                }
            }
            void func Sink(int x) { }
            """);
    }

    /// <summary>
    /// A local of the same name still shadows it, as it does for every other read.
    /// </summary>
    [Fact]
    public void ALocalFunctionPointerStillShadowsTheProcessOne()
    {
        var (diag, _) = Check($$"""
            int func Twice(int x) { return x * 2; }
            int func Thrice(int x) { return x * 3; }
            realm kernel {
                entry func Main() { }
                background process P {
                    let func(int) -> int f = Twice;
                    thread T {
                        entry func Run() { let func(int) -> int f = Thrice; Sink(f(3)); }
                    }
                }
            }
            void func Sink(int x) { }
            """);
        Assert.DoesNotContain(diag.All, d => d.Severity == Severity.Error);
        Assert.Contains(diag.All, d => d.Code == Codes.ShadowedVariable);
    }

    /// <summary>
    /// Calling one before its initialiser has run is a call through a null function pointer. The
    /// call path checks the same pending set the read path does, so this is an ordering error
    /// rather than a fault at boot.
    /// </summary>
    [Fact]
    public void CallingAFunctionPointerBeforeItIsInitialisedIsRejected()
    {
        AssertError(Codes.UseBeforeAssignment, """
            int func Twice(int x) { return x * 2; }
            realm kernel {
                entry func Main() { }
                background process P {
                    let int a = f(2);
                    let func(int) -> int f = Twice;
                    thread T { entry func Run() { Sink(a); } }
                }
            }
            void func Sink(int x) { }
            """);
    }

    #endregion

    #region Lowering

    /// <summary>
    /// The shape of the generated C, asserted because most of it is invisible from Gata: a static
    /// per variable, an initialiser, and a gate every thread passes through before its own body.
    /// </summary>
    [Fact]
    public void LoweringEmitsStaticsAndAGatedInitialiser()
    {
        var (diag, module) = SingleFileCompile.Check("""
            realm kernel {
                entry func Main() { }
                background process P {
                    let int n = 7;
                    thread A { entry func Run() { let int a = n; } }
                    thread B { entry func Run() { let int b = n; } }
                }
            }
            """);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);

        var proc = Assert.Single(module!.Processes);
        var v = Assert.Single(proc.State);
        Assert.Equal("n", v.Name);
        Assert.NotNull(proc.StateInit);
        Assert.Equal(2, proc.Threads.Count);
    }

    /// <summary>
    /// A process with no variables gains nothing - no gate, no initialiser, no per-thread call.
    /// </summary>
    [Fact]
    public void ProcessWithoutStateGetsNoInitialiser()
    {
        var (diag, module) = SingleFileCompile.Check("""
            realm kernel {
                entry func Main() { }
                background process P { thread T { entry func Run() { } } }
            }
            """);
        Assert.False(diag.HasErrors);
        var proc = Assert.Single(module!.Processes);
        Assert.Empty(proc.State);
        Assert.Null(proc.StateInit);
    }

    #endregion
}
