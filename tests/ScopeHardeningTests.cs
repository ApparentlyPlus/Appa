namespace Appa.Tests;

using Appa;

/// <summary>
/// The rules scoped declarations acquired alongside '@shadows', each pinned by the shape that used
/// to slip past it: a name given two meanings in one scope, a local losing to a scoped type, a
/// rejected duplicate process still emitting, and an internal spelling reaching the user.
/// </summary>
public class ScopeHardeningTests
{
    private static List<Diagnostic> Check(string src) => [.. SingleFileCompile.Check(src).Diag.All];

    private static List<string> Errors(string src) =>
        [.. Check(src).Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} {d.Message}")];

    #region One meaning per name

    /// <summary>
    /// A scope holds one meaning per name. Two functions are overloads and two types a plain
    /// duplicate; every other pairing leaves a name whose meaning depends on where it is read, and
    /// each was silently accepted.
    /// </summary>
    [Theory]
    [InlineData("class X { public int a; } int func X() { return 1; }")]
    [InlineData("int func X() { return 1; } class X { public int a; }")]
    [InlineData("enum X { A } int func X() { return 1; }")]
    [InlineData("union X { A(int q), B } int func X() { return 1; }")]
    [InlineData("module X { public static int func G() { return 1; } } int func X() { return 1; }")]
    [InlineData("native type X { int a; } int func X() { return 1; }")]
    [InlineData("class X { public int a; } @extern int func X(int q);")]
    [InlineData("class X { public int a; } class X[T] { public T v; }")]
    [InlineData("union X[T] { A(T q), B } class X { public int a; }")]
    public void OneNameOneMeaningAtRoot(string decls)
    {
        Assert.Contains(Errors(decls + " realm kernel { entry func Main() { } }"),
                        e => e.StartsWith(Codes.DuplicateName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same rule inside a realm and inside a process, where a process name counts too: it is a
    /// segment of the scope path, so 'kernel.P' would otherwise name both a process and a class.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { int func X() { return 1; } class X { public int a; } entry func Main() { } }")]
    [InlineData("realm kernel { class Box { public int a; } class Box[T] { public T v; } entry func Main() { } }")]
    [InlineData("realm kernel { class P { public int a; } foreground process P { } entry func Main() { } }")]
    [InlineData("realm kernel { int func P() { return 1; } foreground process P { } entry func Main() { } }")]
    [InlineData("realm kernel { foreground process P { enum X { A } int func X() { return 1; } } entry func Main() { } }")]
    public void OneNameOneMeaningWhenScoped(string src)
    {
        Assert.Contains(Errors(src), e => e.StartsWith(Codes.DuplicateName, StringComparison.Ordinal));
    }

    /// <summary>
    /// What the rule must not reject: overloads, the same name in sibling scopes, and a file-local
    /// function against a type another file declares.
    /// </summary>
    [Theory]
    [InlineData("int func F(int a) { return a; } int func F(bool b) { return 1; } realm kernel { entry func Main() { } }")]
    [InlineData("realm kernel { foreground process A { class C { public int a; } } " +
                "foreground process B { class C { public int b; } } entry func Main() { } }")]
    [InlineData("realm kernel { foreground process P { } entry func Main() { } } realm userspace { foreground process P { } }")]
    public void DistinctScopesOk(string src)
    {
        Assert.Empty(Errors(src));
    }

    #endregion

    #region Locals against scoped names

    /// <summary>
    /// A local, a parameter or a pattern binding owns its name against a scoped declaration, exactly
    /// as it does against a root one. The rewrite that qualifies scoped names walked bare
    /// identifiers blindly, so every one of these read as the type.
    /// </summary>
    [Theory]
    [InlineData("entry func Main() { let int Cfg = 5; let int q = Cfg; }")]
    [InlineData("int func F(int Cfg) { return Cfg; } entry func Main() { let int z = F(1); }")]
    [InlineData("entry func Main() { for (let int Cfg = 0; Cfg < 3; Cfg++) { let int q = Cfg; } }")]
    [InlineData("entry func Main() { { let int Cfg = 5; let int q = Cfg; } }")]
    public void LocalBeatsScopedType(string body)
    {
        Assert.Empty(Errors($"realm kernel {{ class Cfg {{ public int a; }} {body} }}"));
    }

    /// <summary>
    /// The binding is exactly as wide as its scope: the type still means the type before the local
    /// is declared, and again once the block that declared it closes.
    /// </summary>
    [Fact]
    public void ScopedTypeSurvivesBinding()
    {
        Assert.Empty(Errors("""
            realm kernel {
                module M { public static int func G() { return 7; } }
                entry func Main() {
                    let int first = M.G();
                    { let int M = 1; let int inner = M; }
                    let int last = M.G();
                }
            }
            """));
    }

    #endregion

    #region Rejected duplicate process

    /// <summary>
    /// A duplicate process is reported once and contributes nothing. It used to contribute
    /// everything when it happened to hold the only scoped declarations in the build, because the
    /// rewrite that empties it was skipped as a no-op - so its names reached the emitter unqualified
    /// and collided with the root ones.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { foreground process P { } foreground process P { int func Only() { return 7; } } " +
                "entry func Main() { let int z = Only(); } }")]
    [InlineData("realm kernel { class Anchor { public int a; } foreground process P { } " +
                "foreground process P { int func Only() { return 7; } } entry func Main() { let int z = Only(); } }")]
    public void DuplicateProcessDeclaresNothing(string src)
    {
        var errors = Errors(src);
        Assert.Contains(errors, e => e.Contains("process 'P' is already declared", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("undefined function 'Only'", StringComparison.Ordinal));
    }

    /// <summary>
    /// The name a rejected duplicate declares must not collide with the root declaration of that
    /// name either, which is how the leak first showed itself.
    /// </summary>
    [Fact]
    public void DuplicateProcessVsRoot()
    {
        var errors = Errors("class A { public int r; } realm kernel { foreground process P { } " +
                            "foreground process P { class A { public int y; } } entry func Main() { } }");
        Assert.Single(errors);
        Assert.Contains("process 'P' is already declared", errors[0], StringComparison.Ordinal);
    }

    #endregion

    #region Readable names

    /// <summary>
    /// Every duplicate-name diagnostic about a scoped declaration reads as the user wrote it.
    /// Only the class form went through Mangler.DisplayName, so the other five printed the internal
    /// spelling - and no corpus case declared any of them twice inside a realm.
    /// </summary>
    [Theory]
    [InlineData("enum E { A } enum E { B }", "kernel.E")]
    [InlineData("union U { A(int x), B } union U { C }", "kernel.U")]
    [InlineData("native type H { int a; } native type H { int b; }", "kernel.H")]
    [InlineData("int func D() { return 1; } int func D() { return 2; }", "kernel.D")]
    [InlineData("private int func D() { return 1; } private int func D() { return 2; }", "kernel.D")]
    public void ScopedDuplicatesReadWell(string decls, string expected)
    {
        var errors = Errors($"realm kernel {{ {decls} entry func Main() {{ }} }}");
        Assert.Contains(errors, e => e.Contains($"'{expected}'", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains('@', StringComparison.Ordinal));
    }

    /// <summary>
    /// An instantiation of a scoped name that is not generic is never stamped, so nothing records it
    /// as an instance the build produced. Reading it back from the key filed when the name was
    /// composed is what keeps the raw 'Box@kernel_int' out of the message.
    /// </summary>
    [Fact]
    public void UnstampedInstantiationReadsBack()
    {
        var tree = new ScopeTree();
        var kernel = tree.Intern(ScopeId.Root, "kernel", Realm.Kernel);
        var process = tree.Intern(kernel, "P", Realm.None);
        Mangler.Begin();
        Mangler.SetScopes(tree);

        string box = tree.Qualify(kernel, "Box");
        string cargo = tree.Qualify(process, "Cargo");

        Assert.Equal("kernel.Box[int]", Mangler.DisplayName(Mangler.GenericInstance(box, ["int"])));
        Assert.Equal("kernel.Box[kernel.P.Cargo]", Mangler.DisplayName(Mangler.GenericInstance(box, [cargo])));
        Assert.Equal("kernel.Box", Mangler.DisplayName(box));
        Assert.Equal("Unknown@nowhere", Mangler.DisplayName("Unknown@nowhere"));
    }

    #endregion

    #region Wrong kind

    /// <summary>
    /// A scoped declaration takes over its whole name, so a use in the wrong position finds it
    /// rather than the outer one it displaced. Saying "unknown type" there is the one answer that is
    /// untrue, and it used to be followed by two more errors about the type it invented.
    /// </summary>
    [Theory]
    [InlineData("class X { public int a; }", "@shadows int func X() { return 1; }",
                "let X v = new X();", "is a function here, not a type")]
    [InlineData("int func X() { return 1; }", "@shadows class X { public int a; }",
                "let int n = X();", "is a type here, not a function")]
    [InlineData("class Box[T] { public T v; }", "@shadows class Box { public int a; }",
                "let Box[int] b = new Box[int]();", "is a type here, not a generic type")]
    public void WrongKindIsNamedOnce(string root, string scoped, string use, string expected)
    {
        var errors = Errors($"{root} realm kernel {{ {scoped} entry func Main() {{ {use} }} }}");
        Assert.Single(errors);
        Assert.Contains(expected, errors[0], StringComparison.Ordinal);
    }

    #endregion

    #region Externs

    /// <summary>
    /// An '@extern' names a C symbol under exactly its own spelling and so cannot be qualified, but
    /// a scoped declaration of that name still takes the name over. It was missing from the set of
    /// declarations a scoped one can displace, so this shadowed silently.
    /// </summary>
    [Fact]
    public void ScopedDeclShadowsExtern()
    {
        Assert.Contains(Errors("@extern int func puts(int s); realm kernel { int func puts(int s) { return 0; } " +
                               "entry func Main() { } }"),
                        e => e.StartsWith(Codes.UnmarkedShadow, StringComparison.Ordinal));
        Assert.Empty(Errors("@extern int func puts(int s); realm kernel { @shadows int func puts(int s) { return 0; } " +
                            "entry func Main() { } }"));
    }

    #endregion

    #region Entry funcs own no name

    /// <summary>
    /// An entry func is named by the runtime and maps to one fixed C symbol, so its Gata name means
    /// nothing to anything else. It used to collide with an ordinary function of that name, which is
    /// the rule the ScopeBinder had already settled the other way for shadowing.
    /// </summary>
    [Fact]
    public void EntryFuncOwnsNoName()
    {
        Assert.Empty(Errors("int func Main() { return 7; } realm kernel { entry func Main() { let int z = Main(); } }"));
        Assert.Empty(Errors("int func Run() { return 1; } realm kernel { entry func Main() { } } " +
                            "realm userspace { foreground process P { thread T { entry func Run() { let int z = Run(); } } } }"));
    }

    /// <summary>
    /// It is still unreachable, and still collides with another entry - which is one fixed C symbol
    /// declared twice however the two are spelled.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { entry func Main() { } void func F() { Main(); } }", Codes.CallToEntry)]
    [InlineData("realm kernel { entry func Main() { } void func F() { let func() -> void f = Main; } }", Codes.CallToEntry)]
    [InlineData("realm kernel { entry func Main() { } entry func Main() { } }", Codes.DuplicateName)]
    [InlineData("realm kernel { entry func Main() { } entry func Other() { } }", Codes.DuplicateName)]
    public void EntryFuncUnreachableAndUnique(string src, string code)
    {
        Assert.Contains(Errors(src), e => e.StartsWith(code, StringComparison.Ordinal));
    }

    /// <summary>
    /// Rejecting a value use must not then invent a type for it.
    /// </summary>
    [Fact]
    public void EntryAsValueReportsOnce()
    {
        Assert.Single(Errors("realm kernel { entry func Main() { } void func F() { let func() -> void f = Main; } }"));
    }

    #endregion

    #region Modifiers on a top-level type

    /// <summary>
    /// A visibility or 'static' modifier belongs on a free function, never on a type. Without a
    /// targeted message the modifier reads as the start of a function and the error lands on the
    /// 'class' keyword, naming the wrong thing - and the book documented one of these forms.
    /// </summary>
    [Theory]
    [InlineData("public class X { public int n; }")]
    [InlineData("private class X { public int n; }")]
    [InlineData("static class X { public int n; }")]
    [InlineData("public module M { public static int func F() { return 1; } }")]
    [InlineData("private enum E { A }")]
    [InlineData("public union U { A(int n) }")]
    [InlineData("private native type N { int a; }")]
    public void TopLevelModifierNamed(string decl)
    {
        var errors = Errors($"{decl} realm kernel {{ entry func Main() {{ }} }}");
        Assert.Contains(errors, e => e.Contains("has no meaning on", StringComparison.Ordinal));
    }

    #endregion
}
