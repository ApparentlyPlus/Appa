namespace Appa.Tests;

/// <summary>
/// The messages the CLI gives for invocations that cannot proceed.
/// </summary>
public class CliDiagnosticsTests
{
    /// <summary>
    /// Creates a project tree with an environment and an entry, and optionally a .gconf.
    /// </summary>
    private static TempDir Project(string? gconf)
    {
        string? gata = HostedRun.FindGataCheckout();
        Assert.NotNull(gata);

        var work = TempDir.Create("appa-cli-diag-");
        Directory.CreateDirectory(work.Combine("src"));
        File.WriteAllText(work.Combine("src", "main.g"), "realm userspace { entry func Main() { } }");
        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), work.Combine("env.g"));
        if (gconf != null) File.WriteAllText(work.Combine("p.gconf"), gconf);
        return work;
    }

    private static string Run(string args, string cwd)
    {
        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (_, output) = HostedRun.Run("dotnet", $"\"{appaDll}\" {args}", cwd);
        return output;
    }

    /// <summary>
    /// Half of the --env/--entry pair is the common slip
    /// </summary>
    [Theory]
    [InlineData("--entry src/main.g", "--entry was given without --env")]
    [InlineData("--env env.g", "--env was given without --entry")]
    public void HalfOfTheLooseFilePairNamesTheMissingHalf(string flag, string expected)
    {
        using var work = Project(gconf: null);
        Assert.Contains(expected, Run($"check {flag}", work.Path));
    }

    /// <summary>
    /// Both flags, but to 'build', where they additionally need --pure-transpile. Telling the reader
    /// to pass --env and --entry here would be telling them to do what they did.
    /// </summary>
    [Fact]
    public void BothLooseFileFlagsWithoutPureTranspileSaysWhatIsMissing()
    {
        using var work = Project(gconf: null);
        string got = Run("build --env env.g --entry src/main.g", work.Path);
        Assert.Contains("--pure-transpile", got);
        Assert.DoesNotContain("run 'appa init'", got);
    }

    /// <summary>
    /// A path that exists as neither a directory nor a file was read as a .gconf, so a mistyped
    /// project name came back as "cannot read &lt;name&gt;: Could not find file".
    /// </summary>
    [Fact]
    public void AMissingProjectPathIsReportedAsThePathGiven()
    {
        using var work = Project(gconf: null);
        string got = Run("check no-such-project", work.Path);
        Assert.Contains("'no-such-project' does not exist", got);
        Assert.DoesNotContain("cannot read", got);
    }

    /// <summary>
    /// With no flags and no project, the original advice is still the right advice.
    /// </summary>
    [Fact]
    public void NoProjectAndNoFlagsStillPointsAtInit()
    {
        using var work = Project(gconf: null);
        Assert.Contains("run 'appa init'", Run("check", work.Path));
    }

    /// <summary>
    /// Malformed manifests, each naming what is wrong with it rather than surfacing an XML exception
    /// on its own.
    /// </summary>
    [Theory]
    [InlineData("<appa><TargetBackend>Hosted</TargetBackend>", "cannot read")]
    [InlineData("<notappa></notappa>", "must have an <appa> root")]
    [InlineData("<appa><TargetBackend>Windows</TargetBackend></appa>", "is not a valid <TargetBackend>")]
    [InlineData("<appa><BuildMode>1</BuildMode></appa>", "is not a valid <BuildMode>")]
    public void AMalformedManifestSaysWhatIsWrongWithIt(string gconf, string expected)
    {
        using var work = Project(gconf);
        Assert.Contains(expected, Run("check .", work.Path));
    }

    /// <summary>
    /// A --werror run that found only warnings exited non-zero after printing "2 warnings", with
    /// nothing on screen connecting the failure to the flag that caused it.
    /// </summary>
    [Fact]
    public void WerrorSaysWhyABuildWithNoErrorsFailed()
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = Project("<appa><TargetBackend>Hosted</TargetBackend></appa>");
        File.WriteAllText(work.Combine("src", "main.g"),
            "realm userspace { entry func Main() { let int unusedLocal = 1; } }");

        string stdlib = $"--stdlib \"{Path.Combine(gata, "libgata")}\"";
        Assert.Contains("--werror", Run($"check . {stdlib} --werror", work.Path));
        // Without the flag the same source is a clean check, so the message is about --werror alone.
        Assert.DoesNotContain("--werror:", Run($"check . {stdlib}", work.Path));
    }

    /// <summary>
    /// The project 'appa init' writes has to compile. It did not: the starter main.g still used the
    /// pre-realm 'kernel { }' and 'user { }' block syntax, so the very first thing a new user runs
    /// after creating a project was a syntax error in code they had not written. The book was swept
    /// for that spelling; the template it was generated alongside was not.
    /// </summary>
    [Fact]
    public void AFreshlyInitialisedProjectChecksCleanly()
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = TempDir.Create("appa-init-");
        string made = Run("init demo", work.Path);
        Assert.Contains("Created", made);

        string got = Run($"check demo --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);
        Assert.DoesNotContain("error", got);
    }

    /// <summary>
    /// A well-formed project still checks cleanly - the other half of the test, without which
    /// rejecting everything would pass.
    /// </summary>
    [Fact]
    public void AWellFormedProjectChecks()
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = Project("<appa><TargetBackend>Hosted</TargetBackend></appa>");
        string got = Run($"check . --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);
        Assert.DoesNotContain("error", got);
    }

    #region Reference cycles

    // The same cycle, written at each of the three levels a class can be declared at.
    private const string CycleAtRoot = """
        import Console;

        class Left  { public Right r; func _init() { } }
        class Right { public Left  l; func _init() { } }

        realm userspace {
            entry func Main() {
                let Left x = new Left();
                x.r = new Right();
                x.r.l = x;
            }
        }
        """;

    private const string CycleInRealm = """
        import Console;

        realm userspace {
            class Left  { public Right r; func _init() { } }
            class Right { public Left  l; func _init() { } }

            entry func Main() {
                let Left x = new Left();
                x.r = new Right();
                x.r.l = x;
            }
        }
        """;

    private const string CycleInProcess = """
        import Console;

        realm userspace {
            background process P {
                class Left  { public Right r; func _init() { } }
                class Right { public Left  l; func _init() { } }

                thread T {
                    entry func Run() {
                        let Left x = new Left();
                        x.r = new Right();
                        x.r.l = x;
                    }
                }
            }
            entry func Main() { }
        }
        """;

    /// <summary>
    /// Writes a project whose classes hold each other in a reference cycle.
    /// </summary>
    private static TempDir CycleProject(string source = CycleAtRoot)
    {
        string? gata = HostedRun.FindGataCheckout();
        Assert.NotNull(gata);

        var work = TempDir.Create("appa-cycle-");
        Directory.CreateDirectory(work.Combine("src"));
        File.WriteAllText(work.Combine("src", "main.g"), source);
        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), work.Combine("env.g"));
        File.WriteAllText(work.Combine("p.gconf"), "<appa><TargetBackend>Hosted</TargetBackend></appa>");
        return work;
    }

    /// <summary>
    /// The cycle warning is a diagnostic like any other: a code to look up, and a file and line to
    /// go to. It used to print itself straight to the console, so it had neither.
    /// </summary>
    [Fact]
    public void AReferenceCycleIsReportedWithACodeAndALocation()
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = CycleProject();
        string got = Run($"check . --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);

        Assert.Contains("warning[G101]", got);
        Assert.Contains("'Left', 'Right'", got);
        // A location, not just a message: the file, and a line inside the source rather than 0.
        Assert.Contains("main.g:", got);
        Assert.DoesNotContain("main.g:0:", got);
    }

    /// <summary>
    /// And --werror promotes it, which is the part that was actually broken: a build asked to treat
    /// warnings as errors reported a guaranteed leak and then exited 0, because this warning never
    /// went through the diagnostic bag and so was never counted.
    /// </summary>
    [Fact]
    public void WerrorPromotesTheReferenceCycleWarning()
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = CycleProject();
        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (code, output) = HostedRun.Run("dotnet",
            $"\"{appaDll}\" check . --stdlib \"{Path.Combine(gata, "libgata")}\" --werror", work.Path);

        Assert.Contains("G101", output);
        Assert.True(code != 0, $"--werror let a reference cycle through with exit 0:\n{output}");

        // Without --werror the same project still builds, so the promotion is what changed and not
        // the diagnosis becoming an error outright.
        var (plainCode, _) = HostedRun.Run("dotnet",
            $"\"{appaDll}\" check . --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);
        Assert.Equal(0, plainCode);
    }

    /// <summary>
    /// A cycle among classes declared inside a realm or a process is the same leak as one at file
    /// top level, and must be reported the same way.
    /// </summary>
    [Theory]
    [InlineData(CycleAtRoot, "'Left', 'Right'")]
    [InlineData(CycleInRealm, "'userspace.Left', 'userspace.Right'")]
    [InlineData(CycleInProcess, "'userspace.P.Left', 'userspace.P.Right'")]
    public void AReferenceCycleIsReportedAtEveryScopeItCanBeDeclaredIn(string source, string names)
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = CycleProject(source);
        string got = Run($"check . --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);

        Assert.Contains("warning[G101]", got);
        Assert.Contains(names, got);
        Assert.Contains("main.g:", got);
        Assert.DoesNotContain("main.g:0:", got);
        Assert.DoesNotContain("<program>", got);
    }

    /// <summary>
    /// And --werror promotes it at every scope. This is the half that actually shipped broken: the
    /// scoped cases exited 0 on a guaranteed leak, so a build gating on warnings passed one.
    /// </summary>
    [Theory]
    [InlineData(CycleInRealm)]
    [InlineData(CycleInProcess)]
    public void WerrorPromotesAScopedReferenceCycle(string source)
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = CycleProject(source);
        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (code, output) = HostedRun.Run("dotnet",
            $"\"{appaDll}\" check . --stdlib \"{Path.Combine(gata, "libgata")}\" --werror", work.Path);

        Assert.Contains("G101", output);
        Assert.True(code != 0, $"--werror let a scoped reference cycle through with exit 0:\n{output}");
    }

    /// <summary>
    /// A cycle closed through a library generic must be reported against the author's file, which
    /// is the only end they can cut.
    /// </summary>
    [Fact]
    public void ACycleThroughALibraryGenericIsReportedInTheAuthorsFile()
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var stdlib = TempDir.Create("appa-stdlib-");
        foreach (var f in Directory.GetFiles(Path.Combine(gata, "libgata"), "*.g"))
            File.Copy(f, stdlib.Combine(Path.GetFileName(f)));

        File.WriteAllText(stdlib.Combine("Crate.g"),
            "import Runtime;\nimport Mem;\n\nclass Crate[T] { public T v; func _init() { } }");

        using var work = CycleProject("""
            import Crate;

            class Node { public Crate[Node] c; func _init() { } }

            realm userspace {
                entry func Main() {
                    let Node n = new Node();
                    n.c = new Crate[Node]();
                    n.c.v = n;
                }
            }
            """);

        string got = Run($"check . --stdlib \"{stdlib.Path}\"", work.Path);

        Assert.Contains("warning[G101]", got);
        Assert.Contains("main.g:", got);
        Assert.DoesNotContain("Crate.g:", got);

        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (code, output) = HostedRun.Run("dotnet",
            $"\"{appaDll}\" check . --stdlib \"{stdlib.Path}\" --werror", work.Path);
        Assert.True(code != 0, $"--werror let a library-closed cycle through with exit 0:\n{output}");
    }

    /// <summary>
    /// The negative case at each scope: classes that do not form a cycle stay silent. Without it a
    /// report that fired on every class would satisfy everything above.
    /// </summary>
    [Theory]
    [InlineData("class Solo { public int n; func _init() { } }", "")]
    [InlineData("", "class Solo { public int n; func _init() { } }")]
    public void ScopedClassesWithoutACycleStaySilent(string atRoot, string inRealm)
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = CycleProject($$"""
            import Console;

            {{atRoot}}

            realm userspace {
                {{inRealm}}
                entry func Main() { let Solo s = new Solo(); let n = s.n; }
            }
            """);
        string got = Run($"check . --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);

        Assert.DoesNotContain("G101", got);
    }

    #endregion

    #region Manual reference counting is unsafe-only

    /// <summary>
    /// Builds a project whose entry function does <paramref name="body"/> to a class reference,
    /// against the real libgata - so 'retain'/'release' are the standard library's own intrinsics
    /// and the operand is a managed reference rather than a raw pointer.
    /// </summary>
    private static string RunArcBody(string body, string gata)
    {
        using var work = TempDir.Create("appa-arc-unsafe-");
        Directory.CreateDirectory(work.Combine("src"));
        File.WriteAllText(work.Combine("src", "main.g"), $$"""
            import Console;
            import String;
            import Mem;

            class Item { public int v; func _init(int v) { self.v = v; } }

            realm userspace {
                entry func Main() {
                    let Item a = new Item(1);
                    {{body}}
                    Console.PrintLine("ok");
                }
            }
            """);
        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), work.Combine("env.g"));
        File.WriteAllText(work.Combine("p.gconf"), "<appa><TargetBackend>Hosted</TargetBackend></appa>");
        return Run($"check . --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);
    }

    /// <summary>
    /// Manual reference counting outside 'unsafe' must not compile, in any of the shapes someone
    /// would reach for it.
    /// </summary>
    [Theory]
    [InlineData("release(a);")]
    [InlineData("retain(a);")]
    [InlineData("let Item b = retain(a); Console.PrintLine($\"{b.v}\");")]
    [InlineData("if (a.v == 1) { release(a); }")]
    [InlineData("for (let int i = 0; i < 2; i++) { release(a); }")]
    [InlineData("defer { release(a); }")]
    [InlineData("release(retain(a));")]
    [InlineData("try { release(a); } catch { }")]
    [InlineData("unsafe { let Item q = retain(a); } release(a);")]
    public void ManualRefCountingOnAClassReferenceOutsideUnsafeIsRejected(string body)
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        Assert.Contains("error[G033]", RunArcBody(body, gata));
    }

    /// <summary>
    /// The other half: inside 'unsafe' the same calls are accepted. Without this, a rule that
    /// rejected everything would satisfy the theory above.
    /// </summary>
    [Theory]
    [InlineData("unsafe { let Item b = retain(a); release(b); }")]
    [InlineData("unsafe { if (a.v == 1) { release(retain(a)); } }")]
    public void ManualRefCountingInsideUnsafeIsAccepted(string body)
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        Assert.DoesNotContain("error", RunArcBody(body, gata));
    }

    #endregion

    #region String-keyed containers

    /// <summary>
    /// A container keyed on 'String' points at the sibling type that exists for it.
    /// </summary>
    [Theory]
    [InlineData("Map[String, int]", "StringMap[int]")]
    [InlineData("Set[String]", "StringSet")]
    public void AStringKeyedContainerSuggestsItsSibling(string written, string suggested)
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = TempDir.Create("appa-strkey-");
        Directory.CreateDirectory(work.Combine("src"));
        File.WriteAllText(work.Combine("src", "main.g"), $$"""
            import Console;
            import String;
            import Map;
            import Set;

            realm userspace {
                entry func Main() { let {{written}} c = new {{written}}(); }
            }
            """);
        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), work.Combine("env.g"));
        File.WriteAllText(work.Combine("p.gconf"), "<appa><TargetBackend>Hosted</TargetBackend></appa>");

        string got = Run($"check . --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);

        Assert.Contains("G028", got);
        Assert.Contains($"use '{suggested}'", got);
    }

    #endregion

    #region Library warnings

    /// <summary>
    /// A generic container over the author's own union, which is what drags libgata's internals
    /// into the analyses: every retain into List's raw storage, and its generated '=='.
    /// </summary>
    private static TempDir LibraryWarningProject()
    {
        string? gata = HostedRun.FindGataCheckout();
        Assert.NotNull(gata);

        var work = TempDir.Create("appa-libwarn-");
        Directory.CreateDirectory(work.Combine("src"));
        File.WriteAllText(work.Combine("src", "main.g"), """
            import Console;
            import String;
            import List;

            class K { public int id; func _init(int i) { self.id = i; } }
            union U { Nothing, Ref(K k) }

            realm userspace {
                entry func Main() {
                    let List[U] xs = new List[U]();
                    xs.Add(U.Ref(new K(1)));
                    Console.PrintLine($"n={xs.Length()}");
                }
            }
            """);
        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), work.Combine("env.g"));
        File.WriteAllText(work.Combine("p.gconf"), "<appa><TargetBackend>Hosted</TargetBackend></appa>");
        return work;
    }

    /// <summary>
    /// Warnings inside libgata are the library's business, not the author's.
    /// </summary>
    [Fact]
    public void WarningsInsideLibgataAreNotReportedAgainstTheAuthor()
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = LibraryWarningProject();
        string got = Run($"check . --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);

        Assert.DoesNotContain("List.g:", got);
        Assert.DoesNotContain("Map.g:", got);
        Assert.DoesNotContain("error", got);
    }

    /// <summary>
    /// And they no longer gate --werror.
    /// </summary>
    [Fact]
    public void WerrorIgnoresWarningsInsideLibgata()
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null) return;

        using var work = LibraryWarningProject();
        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (code, output) = HostedRun.Run("dotnet",
            $"\"{appaDll}\" check . --stdlib \"{Path.Combine(gata, "libgata")}\" --werror", work.Path);

        Assert.True(code == 0, $"--werror failed on libgata's own warnings:\n{output}");
    }

    #endregion
}
