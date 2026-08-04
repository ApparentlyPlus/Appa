namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// Compiles the emitted C for every corpus program that checks clean. The strongest check here:
/// hand-written assertions only catch output someone predicted, while gcc catches a self-containing
/// struct or a non-integer subscript by construction.
/// </summary>
public class EmittedCCompilesTests
{
    /// <summary>
    /// A stand-in environment. Corpus cases import no libgata and so declare no realm, leaving
    /// Layout.Compose nothing but shared.h to emit. This supplies the libc headers, the shared.h
    /// include, the two ARC roles Ownership resolves silently, and the floor functions 'debug' and
    /// 'panic' lower to.
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
        static void _env_dbg(const char* m) { (void)m; }
        static void _env_panic(const char* m) { (void)m; }
        }

        """;

    /// <summary>
    /// True if a C diagnostic is an artifact of the stub environment rather than a defect - only
    /// libgata's String, since a corpus case imports no stdlib. Matching on the diagnostic text
    /// rather than case names keeps unrelated cases in those families in scope.
    /// </summary>
    private static bool IsStubArtifact(string diagnostic) =>
        diagnostic.Contains("gata_String", StringComparison.Ordinal);

    /// <summary>
    /// Locates a usable host C compiler, or null.
    /// </summary>
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
    public void CleanCorpusEmitsCompilableC()
    {
        var cc = FindCompiler();
        if (cc == null)
        {
            Assert.Skip("no host C compiler (cc/gcc/clang) found; skipping emitted-C compilation");
            return;
        }

        using var work = TempDir.Create("appa-torture-c-");
        var failures = new List<string>();
        int compiled = 0;

        foreach (var c in TortureCorpus.All)
        {
            var src = StubEnvironment + c.Source;
            var files = FrontEnd(src);
            if (files == null) continue;

            var dir = work.Combine("u" + compiled);
            Directory.CreateDirectory(dir);
            foreach (var f in files) File.WriteAllText(Path.Combine(dir, f.Name), f.Content);

            foreach (var unit in files.Where(f => f.Name.EndsWith(".c", StringComparison.Ordinal)))
            {
                compiled++;
                var psi = new ProcessStartInfo(cc,
                    $"-c -std=c11 -Werror=return-type -I. -o {(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")} {unit.Name}")
                { WorkingDirectory = dir, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0 || IsStubArtifact(err)) continue;

                var first = err.Split('\n').FirstOrDefault(l => l.Contains(": error:", StringComparison.Ordinal))
                            ?? err.Split('\n').FirstOrDefault() ?? "<no diagnostic>";
                failures.Add($"[{c.Name}] {unit.Name}: {first.Trim()}\n{c.Source}");
            }
        }

        Assert.True(compiled > 0, "no translation units were produced; the stub environment stopped working");
        if (failures.Count == 0) return;

        var shown = string.Join("\n\n", failures.Take(20));
        var more = failures.Count > 20 ? $"\n\n... and {failures.Count - 20} more" : "";
        Assert.Fail($"{failures.Count} of {compiled} emitted translation units did not compile:\n\n{shown}{more}");
    }

    /// <summary>
    /// Runs the front end over a source and returns the emitted files, or null if the program was
    /// rejected, crashed, or produced nothing. Only programs the compiler accepts are interesting
    /// here: a rejected one was never going to be emitted.
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
            return null;
        }
    }
}
