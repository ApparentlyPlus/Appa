namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// Compiles the emitted C for every torture-corpus program that checks clean, and asserts
/// the host C compiler accepts it.
///
/// This is the strongest robustness check in the suite and the one that finds what nobody
/// thought to look for. The hand-written assertions in TortureTests can only catch invalid
/// output someone predicted -- an empty enum body, an unbalanced brace. gcc catches the rest
/// by construction: a struct that contains itself, a typedef used before it is defined, a
/// subscript that is not an integer, a member load through a non-pointer, a function emitted
/// twice under one name. Every one of those was a real defect found here rather than reasoned
/// about, and each is now rejected in Gata's own terms before emission.
///
/// Skips when no C compiler is installed, so the suite still runs on a bare machine.
/// </summary>
public class EmittedCCompilesTests
{
    /// <summary>
    /// A stand-in environment. Corpus cases are single files that import no libgata, so they
    /// declare no realm and no floor: without this Layout.Compose emits only shared.h and
    /// there is nothing to compile. The preamble supplies what a real environment would --
    /// the libc headers, the include of shared.h, and definitions for the two ARC roles
    /// Ownership resolves silently when libgata is absent.
    /// </summary>
    private const string StubEnvironment = """
        @preamble(kernel) native {
        #include <stdint.h>
        #include <stddef.h>
        #include <stdbool.h>
        #include "shared.h"
        typedef struct gata_String gata_String;
        static void* gata_MISSING_retain(void* p) { return p; }
        static void gata_MISSING_release(void* p) { (void)p; }
        }

        """;

    /// <summary>
    /// True if a C diagnostic is an artifact of the stub environment rather than a compiler
    /// defect.
    ///
    /// The only such artifact is libgata's String. A single-file corpus case imports no
    /// stdlib, so String has no definition to emit, and anything that touches a string
    /// literal or the String type produces C that names an incomplete type. Matching on the
    /// diagnostic text rather than on case names keeps every other case in scope, including
    /// the ones in those same families that have nothing to do with strings.
    /// </summary>
    private static bool IsStubArtifact(string diagnostic) =>
        diagnostic.Contains("gata_String", StringComparison.Ordinal);

    /// <summary>Locates a usable host C compiler, or null.</summary>
    private static string? FindCompiler()
    {
        foreach (var exe in (string[])["cc", "gcc", "clang"])
        {
            try
            {
                var psi = new ProcessStartInfo(exe, "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                if (p == null) continue;
                p.WaitForExit(5000);
                if (p.ExitCode == 0) return exe;
            }
            catch { /* not on PATH; try the next one */ }
        }
        return null;
    }

    [Fact]
    public void EveryCleanCorpusProgramEmitsCompilableC()
    {
        var cc = FindCompiler();
        if (cc == null)
        {
            Assert.Skip("no host C compiler (cc/gcc/clang) found; skipping emitted-C compilation");
            return;
        }

        var work = Directory.CreateTempSubdirectory("appa-torture-c-").FullName;
        var failures = new List<string>();
        int compiled = 0;

        try
        {
            foreach (var c in TortureCorpus.All)
            {
                var src = StubEnvironment + c.Source;
                var files = FrontEnd(src);
                if (files == null) continue;

                var dir = Path.Combine(work, "u" + compiled);
                Directory.CreateDirectory(dir);
                foreach (var f in files) File.WriteAllText(Path.Combine(dir, f.Name), f.Content);

                foreach (var unit in files.Where(f => f.Name.EndsWith(".c", StringComparison.Ordinal)))
                {
                    compiled++;
                    var psi = new ProcessStartInfo(cc, $"-fsyntax-only -std=c11 -I. {unit.Name}")
                    { WorkingDirectory = dir, RedirectStandardError = true, UseShellExecute = false };
                    using var p = Process.Start(psi)!;
                    var err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0) continue;

                    var first = err.Split('\n').FirstOrDefault(l => l.Contains(": error:", StringComparison.Ordinal))
                                ?? err.Split('\n').FirstOrDefault() ?? "<no diagnostic>";
                    if (IsStubArtifact(err)) continue;
                    failures.Add($"[{c.Name}] {unit.Name}: {first.Trim()}\n{c.Source}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }

        Assert.True(compiled > 0, "no translation units were produced; the stub environment stopped working");
        if (failures.Count == 0) return;

        var shown = string.Join("\n\n", failures.Take(20));
        var more = failures.Count > 20 ? $"\n\n... and {failures.Count - 20} more" : "";
        Assert.Fail($"{failures.Count} of {compiled} emitted translation units did not compile:\n\n{shown}{more}");
    }

    /// <summary>
    /// Runs the front end over a source and returns the emitted files, or null if the program
    /// was rejected, crashed, or produced nothing. Only programs the compiler accepts are
    /// interesting here: a rejected one was never going to be emitted.
    /// </summary>
    private static IReadOnlyList<OutputFile>? FrontEnd(string src)
    {
        const string path = "<torture>";
        try
        {
            var sources = new SourceSet();
            sources.Add(path, src);
            var diag = new DiagnosticBag(sources);

            Program prog;
            try { prog = SingleFileCompile.Parse(src); }
            catch (ParseException) { return null; }

            var programs = new List<(string path, Program prog)> { (path, prog) };
            var visible = new Dictionary<string, HashSet<string>> { [path] = [path] };
            var (module, _, _) = Pipeline.BuildModule(programs, visible, Mode.Debug, diag);
            Pipeline.ValidateIntrinsics(module, diag);
            Pipeline.ValidateStructure(programs, null, diag);
            if (diag.HasErrors) return null;

            return Layout.Compose(new Emitter(module, diag).Build(), module.Symbols);
        }
        catch
        {
            return null; // TortureTests.NoCorpusCaseCrashesTheCompiler owns crashes
        }
    }
}
