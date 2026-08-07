namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// End-to-end regression over the real standard library with nothing but a host C compiler. This is
/// what catches an over-eager new diagnostic: a rule rejecting nonsense is only correct if libgata
/// still passes it, and libgata exercises everything harder.
/// </summary>
public class HostedEndToEndTests
{
    /// <summary>
    /// Finds the sibling Gata checkout by walking up from the test binary, looking for a directory
    /// holding both libgata/ and envs/. Returns null if this is not a source checkout.
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

    /// <summary>
    /// Locates a usable host C compiler, or null.
    /// </summary>
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
    /// A program touching the parts of libgata most likely to break: generic containers, counted
    /// strings, interpolation, fixed arrays, an unsafe pointer round trip. Its output is asserted
    /// exactly, so a miscompile is a wrong answer, not just a build failure.
    /// </summary>
    private const string ProgramSource = """
        import Console;
        import List;
        import Map;
        import Set;
        import Optional;
        import String;
        import Math;

        int func Twice(int n) { return n * 2; }
        class Cfg { public int w; func _init() { self.w = 9; } }
        int func Widen(Cfg c) { return c.w; }
        int func OuterTwice(int n) { return Twice(n); }

        union Note { Blank, Titled(String t), Numbered(int v) }

        int func NoteWeight(Note n) {
            match (n) {
                case Blank { return 0; }
                case Titled(t) { return t.Length(); }
                case Numbered(v) { return v; }
            }
        }

        class Counter {
            int n;
            func _init() { self.n = 0; }
            public void func Bump() { self.n = self.n + 1; }
            public int func Value() { return self.n; }
        }

        realm userspace {
            @shadows int func Twice(int n) { return n * 3; }
            @shadows class Cfg { public int a; }
            @shadows int func Widen(Cfg c) { return c.a; }

            entry func Main() {
                let int Cfg = 5;

                let List[int] xs = new List[int]();
                xs.Add(3);
                xs.Add(Twice(4));

                let Map[int, int] m = new Map[int, int]();
                m.Put(5, 1);

                let Counter c = new Counter();
                for x in xs { c.Bump(); }

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

                let Note n1 = Note.Titled("hi");
                let Note n2 = Note.Numbered(7);
                let String label = "?";
                let int number = 0;
                match (n1) { case Titled(t) { label = t; } case Numbered(v) { number = v; } case Blank { } }
                match (n2) { case Titled(t) { label = t; } case Numbered(v) { number = v; } case Blank { } }

                // Single-probe lookups: one hash and one scan instead of Has-then-Get.
                let int got = 0;
                let bool hit = m.TryGet(5, ref got);
                let bool miss = m.TryGet(99, ref got);

                let StringMap[int] sm = new StringMap[int]();
                sm.Put("k", 4);
                let int sgot = 0;
                let bool shit = sm.TryGet("k", ref sgot);

                let Set[int] set = new Set[int]();
                let bool fresh = set.AddNew(1);
                let bool dupe = set.AddNew(1);
                
                m.Put(7, 0);
                let int found = ValueOr(m.Find(5), -1);
                let int storedZero = ValueOr(m.Find(7), -1);
                let int absent = ValueOr(m.Find(99), -1);
                let bool some = IsSome(m.Find(5));
                let bool none = IsNone(m.Find(99));
                let int firstEl = ValueOr(xs.At(0), -1);
                let int oob = ValueOr(xs.At(99), -1);

                let Map[int, Counter] byId = new Map[int, Counter]();
                byId.Put(1, c);

                Console.PrintLine($"opt={found}{storedZero}{absent} {some}{none} at={firstEl}{oob}");
                Console.PrintLine($"probe={hit}{miss}{shit} v={got}{sgot} set={fresh}{dupe} " +
                                  $"or={m.GetOr(5, -1)}{m.GetOr(99, -1)} vals={byId.Values().Length()}");
                Console.PrintLine($"note={label}{number} weight={NoteWeight(n1)}{NoteWeight(n2)}");
                Console.PrintLine($"len={xs.Length()} map={m.Length()} bumps={c.Value()}");
                Console.PrintLine($"arr={arr[2]} deref={deref} abs={Math.Abs(-9)}");
                Console.PrintLine($"generic={cs.Length()} first={cs.Get(0).Value()}");
                Console.PrintLine($"shadow={Twice(2)} outer={OuterTwice(2)} xs1={xs.Get(1)} local={Cfg}");
                Console.PrintLine($"qual={::Twice(2)}{userspace.Twice(2)} qt={::Widen(new ::Cfg())}");
            }
        }
        """;

    private const string ExpectedOutput =
        "opt=10-1 truetrue at=3-1\n" +
        "probe=truefalsetrue v=14 set=truefalse or=1-1 vals=1\n" +
        "note=hi7 weight=27\n" +
        "len=2 map=2 bumps=2\n" +
        "arr=7 deref=42 abs=9\n" +
        "generic=2 first=2\n" +
        "shadow=6 outer=4 xs1=12 local=5\n" +
        "qual=46 qt=9\n";

    [Fact]
    public void StdlibProgramRuns()
    {
        var gata = FindGataCheckout();
        if (gata == null) { Assert.Skip("no sibling Gata checkout found; skipping hosted end-to-end"); return; }

        var cc = FindCompiler();
        if (cc == null) { Assert.Skip("no host C compiler (cc/gcc/clang) found; skipping hosted end-to-end"); return; }

        using var work = Scratch.Create("appa-hosted-e2e-");
        Directory.CreateDirectory(work.Combine("src"));
        File.WriteAllText(work.Combine("src", "main.g"), ProgramSource);
        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), work.Combine("env.g"));
        File.WriteAllText(work.Combine("host.gconf"), """
            <appa>
                <ProjectName>host</ProjectName>
                <TargetBackend>Hosted</TargetBackend>
                <BuildMode>Debug</BuildMode>
            </appa>
            """);

        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (buildCode, buildOut) = Run("dotnet",
            $"\"{appaDll}\" build \"{work.Path}\" --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);
        Assert.True(buildCode == 0, $"appa build failed:\n{buildOut}");

        var outDir = work.Combine("transpilation");
        Assert.True(File.Exists(Path.Combine(outDir, "program.c")),
            $"expected transpilation/program.c, got: {string.Join(", ", Directory.GetFiles(outDir).Select(Path.GetFileName))}");

        var exe = work.Combine("prog");
        var (ccCode, ccOut) = Run(cc,
            $"-std=c11 -I. -Wall -Wextra -Werror -Wno-unused-parameter -Wno-unused-function " +
            $"-Wno-unused-variable -Wno-missing-field-initializers -o \"{exe}\" program.c -lm", outDir);
        Assert.True(ccCode == 0, $"{cc} rejected the emitted C:\n{ccOut}");

        var (runCode, runOut) = Run(exe, "", work.Path);
        Assert.True(runCode == 0, $"the compiled program exited {runCode}:\n{runOut}");
        Assert.Equal(ExpectedOutput, runOut.Replace("\r\n", "\n"));
    }

    /// <summary>
    /// Runs a process to completion, returning its exit code and combined output.
    /// </summary>
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
