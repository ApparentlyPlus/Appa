namespace Appa.Tests;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Grammar-directed fuzzer over scope <i>shapes</i>: the same names at root, in each realm and in
/// each process, used from every level that sees them. Valid by construction, so a rejection fails
/// too; the oracle is a real link, since a name collision only shows up once the objects meet.
/// </summary>
public class ScopeFuzzTests
{
    private const int Seeds = 120;

    /// <summary>
    /// Stub environment for both realms: generated programs import no libgata, so nothing else
    /// binds the ARC roles or supplies the headers.
    /// </summary>
    private const string Scaffold = """
        @preamble(kernel) native {
        #include <stdint.h>
        #include <stddef.h>
        #include <stdbool.h>
        #include <stdlib.h>
        #include "shared.h"
        }

        @preamble(user) native {
        #include <stdint.h>
        #include <stddef.h>
        #include <stdbool.h>
        #include <stdlib.h>
        #include "shared.h"
        }

        @intrinsic(obj_header)
        native type obj { gata_Fn_void__void_p __dtor; size_t __rc; }

        @intrinsic(alloc)
        void* func gmalloc(usize n) native { return calloc(1, (size_t)n); }

        @intrinsic(retain)
        void* func gretain(void* p) native { if (p) ((gata_obj*)p)->__rc++; return p; }

        @intrinsic(release)
        void func grelease(void* p) native {
            if (!p) return;
            gata_obj* o = (gata_obj*)p;
            if (--o->__rc == 0) { if (o->__dtor) o->__dtor(p); free(p); }
        }

        @intrinsic(obj_init)
        void func gobjinit(void* o, func(void*) -> void dtor) native {
            gata_obj* x = (gata_obj*)o; x->__rc = 1; x->__dtor = dtor;
        }

        """;

    /// <summary>
    /// What a generated program actually exercised, so a generator that drifts into emitting the
    /// same trivial shape every seed fails rather than passing quietly.
    /// </summary>
    private sealed class Coverage
    {
        public int Marked, Unmarked, Processes, ScopedGenerics, NestedArgs, CrossLevelUses, LocalBindings, Qualified;
    }

