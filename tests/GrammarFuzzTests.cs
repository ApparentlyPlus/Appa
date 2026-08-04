namespace Appa.Tests;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Generates random but *valid* Gata programs and checks each survives the whole compiler and emits
/// C gcc accepts. Complements TortureTests' token soup, which rarely type-checks and so never
/// reaches lowering, ARC or the emitter. Seeds are fixed.
/// </summary>
public class GrammarFuzzTests
{
    /// <summary>
    /// How many programs to generate. Kept modest so the suite stays fast; the generator is
    /// deterministic, so raising this locally explores strictly more without losing what the
    /// committed range already covers.
    /// </summary>
    private const int Seeds = 300;

    /// <summary>
    /// A stand-in environment, matching EmittedCCompilesTests: corpus programs import no libgata,
    /// so nothing declares a realm or binds the ARC roles.
    /// </summary>
    private const string StubEnvironment = """
        @preamble(kernel) native {
        #include <stdint.h>
        #include <stddef.h>
        #include <stdbool.h>
        #include <stdlib.h>
        #include "shared.h"
        }

        """;

    /// <summary>
    /// The fixed preamble every generated program shares: the declarations its random body draws
    /// on. Having them constant means a generated body is always well-scoped, so any diagnostic is
    /// about the shape the fuzzer built rather than an undefined name.
    /// </summary>
    private const string Scaffold = """
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

        union U { A(int x), B }
        class Thing { public int n; func _init() { self.n = 0; } }
        throws int func Risky() { throw; }
        int func Two(int v) { return v * 2; }
        """;

    private sealed class Generator(int seed)
    {
        private readonly Random _r = new(seed);
        private int _depth;
        private int _names;
        private bool _inDefer;

        private string Pick(params string[] xs) => xs[_r.Next(xs.Length)];

        /// <summary>
        /// Builds a defer whose body knows it is inside one.
        /// </summary>
        private string Defer()
        {
            bool prev = _inDefer;
            _inDefer = true;
            try { return $"defer {{ {Stmt()} }}"; }
            finally { _inDefer = prev; }
        }

        /// <summary>
        /// Builds a random expression, bottoming out at a leaf past the depth cap.
        /// </summary>
        public string Expr()
        {
            if (_depth > 6) return Pick("1", "i", "n", "arr[0]", "obj.n", "Two(1)");
            _depth++;
            try
            {
                return _r.Next(16) switch
                {
                    0 => Pick("0", "1", "2", "7"),
                    1 => Pick("i", "n", "arr[1]"),
                    2 => $"({Expr()} + {Expr()})",
                    3 => $"({Expr()} < {Expr()} ? 1 : 0)",
                    4 => $"(({Expr()} == {Expr()}) ? 1 : 0)",
                    5 => $"(-{Expr()})",
                    6 => $"({Expr()} * {Expr()})",
                    7 => $"(({Expr()}) > 0 ? {Expr()} : {Expr()})",
                    8 => $"arr[({Expr()} * 0)]",
                    9 => "obj.n",
                    10 => $"Two({Expr()})",
                    11 => $"({Expr()} % 3)",
                    12 => $"({Expr()} as int)",
                    13 => "(sizeof(int) as int)",
                    14 => $"({Expr()} | {Expr()})",
                    _ => Pick($"({Expr()} << 1)", $"({Expr()} & 3)", $"(~{Expr()})",
                              $"(-{Expr()})", $"({Expr()} - {Expr()})", $"({Expr()} / 3)",
                              $"(({Expr()} != {Expr()}) ? 1 : 0)", "default(int)"),
                };
            }
            finally { _depth--; }
        }

