namespace Appa.Tests;

using Appa;

/// <summary>
/// Naming a scope outright: 'kernel.Step', 'kernel.P.Config', '::Helper'. What '@shadows' declares,
/// a qualifier undoes, so every level of a scope tree stays reachable from inside the one that
/// displaced it - outward only, and never as a way into a sibling.
/// </summary>
public class ScopeQualifierTests
{
    private static List<string> Errors(string src) =>
        [.. SingleFileCompile.Check(src).Diag.All
            .Where(d => d.Severity == Severity.Error)
            .Select(d => $"{d.Code} {d.Message}")];

    #region Reaching outward

    /// <summary>
    /// Every enclosing scope is nameable from every depth, in expression position.
    /// </summary>
    [Theory]
    [InlineData("kernel.P.Step()")]
    [InlineData("kernel.Step()")]
    [InlineData("::Step()")]
    [InlineData("Step()")]
    public void EveryEnclosingScopeIsNameable(string call)
    {
        Assert.Empty(Errors($$"""
            int func Step() { return 1; }
            realm kernel {
                @shadows int func Step() { return 2; }
                foreground process P {
                    @shadows int func Step() { return 3; }
                    thread T { entry func R() { let int z = {{call}}; } }
                }
                entry func Main() { }
            }
            """));
    }

    /// <summary>
    /// Type position too. Four of the six forms '@shadows' may mark are types, so an
    /// expression-only qualifier would reach past only a third of what shadowing applies to.
    /// </summary>
    [Theory]
    [InlineData("let ::Cargo c = new ::Cargo(); let int q = c.root;")]
    [InlineData("let kernel.Cargo c = new kernel.Cargo(); let int q = c.inr;")]
    [InlineData("let Cargo c = new Cargo(); let int q = c.inr;")]
    public void QualifiersWorkInTypePosition(string body)
    {
        Assert.Empty(Errors($$"""
            class Cargo { public int root; }
            realm kernel {
                @shadows class Cargo { public int inr; }
                entry func Main() { {{body}} }
            }
            """));
    }

    /// <summary>
    /// A qualifier is legal wherever a name is, not only where one is shadowed.
    /// </summary>
    [Theory]
    [InlineData("class Holder { public ::Cargo held; }")]
    [InlineData("::Cargo func Make(::Cargo c) { return c; }")]
    [InlineData("void func Take(Box[::Cargo] b) { let int n = b.v.root; }")]
    public void QualifiersWorkInEveryTypeSlot(string decl)
    {
        Assert.Empty(Errors($$"""
            class Box[T] { public T v; }
            class Cargo { public int root; }
            realm kernel {
                @shadows class Cargo { public int inr; }
                {{decl}}
                entry func Main() { }
            }
            """));
    }

    /// <summary>
    /// The segment after a scope may be a name whose own members follow. Only the scope tree can
    /// make that split, which is why the parser hands over every dotted segment at once.
    /// </summary>
    [Fact]
    public void ScopeThenNameThenMemberSplitsCorrectly()
    {
        Assert.Empty(Errors("""
            module Algo { public static int func Min(int a, int b) { return a; } }
            realm kernel {
                @shadows class Algo { public int Min; }
                foreground process P {
                    module M { public static int func G() { return 3; } }
                    thread T { entry func R() { let int a = kernel.P.M.G() + ::Algo.Min(1, 2); } }
                }
                entry func Main() { }
            }
            """));
    }

    /// <summary>
    /// A realm is one namespace however many blocks open it, so a qualifier reaches the half
    /// written somewhere else entirely.
    /// </summary>
    [Fact]
    public void AQualifierReachesASplitRealm()
    {
        Assert.Empty(Errors("""
            realm userspace { int func Step() { return 2; } }
            realm userspace {
                foreground process P {
                    @shadows int func Step() { return 3; }
                    thread T { entry func R() { let int v = Step() + userspace.Step(); } }
                }
            }
            realm kernel { entry func Main() { } }
            """));
    }

    #endregion

    #region Outward only

