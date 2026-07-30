namespace Appa.Tests;

/// <summary>
/// Compiles the emitted C the way a user would rather than the way the rest of the suite does.
///
/// Everything else here compiles at the default -O0 with whichever compiler is first on PATH.
/// That combination hid three defects at once: gcc only reports -Wfree-nonheap-object once
/// inlining has run, so it needs -O1 or better, and clang rejects two constructs gcc accepts at
/// any level. A release build with warnings as errors is an ordinary thing to ask for, and until
/// this test existed nothing performed one.
/// </summary>
public class CPortabilityTests
{
    private const string Warnings =
        "-Wall -Wextra -Werror -Wno-unused-parameter -Wno-unused-function " +
        "-Wno-unused-variable -Wno-missing-field-initializers";

    /// <summary>
    /// Exercises the constructs the three known defects lived in: a string literal (a static object
    /// ARC must never free), comparisons in every condition position, and the terminal control
    /// surface, whose host-side declarations are never called but must still be prototyped.
    /// </summary>
    private const string Program = """
        import Console;
        import List;
        import String;

        realm userspace {
            entry func Main() {
                let String greeting = "hello";
                let List[int] xs = new List[int]();
                let int i = 0;
                while (i < 5) {
                    if (i == 3) { xs.Add(i * 2); }
                    i = i + 1;
                }
                let int total = 0;
                for v in xs { if (v != 1) { total = total + v; } }
                Console.PrintLine(greeting + " world");
                Console.PrintLine($"count={xs.Length()} total={total}");
            }
        }
        """;

    public static TheoryData<string, string> Matrix()
    {
        var data = new TheoryData<string, string>();
        foreach (var cc in (string[])["gcc", "clang"])
            foreach (var opt in (string[])["-O0", "-O1", "-O2", "-Os"])
                data.Add(cc, opt);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void EmittedCCompilesCleanUnderWerror(string cc, string opt)
    {
        var gata = HostedRun.FindGataCheckout();
        if (gata == null) return;                       // not a source checkout
        if (HostedRun.FindCompiler() == null) return;   // no host toolchain

        using var work = TempDir.Create("appa-portability-");
        Directory.CreateDirectory(work.Combine("src"));
        File.WriteAllText(work.Combine("src", "main.g"), Program);
        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), work.Combine("env.g"));
        File.WriteAllText(work.Combine("p.gconf"), """
            <appa>
                <ProjectName>p</ProjectName>
                <TargetBackend>Hosted</TargetBackend>
                <BuildMode>Debug</BuildMode>
            </appa>
            """);

        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (buildCode, buildOut) = HostedRun.Run("dotnet",
            $"\"{appaDll}\" build \"{work.Path}\" --stdlib \"{Path.Combine(gata, "libgata")}\"",
            work.Path);
        Assert.True(buildCode == 0, $"appa build failed:\n{buildOut}");

        var outDir = work.Combine("transpilation");

        // A compiler that is not installed is not a failure; one that is must be satisfied.
        var (probe, _) = HostedRun.Run(cc, "--version", outDir);
        if (probe != 0) return;

        var (code, output) = HostedRun.Run(cc,
            $"-std=c11 -I. {opt} {Warnings} -c program.c -o /dev/null", outDir);
        Assert.True(code == 0, $"{cc} {opt} rejected the emitted C:\n{output}");
    }
}