        /// <summary>
        /// Builds a random statement, bottoming out at an assignment past the depth cap.
        /// </summary>
        public string Stmt()
        {
            if (_depth > 5) return $"n = {Expr()};";
            _depth++;
            try
            {
                return _r.Next(16) switch
                {
                    0 => $"n = {Expr()};",
                    1 => $"let int v{_names++} = {Expr()};",
                    2 => $"if ({Expr()} == 0) {{ {Stmt()} }}",
                    3 => $"if ({Expr()} == 0) {{ {Stmt()} }} else {{ {Stmt()} }}",
                    4 => _inDefer ? $"while (false) {{ {Stmt()} }}" : $"while ({Expr()} == 0) {{ {Stmt()} break; }}",
                    5 => $"for (let int k{_names++} = 0; n < 3; n++) {{ {Stmt()} }}",
                    6 => $"for y{_names++} in arr {{ {Stmt()} }}",
                    7 => $"{{ {Stmt()} {Stmt()} }}",
                    8 => $"switch ({Expr()}) {{ case 1 {{ {Stmt()} }} default {{ {Stmt()} }} }}",
                    9 => $"match (u) {{ case A(p{_names++}) {{ {Stmt()} }} case B {{ {Stmt()} }} }}",
                    10 => _inDefer ? $"{{ {Stmt()} }}" : $"try {{ Risky(); {Stmt()} }} catch {{ {Stmt()} }}",
                    11 => _inDefer ? $"n = {Expr()};" : $"let int c{_names++} = Risky() catch {{ {Stmt()} assign {Expr()}; }};",
                    12 => _inDefer ? $"{{ {Stmt()} }}" : Defer(),
                    13 => $"unsafe {{ {Stmt()} }}",
                    14 => $"arr[0] = {Expr()};",
                    _ => Pick($"obj.n = {Expr()};", $"n += {Expr()};", $"n -= {Expr()};",
                              $"n *= {Expr()};", $"n <<= 1;", $"n ^= {Expr()};", "n++;", "n--;",
                              $"arr[1] += {Expr()};", $"obj.n *= {Expr()};",
                              _inDefer ? $"n = {Expr()};" : $"while (true) {{ {Stmt()} continue; }}"),
                };
            }
            finally { _depth--; }
        }
    }

    /// <summary>
    /// Assembles one complete program from a seed.
    /// </summary>
    private static string Generate(int seed)
    {
        var gen = new Generator(seed);
        var body = new StringBuilder();
        for (int i = 0; i < 8; i++) body.Append("    ").Append(gen.Stmt()).Append('\n');

        return StubEnvironment + Scaffold + """

            realm kernel { entry func Main() {
                let int i = 0;
                let int n = 0;
                let arr = [1, 2, 3];
                let Thing obj = new Thing();
                let U u = U.A(1);

            """ + body + "} }\n";
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
    public void FuzzedProgramsCompile()
    {
        var cc = FindCompiler();
        using var work = TempDir.Create("appa-gfuzz-");
        var failures = new List<string>();
        int accepted = 0;

        for (int seed = 1; seed <= Seeds; seed++)
        {
            var src = Generate(seed);

            DiagnosticBag diag;
            IrModule module;
            try
            {
                var sources = new SourceSet();
                sources.Add("<fuzz>", src);
                diag = new DiagnosticBag(sources);

                Program prog;
                try { prog = SingleFileCompile.Parse(src); }
                catch (ParseException ex)
                {
                    failures.Add($"[seed {seed}] parse failed: {ex.Message}\n{src}");
                    continue;
                }

                var programs = new List<(string path, Program prog)> { ("<fuzz>", prog) };
                var visible = new Dictionary<string, HashSet<string>> { ["<fuzz>"] = ["<fuzz>"] };
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
            try { files = Layout.Compose(new Emitter(module, diag).Build(), module.Symbols); }
            catch (Exception ex)
            {
                failures.Add($"[seed {seed}] emitter threw: {ex.GetType().Name}: {ex.Message}\n{src}");
                continue;
            }
            accepted++;
            if (cc == null) continue;

            var dir = work.Combine("s" + seed);
            Directory.CreateDirectory(dir);
            foreach (var f in files) File.WriteAllText(Path.Combine(dir, f.Name), f.Content);

            foreach (var unit in files.Where(f => f.Name.EndsWith(".c", StringComparison.Ordinal)))
            {
                string devNull = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
                var psi = new ProcessStartInfo(cc,
                    $"-c -std=c11 -Werror=return-type -I. -o {devNull} {unit.Name}")
                { WorkingDirectory = dir, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0) continue;

                var first = err.Split('\n').FirstOrDefault(l => l.Contains(": error:", StringComparison.Ordinal)) ?? err;
                failures.Add($"[seed {seed}] emitted invalid C: {first.Trim()}\n{src}");
            }
        }

        if (failures.Count > 0)
        {
            const int shownCount = 3;
            var shown = string.Join("\n\n", failures.Take(shownCount));
            var more = failures.Count > shownCount ? $"\n\n... and {failures.Count - shownCount} more" : "";
            Assert.Fail($"{failures.Count} of {Seeds} generated programs failed:\n\n{shown}{more}");
        }

        Assert.True(accepted > Seeds / 2,
            $"only {accepted} of {Seeds} generated programs were accepted; the generator has drifted out of the language");
    }
}