    /// <summary>
    /// One scope's declarations. Each name is marked '@shadows' exactly when an enclosing scope
    /// already declares it, so a program exercises both branches of the rule at once - and dropping
    /// the marks turns every one of those into an error the negative half must see.
    /// </summary>
    private static string Declarations(Random rng, string tag, string pad, Coverage cov,
                                       HashSet<string> outer, HashSet<string> mine, bool mark)
    {
        var sb = new StringBuilder();

        void Decl(string name, string text)
        {
            bool shadows = outer.Contains(name);
            if (shadows) { if (mark) cov.Marked++; else cov.Unmarked++; }
            sb.Append(pad).Append(shadows && mark ? "@shadows " : "").Append(text).Append('\n');
            mine.Add(name);
        }

        Decl("Cargo", $"class Cargo {{ public int {tag}; }}");
        Decl("Phase", $"enum Phase {{ Boot{tag}, Ready{tag} }}");
        Decl("Step", $"int func Step(int n) {{ return n + {tag.Length}; }}");
        Decl($"Param{tag}", $"int func Param{tag}(int Cargo) {{ return Cargo; }}");

        if (rng.Next(2) == 0)
        {
            cov.ScopedGenerics++;
            Decl("Box", "class Box[T] { public T v; }");
            Decl($"Use{tag}", $"void func Use{tag}(Box[Cargo] b) {{ let int n = b.v.{tag}; }}");
            if (rng.Next(2) == 0)
            {
                cov.NestedArgs++;
                Decl($"Deep{tag}", $"void func Deep{tag}(Box[Box[Cargo]] b) {{ let int n = b.v.v.{tag}; }}");
            }
        }
        if (rng.Next(2) == 0)
        {
            Decl("Slot", "union Slot { Full(Cargo c), Empty }");
            Decl($"Read{tag}", $"int func Read{tag}(Slot s) {{ match (s) {{ " +
                               $"case Full(c) {{ return c.{tag}; }} case Empty {{ return 0; }} }} }}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// A body that uses the innermost meaning of every shared name, so a scope resolving outward one
    /// level too far stops compiling rather than silently computing something else.
    /// </summary>
    private static string Body(string tag, Coverage cov, params (string Path, string Tag)[] outer)
    {
        cov.CrossLevelUses++;
        cov.LocalBindings++;
        var reach = new StringBuilder();
        for (int i = 0; i < outer.Length; i++)
        {
            cov.Qualified++;
            var (path, otag) = outer[i];
            reach.Append($"let {path}Cargo o{i} = new {path}Cargo(); ")
                 .Append($"o{i}.{otag} = {path}Step({i}); ");
        }
        return $"let Cargo c = new Cargo(); c.{tag} = 1; " +
               $"let Phase p = Phase.Boot{tag}; let int n = Step(c.{tag}); " +
               $"{{ let int Cargo = 7; let int Phase = Cargo + 1; n = n + Phase; }} " +
               $"for (let int Step = 0; Step < 2; Step++) {{ n = n + Step; }} " +
               $"let Cargo again = new Cargo(); again.{tag} = n; " +
               $"let Phase q = Phase.Ready{tag}; " + reach;
    }

    /// <summary>
    /// One program: root declarations, a kernel realm and a userspace realm, each with its own
    /// declarations and a random number of processes that declare the same names again. With
    /// mark = false the shadowing marks are dropped and the program must be rejected.
    /// </summary>
    private static string Generate(int seed, Coverage cov, bool mark)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder(Scaffold);

        var atRoot = new HashSet<string>();
        sb.Append(Declarations(rng, "root", "", cov, [], atRoot, mark));

        foreach (var (realm, rtag) in ((string, string)[])[("kernel", "kern"), ("userspace", "user")])
        {
            sb.Append($"realm {realm} {{\n");
            var inRealm = new HashSet<string>();
            sb.Append(Declarations(rng, rtag, "    ", cov, atRoot, inRealm, mark));

            int procs = rng.Next(3);
            for (int i = 0; i < procs; i++)
            {
                cov.Processes++;
                string ptag = $"{rtag}p{i}";
                string mode = rng.Next(2) == 0 ? "foreground" : "background";
                sb.Append($"    {mode} process P{rtag}{i} {{\n");
                sb.Append(Declarations(rng, ptag, "        ", cov, [.. atRoot, .. inRealm], [], mark));
                sb.Append($"        thread T {{ entry func Run() {{ " +
                          $"{Body(ptag, cov, ($"{realm}.P{rtag}{i}.", ptag), ($"{realm}.", rtag), ("::", "root"))} }} }}\n");
                sb.Append("    }\n");
            }

            string reach = $"{Body(rtag, cov, ($"{realm}.", rtag), ("::", "root"))}";
            if (realm == "kernel") sb.Append($"    entry func Main() {{ {reach} }}\n");
            else sb.Append($"    void func Helper() {{ {reach} }}\n");
            sb.Append("}\n");
        }

        sb.Append($"void func AtRoot() {{ {Body("root", cov)} }}\n");
        return sb.ToString();
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

    [Fact]
    public void ScopedProgramsLink()
    {
        var cc = FindCompiler();
        using var work = Scratch.Create("appa-scopefuzz-");
        var failures = new List<string>();
        var cov = new Coverage();
        int linked = 0;

        for (int seed = 1; seed <= Seeds; seed++)
        {
            var src = Generate(seed, cov, mark: true);
            IrModule module;
            try
            {
                var sources = new SourceSet();
                sources.Add("<scopefuzz>", src);
                var diag = new DiagnosticBag(sources);

                Program prog;
                try { prog = SingleFileCompile.Parse(src); }
                catch (ParseException ex)
                {
                    failures.Add($"[seed {seed}] parse failed: {ex.Message}\n{src}");
                    continue;
                }

                var programs = new List<(string path, Program prog)> { ("<scopefuzz>", prog) };
                var visible = new Dictionary<string, HashSet<string>> { ["<scopefuzz>"] = ["<scopefuzz>"] };
                var (m, _, _) = Pipeline.BuildModule(programs, visible, Mode.Debug, diag);
                Pipeline.ValidateIntrinsics(m, diag);
                Pipeline.ValidateStructure(programs, null, diag);

                if (diag.HasErrors)
                {
                    var errs = diag.All.Where(d => d.Severity == Severity.Error)
                                       .Select(d => $"{d.Code} {d.Message}");
                    failures.Add($"[seed {seed}] rejected a valid program: {string.Join("; ", errs)}\n{src}");
                    continue;
                }
                module = m;
            }
            catch (Exception ex)
            {
                failures.Add($"[seed {seed}] front end threw: {ex.GetType().Name}: {ex.Message}\n{src}");
                continue;
            }

            IReadOnlyList<OutputFile> files;
            try
            {
                var sources = new SourceSet();
                sources.Add("<scopefuzz>", src);
                files = Layout.Compose(new Emitter(module, new DiagnosticBag(sources)).Build(), module.Symbols);
            }
            catch (Exception ex)
            {
                failures.Add($"[seed {seed}] emitter threw: {ex.GetType().Name}: {ex.Message}\n{src}");
                continue;
            }

            if (cc == null) continue;

            var dir = work.Combine("s" + seed);
            Directory.CreateDirectory(dir);
            foreach (var f in files) File.WriteAllText(Path.Combine(dir, f.Name), f.Content);
            var units = files.Where(f => f.Name.EndsWith(".c", StringComparison.Ordinal))
                             .Select(f => f.Name).ToList();
            if (units.Count == 0) continue;

            var psi = new ProcessStartInfo(cc,
                $"-std=c11 -I. -r -nostdlib -Werror=return-type -o linked.out {string.Join(" ", units)}")
            { WorkingDirectory = dir, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            linked++;
            if (p.ExitCode == 0) continue;

            var first = err.Split('\n').FirstOrDefault(l =>
                            l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? err;
            failures.Add($"[seed {seed}] emitted C did not build: {first.Trim()}\n{src}");
        }

        if (failures.Count > 0)
        {
            var shown = string.Join("\n\n", failures.Take(3));
            var more = failures.Count > 3 ? $"\n\n... and {failures.Count - 3} more" : "";
            Assert.Fail($"{failures.Count} scope-fuzz failures:\n\n{shown}{more}");
        }

        Assert.True(cov.Marked >= 300, $"generator emitted only {cov.Marked} shadowing declarations");
        Assert.True(cov.Processes >= 60, $"generator emitted only {cov.Processes} processes");
        Assert.True(cov.ScopedGenerics >= 60, $"generator emitted only {cov.ScopedGenerics} scoped generics");
        Assert.True(cov.NestedArgs >= 20, $"generator emitted only {cov.NestedArgs} nested type arguments");
        Assert.True(cov.CrossLevelUses >= 300, $"generator emitted only {cov.CrossLevelUses} scoped uses");
        Assert.True(cov.LocalBindings >= 300, $"generator emitted only {cov.LocalBindings} local bindings");
        Assert.True(cov.Qualified >= 500, $"generator emitted only {cov.Qualified} scope qualifiers");
        if (cc != null) Assert.True(linked > 0, "no generated program reached the linker");
    }

    /// <summary>
    /// The same programs with the marks dropped. Every one must be rejected, and every error must be
    /// G088 - a generator whose programs are invalid for some unrelated reason would otherwise let
    /// the positive half pass on a rule that never fires.
    /// </summary>
    [Fact]
    public void UnmarkedShadowingRejected()
    {
        var failures = new List<string>();
        var cov = new Coverage();

        for (int seed = 1; seed <= Seeds; seed++)
        {
            var src = Generate(seed, cov, mark: false);
            try
            {
                var sources = new SourceSet();
                sources.Add("<scopefuzz>", src);
                var diag = new DiagnosticBag(sources);
                var prog = SingleFileCompile.Parse(src);

                var programs = new List<(string path, Program prog)> { ("<scopefuzz>", prog) };
                var visible = new Dictionary<string, HashSet<string>> { ["<scopefuzz>"] = ["<scopefuzz>"] };
                var (m, _, _) = Pipeline.BuildModule(programs, visible, Mode.Debug, diag);
                Pipeline.ValidateIntrinsics(m, diag);
                Pipeline.ValidateStructure(programs, null, diag);

                var errs = diag.All.Where(d => d.Severity == Severity.Error).ToList();
                if (errs.Count == 0)
                {
                    failures.Add($"[seed {seed}] unmarked shadowing was accepted\n{src}");
                    continue;
                }
                var stray = errs.FirstOrDefault(d => d.Code != Codes.UnmarkedShadow);
                if (stray != null)
                    failures.Add($"[seed {seed}] rejected for the wrong reason: {stray.Code} {stray.Message}\n{src}");
            }
            catch (Exception ex)
            {
                failures.Add($"[seed {seed}] front end threw: {ex.GetType().Name}: {ex.Message}\n{src}");
            }
        }

        if (failures.Count > 0)
        {
            var shown = string.Join("\n\n", failures.Take(3));
            var more = failures.Count > 3 ? $"\n\n... and {failures.Count - 3} more" : "";
            Assert.Fail($"{failures.Count} unmarked-shadowing failures:\n\n{shown}{more}");
        }
        Assert.True(cov.Unmarked >= 300, $"generator emitted only {cov.Unmarked} shadowing declarations");
    }
}
