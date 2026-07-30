namespace Appa.Tests;

/// <summary>
/// The messages the CLI gives for invocations that cannot proceed. This surface is reached by every
/// user before any Gata code is, and it had been checked by nothing - a wrong argument produced a
/// message about a file the reader never mentioned, or advised the very flag they had just passed.
///
/// Driven through the real executable rather than the resolver, because the failure is the process
/// exiting: the paths under test call Environment.Exit.
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
    /// Half of the --env/--entry pair is the common slip, and the old message answered it by naming
    /// both flags - including the one already on the command line.
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
}