    /// <summary>
    /// A sibling realm and a sibling process are what scopes exist to separate. Naming one is the
    /// error, or the qualifier would be a new visibility rule rather than a disambiguator.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { class Cfg { public int a; } entry func Main() { } } " +
                "realm userspace { void func F() { let kernel.Cfg c = new kernel.Cfg(); } }")]
    [InlineData("realm kernel { foreground process A { class Cfg { public int a; } thread T { entry func R() { } } } " +
                "foreground process B { thread T { entry func R() { let kernel.A.Cfg c; } } } entry func Main() { } }")]
    [InlineData("realm kernel { int func Step() { return 1; } entry func Main() { } } " +
                "int func Outer() { return kernel.Step(); }")]
    public void SiblingAndInwardScopesAreRejected(string src)
    {
        var errors = Errors(src);
        Assert.Contains(errors, e => e.StartsWith(Codes.ScopeNotEnclosing, StringComparison.Ordinal));
    }

    /// <summary>
    /// A realm exists as a scope whether or not the program opens a block for it, so naming the one
    /// this code is not inside reads as a reach sideways rather than as a misspelling.
    /// </summary>
    [Fact]
    public void ARealmWithoutABlockStillReadsAsASibling()
    {
        var errors = Errors("realm kernel { entry func Main() { let userspace.X c; } }");
        Assert.Contains(errors, e => e.Contains("'userspace' does not enclose", StringComparison.Ordinal));
    }

    #endregion

    #region One exact scope

    /// <summary>
    /// A qualifier names one scope and stops there. Walking further out on a miss would make
    /// 'kernel.Config' quietly mean the root Config, which is the confusion it exists to remove.
    /// </summary>
    [Theory]
    [InlineData("int func Step() { return 1; } realm kernel { entry func Main() { let int z = kernel.Step(); } }")]
    [InlineData("realm kernel { int func Step() { return 1; } entry func Main() { let int z = ::Step(); } }")]
    [InlineData("realm kernel { foreground process P { int func Step() { return 1; } " +
                "thread T { entry func R() { } } } entry func Main() { let int z = kernel.Step(); } }")]
    public void ANameTheScopeDoesNotDeclareIsRejected(string src)
    {
        Assert.Contains(Errors(src), e => e.StartsWith(Codes.UnknownInScope, StringComparison.Ordinal));
    }

    /// <summary>
    /// A process is a scope, not a value, so naming one where a declaration belongs says so.
    /// </summary>
    [Fact]
    public void AProcessIsNotAName()
    {
        var errors = Errors("realm kernel { foreground process P { thread T { entry func R() { } } } " +
                            "entry func Main() { let int z = kernel.P(); } }");
        Assert.Contains(errors, e => e.Contains("'kernel' declares no 'P'", StringComparison.Ordinal));
    }

    #endregion

    #region Exactly one error

    /// <summary>
    /// A rejected qualifier is poison, not the bare name. Left as the bare name it would be resolved
    /// again by every later pass, each inventing its own complaint about a name the author never
    /// asked for on its own.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { entry func Main() { let int z = kernel.Nope(); } }")]
    [InlineData("realm kernel { entry func Main() { let int z = ::Nope(); } }")]
    [InlineData("realm kernel { entry func Main() { let int z = kernel.Q.Nope(); } }")]
    [InlineData("realm kernel { class Cfg { public int a; } entry func Main() { } } " +
                "realm userspace { void func F() { let kernel.Cfg c = new kernel.Cfg(); } }")]
    public void OneRejectedQualifierIsOneError(string src)
    {
        Assert.Single(Errors(src));
    }

    #endregion

    #region Boundaries

    /// <summary>
    /// The qualifier reaches past a displacement; it never excuses declaring one. '@shadows' says
    /// the displacement is deliberate, and that is a separate statement from reaching past it.
    /// </summary>
    [Fact]
    public void AQualifierDoesNotExcuseShadows()
    {
        Assert.Contains(Errors("int func Step() { return 1; } realm kernel { int func Step() { return 2; } " +
                               "entry func Main() { let int z = ::Step(); } }"),
                        e => e.StartsWith(Codes.UnmarkedShadow, StringComparison.Ordinal));
    }

    /// <summary>
    /// A local owns its name against a scoped declaration, and the qualifier reaches past the local
    /// too - it names a scope, and a local is in none.
    /// </summary>
    [Fact]
    public void AQualifierReachesPastALocalOfTheSameName()
    {
        Assert.Empty(Errors("""
            int func Step() { return 1; }
            realm kernel {
                @shadows int func Step() { return 2; }
                entry func Main() { let int Step = 9; let int a = Step; let int b = ::Step() + kernel.Step(); }
            }
            """));
    }

    /// <summary>
    /// Both realm names are reserved now, which is what lets a qualifier be recognised before
    /// anything is resolved rather than parsed both ways and decided later.
    /// </summary>
    [Theory]
    [InlineData("class userspace { public int a; } realm kernel { entry func Main() { } }")]
    [InlineData("realm kernel { entry func Main() { let int userspace = 1; } }")]
    [InlineData("realm kernel { entry func Main() { let int z = kernel; } }")]
    public void ARealmNameIsNotAnIdentifier(string src)
    {
        Assert.Contains(Errors(src), e => e.StartsWith(Codes.Syntax, StringComparison.Ordinal));
    }

    /// <summary>
    /// The lexer takes the longest run, so the conditional's ':' followed by '::' needs the space a
    /// reader would write anyway - and says so instead of reporting a missing ':'.
    /// </summary>
    [Fact]
    public void TightTernaryColonSaysWhatIsWrong()
    {
        Assert.Contains(Errors("int func Step() { return 1; } realm kernel { @shadows int func Step() { return 2; } " +
                               "entry func Main() { let int z = true?1:::Step(); } }"),
                        e => e.Contains("cannot be the ':' of a conditional", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing about a qualifier makes the compiler throw, however malformed the path.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { entry func Main() { let int z = ::::Step(); } }")]
    [InlineData("realm kernel { entry func Main() { let int z = kernel..Step(); } }")]
    [InlineData("realm kernel { entry func Main() { let :: c; } }")]
    [InlineData("realm kernel { class ::C { public int a; } entry func Main() { } }")]
    [InlineData("realm ::kernel { entry func Main() { } }")]
    [InlineData("realm kernel { entry func Main() { let int z = kernel.P.Q.R.S.T.U(); } }")]
    [InlineData("realm kernel { entry func Main() { let Box[kernel.] b; } }")]
    public void MalformedQualifiersAreReportedNotThrown(string src)
    {
        Assert.NotEmpty(Errors(src));
    }

    #endregion
}
