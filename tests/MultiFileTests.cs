namespace Appa.Tests;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Robustness suite for everything that only exists once a build spans more than one file:
/// import resolution and cycles, per-file visibility, cross-file name collisions, realm blocks
/// split across files, and the mangling that keeps two files' same-named private functions
/// apart.
///
/// None of it is reachable from a source string. Pipeline.Transpile follows imports by reading
/// them off disk, so every case here writes a real project directory and drives the same entry
/// point the CLI does.
///
/// The linking test is the multi-file counterpart of EmittedCCompilesTests: syntax-checking one
/// translation unit at a time cannot see a symbol defined twice across two of them, or declared
/// in a header and never defined. Only the linker can.
/// </summary>
public class MultiFileTests
{
    #region Harness

    /// <summary>The result of running the front end over a written-out project.</summary>
    private sealed record BuildResult(
        DiagnosticBag? Diag,
        IrModule? Module,
        IReadOnlyList<OutputFile>? Files,
        string? Crash);

    /// <summary>
    /// Writes a case's files into the given directory and runs the same front-end sequence
    /// Program.RunFrontEnd does, minus the toolchain. The caller owns the directory, so it can
    /// go on to compile what was emitted and still get cleanup from a single 'using'.
    /// </summary>
    private static BuildResult Build(MultiFileCase c, TempDir dir)
    {
        var work = dir.Path;
        try
        {
            bool hasEnv = c.Files.Any(f => f.Path == "env.g");
            foreach (var (path, content) in c.Files)
            {
                var full = Path.Combine(work, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            if (!hasEnv) File.WriteAllText(Path.Combine(work, "env.g"), MultiFileCorpus.DefaultEnv);

            var envPath = Path.Combine(work, "env.g");
            var entryPath = Path.Combine(work, "src", "main.g");
            // libgata is deliberately absent: these cases test the project's own import graph,
            // and a missing library module is itself one of the cases.
            var stdlib = Path.Combine(work, "no-libgata");

            var inputs = new List<string> { envPath, entryPath };
            var (programs, _, imports, diag) = Pipeline.Transpile(inputs, work, stdlib);
            var visible = Pipeline.VisibleModules(imports);
            var (module, _, _) = Pipeline.BuildModule(programs, visible, Mode.Debug, diag);

            Pipeline.ValidateEnvironment(programs, diag);
            Pipeline.ValidateIntrinsics(module, diag);
            Pipeline.ValidateStructure(programs, Target.Hosted, diag);

            if (diag.HasErrors) return new BuildResult(diag, module, null, null);
            var files = Layout.Compose(new Emitter(module, diag).Build(), module.Symbols);
            return new BuildResult(diag, module, files, null);
        }
        catch (Exception ex)
        {
            var frame = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "<no stack>";
            return new BuildResult(null, null, null,
                $"{ex.GetType().Name}: {ex.Message.Replace('\n', ' ')} @ {frame}");
        }
    }

    /// <summary>Renders a case's files for a failure message.</summary>
    private static string Describe(MultiFileCase c) =>
        string.Join("\n", c.Files.Select(f => $"--- {f.Path} ---\n{f.Content}"));

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

    /// <summary>Collects failure messages across a sweep and turns them into a single assertion.</summary>
    private sealed class Failures
    {
        private readonly List<string> _items = [];

        public void Add(string msg) => _items.Add(msg);

        public void Assert(string what)
        {
            if (_items.Count == 0) return;
            var shown = string.Join("\n\n", _items.Take(15));
            var more = _items.Count > 15 ? $"\n\n... and {_items.Count - 15} more" : "";
            Xunit.Assert.Fail($"{_items.Count} {what}:\n\n{shown}{more}");
        }
    }

    #endregion

    /// <summary>
    /// No project, however tangled its imports, makes the compiler throw. Cycles, self-imports,
    /// and files that fail to parse all have to come back as diagnostics.
    /// </summary>
    [Fact]
    public void NoMultiFileProjectCrashesTheCompiler()
    {
        var fails = new Failures();
        foreach (var c in MultiFileCorpus.All)
        {
            using var work = TempDir.Create("appa-multifile-");
            var r = Build(c, work);
            if (r.Crash != null) fails.Add($"[{c.Name}] {r.Crash}\n{Describe(c)}");
        }
        fails.Assert("multi-file projects crashed the compiler");
    }

    [Fact]
    public void MultiFileExpectationsHold()
    {
        var fails = new Failures();
        foreach (var c in MultiFileCorpus.All)
        {
            if (c.Expect == Expect.Any) continue;

            using var work = TempDir.Create("appa-multifile-");
            var r = Build(c, work);
            if (r.Crash != null) continue; // owned by NoMultiFileProjectCrashesTheCompiler

            var errors = r.Diag!.All.Where(d => d.Severity == Severity.Error).ToList();
            var got = errors.Count == 0 ? "no errors" : string.Join("; ", errors.Select(e => $"{e.Code} {e.Message}"));

            if (c.Expect == Expect.Rejected)
            {
                if (errors.Count == 0)
                    fails.Add($"[{c.Name}] expected an error, got none\n{Describe(c)}");
                else if (c.Code != null && !errors.Any(e => e.Code == c.Code))
                    fails.Add($"[{c.Name}] expected {c.Code}, got: {got}\n{Describe(c)}");
            }
            else if (errors.Count > 0)
            {
                fails.Add($"[{c.Name}] expected clean, got: {got}\n{Describe(c)}");
            }
        }
        fails.Assert("unmet multi-file expectations");
    }

    /// <summary>
    /// Every diagnostic a multi-file build produces must point at a file that took part in the
    /// build, with a span inside that file.
    ///
    /// Single-file cases cannot catch a wrong File on a Loc, because there is only one file to
    /// name. With several in play, a pass that reports against the wrong one -- easy to do when
    /// a symbol is declared in one file and used in another -- renders the caret under
    /// unrelated source, or under nothing at all.
    /// </summary>
    [Fact]
    public void MultiFileDiagnosticsPointAtRealSource()
    {
        var fails = new Failures();
        foreach (var c in MultiFileCorpus.All)
        {
            using var work = TempDir.Create("appa-multifile-");
            var r = Build(c, work);
            if (r.Crash != null) continue;

            foreach (var d in r.Diag!.All)
            {
                if (d.Loc.Span == TextSpan.None) continue; // build-level, not file-level
                if (string.IsNullOrEmpty(d.Loc.File)) continue;
                // Names the compiler uses for things with no file of their own.
                if (d.Loc.File is "<runtime>" or "<environment>") continue;

                if (!File.Exists(d.Loc.File))
                {
                    fails.Add($"[{c.Name}] {d.Code} points at '{d.Loc.File}', which is not a build file -- '{d.Message}'");
                    continue;
                }
                int len = new FileInfo(d.Loc.File).Length == 0 ? 0 : File.ReadAllText(d.Loc.File).Length;
                if (d.Loc.Span.Start < 0 || d.Loc.Span.Start + d.Loc.Span.Length > len)
                    fails.Add($"[{c.Name}] {d.Code} span [{d.Loc.Span.Start}..{d.Loc.Span.Start + d.Loc.Span.Length}] " +
                              $"outside {Path.GetFileName(d.Loc.File)} (length {len}) -- '{d.Message}'");
            }
        }
        fails.Assert("multi-file diagnostics pointing at unreal source");
    }

    /// <summary>
    /// Every project that builds clean must emit C that compiles *and links*.
    ///
    /// This is what single-translation-unit syntax checking cannot do. Two files each declaring
    /// a private function of the same name, one generic instantiated from two files, a class
    /// used across a realm boundary -- each of those emits fine in isolation and fails only when
    /// the objects are put together. Linking is the only check that sees it.
    /// </summary>
    [Fact]
    public void EmittedTranslationUnitsCompileAndLink()
    {
        var cc = FindCompiler();
        if (cc == null)
        {
            Assert.Skip("no host C compiler (cc/gcc/clang) found; skipping multi-file link check");
            return;
        }

        var fails = new Failures();
        int linked = 0;

        foreach (var c in MultiFileCorpus.All)
        {
            using var work = TempDir.Create("appa-multifile-");
            var r = Build(c, work);
            if (r.Crash != null || r.Files == null) continue;

            var outDir = work.Combine("out");
            Directory.CreateDirectory(outDir);
            foreach (var f in r.Files) File.WriteAllText(Path.Combine(outDir, f.Name), f.Content);

            var units = r.Files.Where(f => f.Name.EndsWith(".c", StringComparison.Ordinal))
                               .Select(f => f.Name).ToList();
            if (units.Count == 0) continue;

            // A user realm emits a generated main(); a kernel realm does not, so those link
            // as a relocatable object instead of a program.
            bool hasMain = r.Files.Any(f => f.Content.Contains("int main(void)", StringComparison.Ordinal));
            string mode = hasMain ? "" : "-r -nostdlib ";
            var args = $"-std=c11 -I. {mode}-o linked.out {string.Join(" ", units)}";

            var psi = new ProcessStartInfo(cc, args)
            { WorkingDirectory = outDir, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            linked++;
            if (p.ExitCode == 0) continue;

            var first = err.Split('\n').FirstOrDefault(l =>
                            l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? err;
            fails.Add($"[{c.Name}] {cc} failed on {string.Join(" + ", units)}: {first.Trim()}\n{Describe(c)}");
        }

        Assert.True(linked > 0, "no multi-file project reached the linker; the harness stopped emitting");
        fails.Assert("multi-file projects whose emitted C did not compile and link");
    }

    /// <summary>
    /// Random import-graph fuzzer.
    ///
    /// The hand-written cases cover the graph shapes someone thought of -- a cycle, a diamond,
    /// a chain. This generates arbitrary ones: any number of files, edges chosen at random, so
    /// cycles, self-edges, multiple entry paths into the same file and repeated edges all turn
    /// up in combination. Every file calls into the files it imports, so the graph has to
    /// actually resolve rather than merely parse.
    ///
    /// Two properties are asserted. A generated project must never crash the compiler. And an
    /// *acyclic* graph, where every call target is genuinely reachable through imports, must
    /// build clean -- so a false rejection is a failure too, not just a false acceptance.
    /// Cyclic graphs are only held to the no-crash property, since a call across a cycle may
    /// legitimately be out of scope depending on which direction the edge runs.
    /// </summary>
    [Fact]
    public void RandomImportGraphsBuildCleanly()
    {
        var fails = new Failures();

        for (int seed = 1; seed <= 60; seed++)
        {
            var rng = new Random(seed);
            int n = 2 + rng.Next(7);

            // edges[i] lists the files file i imports. Restricting targets to j > i for the
            // acyclic half guarantees a topological order exists.
            bool acyclic = seed % 2 == 0;
            var edges = new List<int>[n];
            for (int i = 0; i < n; i++)
            {
                edges[i] = [];
                int count = rng.Next(4);
                for (int k = 0; k < count; k++)
                {
                    int target = acyclic ? (i + 1 + rng.Next(Math.Max(1, n - i - 1))) : rng.Next(n);
                    if (target < n && target != i && !edges[i].Contains(target)) edges[i].Add(target);
                }
            }

            var files = new List<(string, string)>();
            for (int i = 0; i < n; i++)
            {
                // Each file declares a class, an enum and a generic, then uses its own types
                // *and* its imports' types -- including instantiating its own generic over a
                // class from another file, which is the shape that has to widen the stamped
                // instance's scope to the requesting file.
                var body = new StringBuilder();
                foreach (var t in edges[i]) body.AppendLine($"import \"src/f{t}.g\";");
                // Every file imports the one shared generic, declared in a file of its own.
                // Instantiating it over a locally declared class is the case where the stamped
                // instance lands in shared.g, which cannot see C{i} without the widening.
                body.AppendLine("import \"src/shared.g\";");
                body.AppendLine($"class C{i} {{ public int n; }}");
                body.AppendLine($"enum E{i} {{ A{i}, B{i} }}");
                body.AppendLine($"class Box{i}[T] {{ public T v; }}");
                body.AppendLine($"public int func F{i}() {{");
                body.AppendLine($"    let C{i} own = new C{i}(); own.n = {i};");
                body.AppendLine($"    let E{i} e = E{i}.A{i};");
                body.AppendLine($"    let Box{i}[C{i}] b = new Box{i}[C{i}]();");
                body.AppendLine($"    let Shared[C{i}] s = new Shared[C{i}]();");
                foreach (var t in edges[i])
                {
                    body.AppendLine($"    let C{t} d{t} = new C{t}(); d{t}.n = {t};");
                    body.AppendLine($"    let Box{i}[C{t}] cross{t} = new Box{i}[C{t}]();");
                    body.AppendLine($"    let int r{t} = F{t}();");
                }
                body.AppendLine("    return own.n;");
                body.AppendLine("}");
                files.Add(($"src/f{i}.g", body.ToString()));
            }
            files.Add(("src/shared.g", "class Shared[T] { public T v; }\n"));
            var mainImports = string.Join("\n", Enumerable.Range(0, n).Select(i => $"import \"src/f{i}.g\";"));
            var mainBody = "let int v = " + string.Join(" + ", Enumerable.Range(0, n).Select(i => $"F{i}()")) + ";";
            files.Add(("src/main.g", $"{mainImports}\nuser {{ entry func Main() {{ {mainBody} }} }}\n"));

            var shape = acyclic ? "acyclic" : "cyclic";
            var c = new MultiFileCase($"graph/{shape}/seed{seed}", [.. files],
                                      acyclic ? Expect.Accepted : Expect.Any);

            using var work = TempDir.Create("appa-multifile-");
            var r = Build(c, work);

            if (r.Crash != null) { fails.Add($"[{c.Name}] {r.Crash}\n{Describe(c)}"); continue; }
            if (!acyclic) continue;

            var errors = r.Diag!.All.Where(d => d.Severity == Severity.Error).ToList();
            if (errors.Count > 0)
                fails.Add($"[{c.Name}] acyclic graph was rejected: " +
                          string.Join("; ", errors.Select(e => $"{e.Code} {e.Message}")) + $"\n{Describe(c)}");
        }

        fails.Assert("random import graphs that did not build");
    }
}
