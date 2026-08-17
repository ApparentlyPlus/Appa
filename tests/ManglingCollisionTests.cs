namespace Appa.Tests;

using System.Diagnostics;

/// <summary>
/// One invariant, swept: a program the compiler accepts must emit C that compiles. Every readable
/// C name joins its parts with '_', which is also legal inside each part, so names that split
/// differently can spell one symbol - 'class A_B { M }' and 'class A { B_M }' are both 'gata_A_B_M'.
/// The names here are chosen to make that happen, so each program is either rejected or compiles.
/// </summary>
public class ManglingCollisionTests
{
    /// <summary>
    /// Names whose underscore splits overlap. Every ordered pair of these placed in two declarations
    /// spells at least one shared C name somewhere.
    /// </summary>
    private static readonly string[] Names = ["A", "B", "A_B", "B_C", "A_B_C"];

    private const string StubEnvironment = """
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
        void func grelease(void* p) native { if (p) free(p); }

        @intrinsic(obj_init)
        void func gobjinit(void* o, func(void*) -> void dtor) native {
            gata_obj* x = (gata_obj*)o; x->__rc = 1; x->__dtor = dtor;
        }

        """;

    /// <summary>
    /// Every pairing of declaration kinds that shares a C namespace, as a source template taking
    /// two names.
    /// </summary>
    private static IEnumerable<(string Kind, string Source)> Shapes(string x, string y)
    {
        yield return ("method/method",
            $"class {x} {{ public int func {y}() {{ return 1; }} }} " +
            $"class {y} {{ public int func {x}() {{ return 2; }} }} " +
            "realm kernel { entry func Main() { } }");

        yield return ("method/func",
            $"class {x} {{ public int func {y}() {{ return 1; }} }} " +
            $"int func {x}_{y}() {{ return 2; }} " +
            "realm kernel { entry func Main() { } }");

        yield return ("enum/func",
            $"enum {x} {{ {y} }} int func {x}_{y}() {{ return 1; }} " +
            "realm kernel { entry func Main() { } }");

        yield return ("enum/enum",
            $"enum {x} {{ {y} }} enum {x}_{y} {{ {x} }} " +
            "realm kernel { entry func Main() { } }");

        yield return ("type/type",
            $"class {x}_{y} {{ public int n; }} class {x} {{ public int n; }} " +
            $"union {y} {{ V(int n) }} " +
            "realm kernel { entry func Main() { } }");

        yield return ("thread/thread",
            "realm kernel { entry func Main() { } } realm userspace { " +
            $"foreground process {x} {{ thread {y} {{ entry func R() {{ }} }} }} " +
            $"foreground process {x}_{y} {{ thread {x} {{ entry func R() {{ }} }} }} }}");

        yield return ("generic/class",
            $"class {x}[T] {{ public T v; }} class {x}_int {{ public int n; }} " +
            $"realm kernel {{ int func U({x}[int] b) {{ return b.v; }} entry func Main() {{ }} }}");

        yield return ("nativetype/type",
            $"native type {x}_{y} {{ int n; }} class {x} {{ public int n; }} enum {y} {{ V }} " +
            "realm kernel { entry func Main() { } }");

        yield return ("module/func",
            $"module {x} {{ public static int func {y}() {{ return 1; }} }} " +
            $"int func {x}_{y}() {{ return 2; }} " +
            "realm kernel { entry func Main() { } }");

        yield return ("union/variant",
            $"union {x} {{ {y}(int n) }} int func {x}_{y}() {{ return 1; }} " +
            "realm kernel { entry func Main() { } }");

        yield return ("scoped/method",
            $"realm kernel {{ class {x} {{ public int func {y}() {{ return 1; }} }} " +
            $"int func {x}_{y}() {{ return 2; }} entry func Main() {{ }} }}");

        yield return ("thread/cross-realm",
            $"realm kernel {{ foreground process {x} {{ thread {y} {{ entry func R() {{ }} }} }} " +
            "entry func Main() { } } " +
            $"realm userspace {{ foreground process {x} {{ thread {y} {{ entry func R() {{ }} }} }} }}");
    }

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
    /// The names the compiler emits without the program declaring them: the launcher, the entry
    /// symbol, thread entries, synthesised function-pointer typedefs, and the dense tokens. A
    /// declaration that spells one of these either has to be rejected or has to survive - and
    /// several used to link silently, binding a call to a body the author never wrote.
    /// </summary>
    [Theory]
    [InlineData("@extern void func uapps(); realm kernel { entry func Main() { uapps(); } } " +
                "realm userspace { foreground process P { thread T { entry func R() { } } } }", false)]
    [InlineData("@extern void func gata_kernelspace_main(); realm kernel { entry func Main() { } }", false)]
    [InlineData("int func userspace_P_T_main() { return 1; } realm kernel { entry func Main() { } } " +
                "realm userspace { foreground process P { thread T { entry func R() { } } } }", false)]
    [InlineData("class Fn_void__void_p { public int n; } " +
                "realm kernel { entry func Main() { let Fn_void__void_p f = new Fn_void__void_p(); } }", false)]
    [InlineData("native type Fn_void__void_p { int n; } realm kernel { entry func Main() { } }", false)]
    [InlineData("@keep class __g1 { public int n; } realm kernel { class A { public int n; } " +
                "entry func Main() { let A a = new A(); let __g1 g = new __g1(); let int q = a.n + g.n; } }", true)]
    [InlineData("enum __g2 { X } realm kernel { class A { public int n; } " +
                "entry func Main() { let A a = new A(); let __g2 e = __g2.X; } }", true)]
    public void GeneratedNamesNotHijacked(string body, bool accepted)
    {
        var cc = FindCompiler();
        if (cc == null) { Assert.Skip("no host C compiler found"); return; }

        var (diag, module) = SingleFileCompile.Check(StubEnvironment + body);
        if (!accepted)
        {
            Assert.True(diag.HasErrors, "expected a rejection, but the program was accepted");
            return;
        }
        Assert.False(diag.HasErrors, "expected acceptance, got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} {d.Message}")));

        // Accepted means it has to compile: the token that gave way must not reappear elsewhere.
        var files = Layout.Compose(new Emitter(module!, diag).Build(), module!.Symbols);
        using var work = Scratch.Create("appa-generated-");
        foreach (var f in files) File.WriteAllText(Path.Combine(work.Path, f.Name), f.Content);
        foreach (var tu in files.Where(f => f.Name.EndsWith(".c", StringComparison.Ordinal)))
        {
            var psi = new ProcessStartInfo(cc,
                $"-c -std=c11 -I. -o {(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")} {tu.Name}")
            { WorkingDirectory = work.Path, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"{tu.Name} did not compile:\n{err}");
        }
    }

    /// <summary>
    /// C's vocabulary is not Gata's, so 'inline' and 'register' are ordinary Gata identifiers.
    /// </summary>
    [Theory]
    [InlineData("class K { public int inline; } realm kernel { " +
                "entry func Main() { let K k = new K(); k.inline = 1; let int n = k.inline; } }")]
    [InlineData("class K { public int register; public int volatile; public int const; } realm kernel { " +
                "entry func Main() { let K k = new K(); k.register = 1; k.volatile = k.register; " +
                "k.const = k.volatile; } }")]
    [InlineData("union U { Empty, Wrapped(int extern) } realm kernel { entry func Main() { " +
                "let U u = U.Wrapped(3); let int n = 0; " +
                "match (u) { case Empty { } case Wrapped(extern) { n = extern; } } } }")]
    [InlineData("union U { restrict, typedef(int goto) } realm kernel { entry func Main() { " +
                "let U a = U.typedef(1); let U b = U.restrict(); let bool same = a == b; } }")]
    [InlineData("class K { public int stdout; public int errno; } realm kernel { " +
                "entry func Main() { let K k = new K(); k.stdout = 1; k.errno = k.stdout; } }")]
    [InlineData("class K { public int inline; func _init() { self.inline = 7; } } realm kernel { " +
                "entry func Main() { let K k = new K(); let int n = k.inline; } }")]
    public void CReservedIdentifiersSurvive(string body)
    {
        var cc = FindCompiler();
        if (cc == null) { Assert.Skip("no host C compiler found"); return; }

        var (diag, module) = SingleFileCompile.Check(StubEnvironment + body);
        Assert.False(diag.HasErrors, "expected acceptance, got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} {d.Message}")));

        var files = Layout.Compose(new Emitter(module!, diag).Build(), module!.Symbols);
        using var work = Scratch.Create("appa-creserved-");
        foreach (var f in files) File.WriteAllText(Path.Combine(work.Path, f.Name), f.Content);
        foreach (var tu in files.Where(f => f.Name.EndsWith(".c", StringComparison.Ordinal)))
        {
            var psi = new ProcessStartInfo(cc,
                $"-c -std=c11 -I. -o {(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")} {tu.Name}")
            { WorkingDirectory = work.Path, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"{tu.Name} did not compile:\n{err}");
        }
    }

    /// <summary>
    /// The one name the compiler may not rename to rescue it. An '@extern' is emitted verbatim so
    /// the linker can find it, so spelling one 'inline' is a program the compiler has to reject
    /// rather than repair.
    /// </summary>
    [Theory]
    [InlineData("@extern void func inline(); realm kernel { entry func Main() { } }")]
    [InlineData("@extern int func stdout(int n); realm kernel { entry func Main() { let int q = stdout(1); } }")]
    public void CReservedExternNamesRejected(string body)
    {
        var (diag, _) = SingleFileCompile.Check(StubEnvironment + body);
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == Codes.CReservedCName);
    }

    [Fact]
    public void NoCollisionsInC()
    {
        var cc = FindCompiler();
        if (cc == null) { Assert.Skip("no host C compiler found; skipping mangling-collision sweep"); return; }

        using var work = Scratch.Create("appa-mangle-");
        var failures = new List<string>();
        int accepted = 0, rejected = 0, unit = 0;

        foreach (var x in Names)
            foreach (var y in Names)
                foreach (var (kind, body) in Shapes(x, y))
                {
                    string src = StubEnvironment + body;
                    IReadOnlyList<OutputFile> files;
                    try
                    {
                        var (diag, module) = SingleFileCompile.Check(src);
                        if (diag.HasErrors || module == null) { rejected++; continue; }
                        files = Layout.Compose(new Emitter(module, diag).Build(), module.Symbols);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"[{kind} {x}/{y}] the compiler threw: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    accepted++;
                    var dir = work.Combine("u" + unit++);
                    Directory.CreateDirectory(dir);
                    foreach (var f in files) File.WriteAllText(Path.Combine(dir, f.Name), f.Content);

                    foreach (var tu in files.Where(f => f.Name.EndsWith(".c", StringComparison.Ordinal)))
                    {
                        var psi = new ProcessStartInfo(cc,
                            $"-c -std=c11 -I. -o {(OperatingSystem.IsWindows() ? "NUL" : "/dev/null")} {tu.Name}")
                        { WorkingDirectory = dir, RedirectStandardError = true, UseShellExecute = false };
                        using var p = Process.Start(psi)!;
                        string err = p.StandardError.ReadToEnd();
                        p.WaitForExit();
                        if (p.ExitCode == 0) continue;

                        string first = err.Split('\n').FirstOrDefault(l => l.Contains(": error:", StringComparison.Ordinal))
                                       ?? "<no diagnostic>";
                        failures.Add($"[{kind} {x}/{y}] {tu.Name}: {first.Trim()}\n{body}");
                    }
                }

        Assert.True(accepted > 40, $"only {accepted} programs were accepted; the generator stopped covering");
        Assert.True(rejected > 10, $"only {rejected} programs were rejected; the collision rule stopped firing");

        if (failures.Count == 0) return;
        var shown = string.Join("\n\n", failures.Take(15));
        var more = failures.Count > 15 ? $"\n\n... and {failures.Count - 15} more" : "";
        Assert.Fail($"{failures.Count} accepted programs emitted colliding C:\n\n{shown}{more}");
    }
}
