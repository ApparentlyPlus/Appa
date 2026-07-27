namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// End-to-end regression over the real standard library, without the GatOS cross toolchain.
///
/// BootTests already proves the GatOS path, but it needs an installed toolchain and QEMU and
/// skips on most machines, which leaves the everyday question unanswered: does a program that
/// actually uses libgata still compile and run? A Hosted build answers it with nothing but the
/// host C compiler - it transpiles the whole stdlib, hands the result to gcc with warnings as
/// errors, and runs the binary.
///
/// This is the check that catches an over-eager new diagnostic. A rule added to reject some
/// nonsense program is only correct if libgata itself still passes it, and libgata exercises
/// generics, ARC, operator overloading, unsafe pointers and native blocks far harder than any
/// synthetic corpus case does.
///
/// Skips when the Gata checkout or a C compiler is missing.
/// </summary>
public class HostedEndToEndTests
{
    /// <summary>
    /// Finds the sibling Gata checkout by walking up from the test binary, looking for a
    /// directory holding both libgata/ and envs/. Returns null if this is not a source
    /// checkout.
    /// </summary>
    private static string? FindGataCheckout()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Gata");
            if (Directory.Exists(Path.Combine(candidate, "libgata")) &&
                File.Exists(Path.Combine(candidate, "envs", "env.hosted.g")))
                return candidate;
        }
        return null;
    }

    /// <summary>Locates a usable host C compiler, or null.</summary>
    private static string? FindCompiler()
    {
        foreach (var exe in (string[])["cc", "gcc", "clang"])
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                if (p == null) continue;
                p.WaitForExit(5000);
                if (p.ExitCode == 0) return exe;
            }
            catch { /* not on PATH; try the next one */ }
        }
        return null;
    }

    /// <summary>
    /// A program that touches the parts of libgata most likely to break: generic containers,
    /// reference-counted strings, interpolation, fixed arrays, and an unsafe pointer round trip.
    /// Its output is asserted exactly, so a miscompile shows up as a wrong answer rather than
    /// only as a compile failure.
    /// </summary>
    private const string ProgramSource = """
        import Console;
        import List;
        import Map;
        import String;
        import Math;

        int func Twice(int n) { return n * 2; }

        class Counter {
            int n;
            func _init() { self.n = 0; }
            public void func Bump() { self.n = self.n + 1; }
            public int func Value() { return self.n; }
        }

        user {
            entry func Main() {
                let List[int] xs = new List[int]();
                xs.Add(3);
                xs.Add(Twice(4));

                let Map[int, int] m = new Map[int, int]();
                m.Put(5, 1);

                let Counter c = new Counter();
                for x in xs { c.Bump(); }

                // A library generic instantiated over a class declared in *this* file. The
                // stamped List[Counter] lands in List.g, which has never heard of Counter,
                // so this only resolves if the instance is given the requesting file's scope.
                let List[Counter] cs = new List[Counter]();
                cs.Add(c);
                cs.Add(new Counter());

                let [4]int arr = [0, 0, 0, 0];
                arr[2] = 7;

                let int deref = 0;
                unsafe {
                    let int n = 41;
                    let int* p = &n;
                    deref = *p + 1;
                }

                Console.PrintLine($"len={xs.Length()} map={m.Length()} bumps={c.Value()}");
                Console.PrintLine($"arr={arr[2]} deref={deref} abs={Math.Abs(-9)}");
                Console.PrintLine($"generic={cs.Length()} first={cs.Get(0).Value()}");
            }
        }
        """;

    private const string ExpectedOutput = "len=2 map=1 bumps=2\narr=7 deref=42 abs=9\ngeneric=2 first=2\n";

    [Fact]
    public void StdlibProgramTranspilesCompilesAndRuns()
    {
        var gata = FindGataCheckout();
        if (gata == null) { Assert.Skip("no sibling Gata checkout found; skipping hosted end-to-end"); return; }

        var cc = FindCompiler();
        if (cc == null) { Assert.Skip("no host C compiler (cc/gcc/clang) found; skipping hosted end-to-end"); return; }

        var work = Directory.CreateTempSubdirectory("appa-hosted-e2e-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(work, "src"));
            File.WriteAllText(Path.Combine(work, "src", "main.g"), ProgramSource);
            File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), Path.Combine(work, "env.g"));
            File.WriteAllText(Path.Combine(work, "host.gconf"), """
                <appa>
                    <ProjectName>host</ProjectName>
                    <TargetBackend>Hosted</TargetBackend>
                    <BuildMode>Debug</BuildMode>
                </appa>
                """);

            // Transpile through the real CLI so manifest handling and import resolution are
            // covered too, not just the parts SingleFileCompile reaches.
            var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
            var (buildCode, buildOut) = Run("dotnet",
                $"\"{appaDll}\" build \"{work}\" --stdlib \"{Path.Combine(gata, "libgata")}\"", work);
            Assert.True(buildCode == 0, $"appa build failed:\n{buildOut}");

            var outDir = Path.Combine(work, "transpilation");
            Assert.True(File.Exists(Path.Combine(outDir, "program.c")),
                $"expected transpilation/program.c, got: {string.Join(", ", Directory.GetFiles(outDir).Select(Path.GetFileName))}");

            // -Werror is the point: libgata's emitted C must be clean, not merely accepted.
            // A few warnings are about C style the emitter deliberately does not chase.
            var exe = Path.Combine(work, "prog");
            var (ccCode, ccOut) = Run(cc,
                $"-std=c11 -I. -Wall -Wextra -Werror -Wno-unused-parameter -Wno-unused-function " +
                $"-Wno-unused-variable -Wno-missing-field-initializers -o \"{exe}\" program.c -lm", outDir);
            Assert.True(ccCode == 0, $"{cc} rejected the emitted C:\n{ccOut}");

            var (runCode, runOut) = Run(exe, "", work);
            Assert.True(runCode == 0, $"the compiled program exited {runCode}:\n{runOut}");
            Assert.Equal(ExpectedOutput, runOut.Replace("\r\n", "\n"));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Runs a process to completion, returning its exit code and combined output.</summary>
    private static (int Code, string Output) Run(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return (p.ExitCode, stdout + stderr);
    }
}
