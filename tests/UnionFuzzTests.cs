namespace Appa.Tests;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Grammar-directed fuzzer over union <i>shapes</i>, which everything unions grew is generated
/// from. Programs are valid by construction, so a rejection fails too; the oracle is a real gcc
/// compile with -Werror=return-type, and seeds are fixed.
/// </summary>
public class UnionFuzzTests
{
    private const int Seeds = 250;

    /// <summary>
    /// Stub environment and ARC scaffolding: generated programs import no libgata, so nothing else
    /// binds the runtime roles or declares a realm. Plain and Valued are the payload classes,
    /// differing only in that Valued declares '==' and so compares by value.
    /// </summary>
    private const string Scaffold = """
        @preamble(kernel) native {
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

        enum Colour { Red, Green }

        class Plain { public int n; func _init() { self.n = 0; } }

        class Valued {
            public int n;
            func _init() { self.n = 0; }
            public operator bool func ==(Valued o) { return self.n == o.n; }
        }

        class Crate[T] { public T item; }

        """;

    /// <summary>
    /// One generated union: its name and the variants it declares.
    /// </summary>
    private sealed record Union(string Name, List<Variant> Variants);

    /// <summary>
    /// One variant: its name and its field types, in declaration order.
    /// </summary>
    private sealed record Variant(string Name, List<string> FieldTypes)
    {
        public string FieldName(int i) => $"f{i}";
    }

    private sealed class Generator(int seed)
    {
        private readonly Random _r = new(seed);
        private int _names;

        public string Fresh(string prefix) => $"{prefix}{_names++}";

        private T Pick<T>(IReadOnlyList<T> xs) => xs[_r.Next(xs.Count)];

        public int Next(int n) => _r.Next(n);

        /// <summary>
        /// Builds unions in dependency order - union i may only mention earlier ones - which makes
        /// a by-value cycle impossible without reasoning about one. A rejection is a failure here,
        /// so the generator must stay inside the language.
        /// </summary>
        public List<Union> Unions()
        {
            var result = new List<Union>();
            int count = 1 + _r.Next(3);

            for (int u = 0; u < count; u++)
            {
                var variants = new List<Variant>();
                int nv = 1 + _r.Next(4);

                for (int v = 0; v < nv; v++)
                {
                    var fields = new List<string>();
                    int nf = _r.Next(4);                  // 0 fields is a legal, and interesting, variant
                    for (int f = 0; f < nf; f++) fields.Add(FieldType(result));
                    variants.Add(new Variant($"V{v}", fields));
                }

                result.Add(new Union($"U{u}", variants));
            }
            return result;
        }

        /// <summary>
        /// Picks a payload type. The weighting is deliberate: primitives keep programs cheap, but
        /// every category that changes what the compiler generates - a managed class, one with its
        /// own equality, a nested union, an aggregate - stays well represented.
        /// </summary>
        private string FieldType(List<Union> declared)
        {
            var choices = new List<string>
            {
                "int", "int", "bool", "char", "int64", "float", "double",
                "Plain", "Plain", "Valued", "Valued",
                "Colour", "[2]int", "[3]bool",
            };
            foreach (var u in declared) { choices.Add(u.Name); choices.Add(u.Name); }
            return Pick(choices);
        }

        /// <summary>
        /// Builds an expression producing a value of the given payload type.
        /// </summary>
        public string ValueOf(string type, List<Union> unions, int depth)
        {
            return type switch
            {
                "int" => _r.Next(4).ToString(),
                "int64" => $"({_r.Next(4)} as int64)",
                "bool" => Pick(["true", "false"]),
                "char" => "'a'",
                "float" => $"{_r.Next(3)}.5f",
                "double" => $"{_r.Next(3)}.5",
                "Colour" => Pick(["Colour.Red", "Colour.Green"]),
                "Plain" => "new Plain()",
                "Valued" => "new Valued()",
                "[2]int" => $"[{_r.Next(3)}, {_r.Next(3)}]",
                "[3]bool" => "[true, false, true]",
                _ => Construct(unions.First(u => u.Name == type), unions, depth + 1),
            };
        }

        /// <summary>
        /// Builds a construction of some variant of the given union.
        /// </summary>
        public string Construct(Union u, List<Union> unions, int depth)
        {
            var v = depth > 3 && u.Variants.Any(x => x.FieldTypes.Count == 0)
                ? u.Variants.First(x => x.FieldTypes.Count == 0)
                : Pick(u.Variants);

            var args = v.FieldTypes.Select(t => ValueOf(t, unions, depth));
            return $"{u.Name}.{v.Name}({string.Join(", ", args)})";
        }

        /// <summary>
        /// Builds a match over a variable of the given union: either exhaustive with no default, or
        /// a subset plus a default. Both shapes are legal and they lower differently - the
        /// exhaustive one collapses its last arm to a bare else - so both need generating.
        /// </summary>
        public string Match(Union u, string scrutinee, bool allArmsReturn)
        {
            bool exhaustive = _r.Next(2) == 0;
            var arms = new StringBuilder();
            var chosen = exhaustive ? u.Variants : [.. u.Variants.Take(1 + _r.Next(u.Variants.Count))];

            foreach (var v in chosen)
            {
                string binds = v.FieldTypes.Count == 0
                    ? ""
                    : "(" + string.Join(", ", v.FieldTypes.Select((_, i) => Fresh("b"))) + ")";
                string body = allArmsReturn ? $"return {v.FieldTypes.Count};" : $"acc = acc + {v.FieldTypes.Count};";
                arms.Append($"case {v.Name}{binds} {{ {body} }} ");
            }

            if (!exhaustive)
                arms.Append(allArmsReturn ? "default { return -1; } " : "default { acc = acc - 1; } ");

            return $"match ({scrutinee}) {{ {arms}}}";
        }
    }

    /// <summary>
    /// What the corpus actually contained, tallied while generating. A fuzzer that stops producing
    /// the shapes it was written for still passes, so these counts are asserted against every
    /// category the union machinery branches on.
    /// </summary>
    private sealed class Coverage
    {
        public int ManagedPayload, ValuedPayload, NestedUnion, FloatPayload,
                   ArrayPayload, EnumPayload, EmptyVariant, MultiFieldVariant, MultiUnion;

        public void Observe(List<Union> unions)
        {
            if (unions.Count > 1) MultiUnion++;
            foreach (var u in unions)
                foreach (var v in u.Variants)
                {
                    if (v.FieldTypes.Count == 0) EmptyVariant++;
                    if (v.FieldTypes.Count > 1) MultiFieldVariant++;
                    foreach (var t in v.FieldTypes)
                    {
                        if (t == "Plain") ManagedPayload++;
                        else if (t == "Valued") ValuedPayload++;
                        else if (t is "float" or "double") FloatPayload++;
                        else if (t.StartsWith('[')) ArrayPayload++;
                        else if (t == "Colour") EnumPayload++;
                        else if (t.StartsWith('U')) NestedUnion++;
                    }
                }
        }
    }

    /// <summary>
    /// Assembles one complete program from a seed.
    /// </summary>
    private static string Generate(int seed, Coverage? coverage = null)
    {
        var gen = new Generator(seed);
        var unions = gen.Unions();
        coverage?.Observe(unions);
        var src = new StringBuilder(Scaffold);

        foreach (var u in unions)
        {
            var variants = u.Variants.Select(v =>
                v.FieldTypes.Count == 0
                    ? v.Name
                    : $"{v.Name}({string.Join(", ", v.FieldTypes.Select((t, i) => $"{t} {v.FieldName(i)}"))})");
            src.Append($"union {u.Name} {{ {string.Join(", ", variants)} }}\n");
        }
        src.Append('\n');

        foreach (var u in unions)
            src.Append($"int func Weigh_{u.Name}({u.Name} v) {{ {gen.Match(u, "v", allArmsReturn: true)} }}\n");

        foreach (var u in unions)
        {
            src.Append($"class Box_{u.Name} {{ {u.Name} slot; " +
                       $"public func _init({u.Name} s) {{ self.slot = s; }} " +
                       $"public {u.Name} func Get() {{ return self.slot; }} }}\n");
            src.Append($"{u.Name} func Echo_{u.Name}({u.Name} v) {{ return v; }}\n");
        }
        src.Append('\n');

        var body = new StringBuilder();
        foreach (var u in unions)
        {
            string a = gen.Fresh("a");
            string b = gen.Fresh("b");
            body.Append($"    let {u.Name} {a} = {gen.Construct(u, unions, 0)};\n");
            body.Append($"    let {u.Name} {b} = {gen.Construct(u, unions, 0)};\n");
            body.Append($"    {a} = {gen.Construct(u, unions, 0)};\n");
            body.Append($"    if ({a} == {b}) {{ acc = acc + 1; }}\n");
            body.Append($"    if ({a} != {b}) {{ acc = acc + 2; }}\n");
            body.Append($"    acc = acc + Weigh_{u.Name}({a});\n");
            body.Append($"    acc = acc + Weigh_{u.Name}(Echo_{u.Name}({b}));\n");
            body.Append($"    let Box_{u.Name} {gen.Fresh("box")} = new Box_{u.Name}({a});\n");
            body.Append($"    let Crate[{u.Name}] {gen.Fresh("cr")} = new Crate[{u.Name}]();\n");
            body.Append($"    {gen.Match(u, a, allArmsReturn: false)}\n");
            body.Append($"    {{ let {u.Name} {gen.Fresh("t")} = {gen.Construct(u, unions, 0)}; " +
                        $"if (acc > 100000) {{ acc = 0; }} }}\n");
        }

        src.Append("realm kernel { entry func Main() {\n    let int acc = 0;\n");
        src.Append(body);
        src.Append("} }\n");
        return src.ToString();
    }

    /// <summary>
    /// Assembles one generic-union program: several templates over two argument sets each, plus a
    /// body building, matching and comparing them. Two instantiations is the point - one never
    /// exercises which stamped union 'G.V(...)' means, nor catches bleed.
    /// </summary>
    private static string GenerateGeneric(int seed, GenericCoverage? coverage = null)
    {
        var r = new Random(seed);
        var src = new StringBuilder(Scaffold);
        var body = new StringBuilder();
        int names = 0;

        string[] concretes = ["int", "bool", "double", "Plain", "Valued", "Colour"];

        int count = 1 + r.Next(3);
        var declared = new List<(string Base, string[] Params, List<(string V, string[] F)> Variants, string[] Inst)>();

        for (int g = 0; g < count; g++)
        {
            int np = 1 + r.Next(2);
            var ps = Enumerable.Range(0, np).Select(i => $"P{i}").ToArray();
            var fieldChoices = new List<string>(ps);
            fieldChoices.AddRange(ps);
            fieldChoices.AddRange(["int", "bool", "double", "Plain", "Valued", "[2]int"]);
            foreach (var d in declared)
                fieldChoices.Add($"{d.Base}[{string.Join(", ", Enumerable.Range(0, d.Params.Length).Select(i => ps[i % ps.Length]))}]");

            var variants = new List<(string V, string[] F)>();
            int nv = 1 + r.Next(3);
            for (int v = 0; v < nv; v++)
            {
                int nf = r.Next(3);
                variants.Add(($"V{v}", Enumerable.Range(0, nf).Select(_ => fieldChoices[r.Next(fieldChoices.Count)]).ToArray()));
            }
            variants.Add(("Nil", []));

            string baseName = $"G{g}";
            var decl = variants.Select(v => v.F.Length == 0
                ? v.V
                : $"{v.V}({string.Join(", ", v.F.Select((t, i) => $"{t} f{i}"))})");
            src.Append($"union {baseName}[{string.Join(", ", ps)}] {{ {string.Join(", ", decl)} }}\n");

            var pick = concretes.OrderBy(_ => r.Next()).Take(np * 2).ToArray();
            string instA = string.Join(", ", pick.Take(np));
            string instB = string.Join(", ", pick.Skip(np).Take(np));
            declared.Add((baseName, ps, variants, [instA, instB]));
            coverage?.Observe(np, variants, fieldChoices);
        }

        foreach (var d in declared)
            foreach (var inst in d.Inst)
            {
                string ty = $"{d.Base}[{inst}]";
                var binding = d.Params.Zip(inst.Split(", "), (p, c) => (p, c)).ToDictionary(x => x.p, x => x.c);

                foreach (var v in d.Variants)
                {
                    string name = $"v{names++}";
                    var args = new List<string>(v.F.Length);
                    foreach (var f in v.F)
                    {
                        string concreteField = SubstituteParams(f, binding);
                        if (concreteField.Contains('[') && !concreteField.StartsWith('['))
                        {
                            string tmp = $"n{names++}";
                            string nb = concreteField[..concreteField.IndexOf('[')];
                            body.Append($"        let {concreteField} {tmp} = {nb}.Nil();\n");
                            args.Add(tmp);
                        }
                        else args.Add(ConcreteValue(concreteField, r));
                    }
                    body.Append($"        let {ty} {name} = {d.Base}.{v.V}({string.Join(", ", args)});\n");
                    body.Append($"        acc = acc + ({name} == {name} ? 1 : 0);\n");

                    var arms = string.Join(" ", d.Variants.Select(w =>
                        $"case {w.V}{(w.F.Length == 0 ? "" : "(" + string.Join(", ", w.F.Select((_, i) => $"b{names++}")) + ")")} " +
                        $"{{ acc = acc + {w.F.Length}; }}"));
                    body.Append($"        match ({name}) {{ {arms} }}\n");
                }
            }

        src.Append("realm kernel { entry func Main() {\n    let int acc = 0;\n");
        src.Append(body);
        src.Append("} }\n");
        return src.ToString();
    }

    /// <summary>
    /// Rewrites a template field's type under a parameter binding: 'P0' becomes its concrete
    /// argument and 'G0[P0, P1]' becomes 'G0[int, bool]'. A fixed array has brackets but names no
    /// template, so the binding lookup matches it first and returns it whole.
    /// </summary>
    private static string SubstituteParams(string field, Dictionary<string, string> binding)
    {
        if (binding.TryGetValue(field, out var direct)) return direct;
        if (!field.Contains('[') || field.StartsWith('[')) return field;

        int open = field.IndexOf('[');
        string b = field[..open];
        var args = field[(open + 1)..^1].Split(", ")
            .Select(a => binding.GetValueOrDefault(a, a));
        return $"{b}[{string.Join(", ", args)}]";
    }

    private static string ConcreteValue(string t, Random r) => t switch
    {
        "int" => r.Next(3).ToString(),
        "bool" => r.Next(2) == 0 ? "true" : "false",
        "double" => $"{r.Next(3)}.5",
        "Colour" => r.Next(2) == 0 ? "Colour.Red" : "Colour.Green",
        "Plain" => "new Plain()",
        "Valued" => "new Valued()",
        "[2]int" => $"[{r.Next(3)}, {r.Next(3)}]",
        _ => "0",
    };

    /// <summary>
    /// Coverage tally for the generic corpus, asserted so the generator cannot drift.
    /// </summary>
    private sealed class GenericCoverage
    {
        public int TwoParams, ParamPayload, NestedTemplate, EmptyVariant, ConcretePayload;

        public void Observe(int np, List<(string V, string[] F)> variants, List<string> choices)
        {
            if (np > 1) TwoParams++;
            if (choices.Any(c => c.Contains('['))) NestedTemplate++;
            foreach (var v in variants)
            {
                if (v.F.Length == 0) EmptyVariant++;
                foreach (var f in v.F)
                {
                    if (f.StartsWith('P')) ParamPayload++;
                    else if (!f.Contains('[')) ConcretePayload++;
                }
            }
        }
    }

    [Fact]
    public void GenericUnionProgramsCompile()
    {
        var cc = FindCompiler();
        using var work = Scratch.Create("appa-gufuzz-");
        var failures = new List<string>();
        var coverage = new GenericCoverage();
        int accepted = 0;

        for (int seed = 1; seed <= Seeds; seed++)
        {
            var src = GenerateGeneric(seed, coverage);
            IrModule module;

            try
            {
                var sources = new SourceSet();
                sources.Add("<gufuzz>", src);
                var diag = new DiagnosticBag(sources);

                Program prog;
                try { prog = SingleFileCompile.Parse(src); }
                catch (ParseException ex)
                {
                    failures.Add($"[seed {seed}] parse failed: {ex.Message}\n{src}");
                    continue;
                }

                var programs = new List<(string path, Program prog)> { ("<gufuzz>", prog) };
                var visible = new Dictionary<string, HashSet<string>> { ["<gufuzz>"] = ["<gufuzz>"] };
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
                sources.Add("<gufuzz>", src);
                files = Layout.Compose(new Emitter(module, new DiagnosticBag(sources)).Build(), module.Symbols);
            }
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
            Assert.Fail($"{failures.Count} of {Seeds} generated generic-union programs failed:\n\n{shown}{more}");
        }

        Assert.Equal(Seeds, accepted);
        Assert.True(coverage.TwoParams > 20, $"too few two-parameter templates: {coverage.TwoParams}");
        Assert.True(coverage.ParamPayload > 50, $"too few payloads of parameter type: {coverage.ParamPayload}");
        Assert.True(coverage.NestedTemplate > 20, $"too few templates nesting another: {coverage.NestedTemplate}");
        Assert.True(coverage.EmptyVariant > 50, $"too few payload-free variants: {coverage.EmptyVariant}");
        Assert.True(coverage.ConcretePayload > 50, $"too few concrete payloads: {coverage.ConcretePayload}");
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
    public void UnionProgramsCompile()
    {
        var cc = FindCompiler();
        using var work = Scratch.Create("appa-ufuzz-");
        var failures = new List<string>();
        var coverage = new Coverage();
        int accepted = 0;

        for (int seed = 1; seed <= Seeds; seed++)
        {
            var src = Generate(seed, coverage);
            IrModule module;

            try
            {
                var sources = new SourceSet();
                sources.Add("<ufuzz>", src);
                var diag = new DiagnosticBag(sources);

                Program prog;
                try { prog = SingleFileCompile.Parse(src); }
                catch (ParseException ex)
                {
                    failures.Add($"[seed {seed}] parse failed: {ex.Message}\n{src}");
                    continue;
                }

                var programs = new List<(string path, Program prog)> { ("<ufuzz>", prog) };
                var visible = new Dictionary<string, HashSet<string>> { ["<ufuzz>"] = ["<ufuzz>"] };
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
                sources.Add("<ufuzz>", src);
                files = Layout.Compose(new Emitter(module, new DiagnosticBag(sources)).Build(), module.Symbols);
            }
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
            Assert.Fail($"{failures.Count} of {Seeds} generated union programs failed:\n\n{shown}{more}");
        }

        Assert.Equal(Seeds, accepted);
        Assert.True(coverage.ManagedPayload > 20, $"too few reference-counted payloads: {coverage.ManagedPayload}");
        Assert.True(coverage.ValuedPayload > 20, $"too few payloads with an '==' overload: {coverage.ValuedPayload}");
        Assert.True(coverage.NestedUnion > 20, $"too few nested-union payloads: {coverage.NestedUnion}");
        Assert.True(coverage.FloatPayload > 20, $"too few floating-point payloads: {coverage.FloatPayload}");
        Assert.True(coverage.ArrayPayload > 20, $"too few fixed-array payloads: {coverage.ArrayPayload}");
        Assert.True(coverage.EnumPayload > 10, $"too few enum payloads: {coverage.EnumPayload}");
        Assert.True(coverage.EmptyVariant > 20, $"too few payload-free variants: {coverage.EmptyVariant}");
        Assert.True(coverage.MultiFieldVariant > 20, $"too few multi-field variants: {coverage.MultiFieldVariant}");
        Assert.True(coverage.MultiUnion > 50, $"too few programs with several unions: {coverage.MultiUnion}");
    }

    /// <summary>
    /// Runs randomly shaped unions against three laws that hold whatever the payloads are, so the
    /// generator predicts nothing: reflexive, '==' and '!=' never agree, and different variants are
    /// never equal - the last catching a payload read from the wrong arm.
    /// </summary>
    [Fact]
    public void UnionsObeyEqualityLaws()
    {
        var gata = HostedRun.FindGataCheckout();
        if (gata == null) { Assert.Skip("no sibling Gata checkout found"); return; }
        var cc = HostedRun.FindCompiler();
        if (cc == null) { Assert.Skip("no host C compiler (cc/gcc/clang) found"); return; }

        var src = new StringBuilder("""
            import Console;
            import String;
            import List;

            class Plain { public int n; public func _init() { self.n = 0; } }
            class Valued {
                public int n;
                public func _init() { self.n = 0; }
                public operator bool func ==(Valued o) { return self.n == o.n; }
            }

            """);
        var body = new StringBuilder();
        int totalPairs = 0;

        for (int seed = 1; seed <= 40; seed++)
        {
            var gen = new Generator(seed);
            var unions = gen.Unions();

            foreach (var u in unions)
            {
                string name = $"S{seed}_{u.Name}";
                var variants = u.Variants.Select(v =>
                    v.FieldTypes.Count == 0
                        ? v.Name
                        : $"{v.Name}({string.Join(", ", v.FieldTypes.Select((t, i) => $"{Hosted(t, seed)} {v.FieldName(i)}"))})");
                src.Append($"union {name} {{ {string.Join(", ", variants)} }}\n");

                string list = $"l{seed}_{u.Name}";
                body.Append($"        let List[{name}] {list} = new List[{name}]();\n");
                var variantOf = new List<int>();
                for (int rep = 0; rep < 2; rep++)
                    for (int vi = 0; vi < u.Variants.Count; vi++)
                    {
                        var v = u.Variants[vi];
                        var args = v.FieldTypes.Select(t => HostedValue(t, seed, gen, unions));
                        body.Append($"        {list}.Add({name}.{v.Name}({string.Join(", ", args)}));\n");
                        variantOf.Add(vi);
                    }

                for (int i = 0; i < variantOf.Count; i++)
                    for (int j = 0; j < variantOf.Count; j++)
                    {
                        totalPairs++;
                        body.Append($"        eq = {list}.Get({i}) == {list}.Get({j});\n");
                        body.Append($"        ne = {list}.Get({i}) != {list}.Get({j});\n");
                        body.Append("        if (eq == ne) { bad = bad + 1; }\n");
                        if (i == j) body.Append("        if (!eq) { bad = bad + 1; }\n");
                        else if (variantOf[i] != variantOf[j]) body.Append("        if (eq) { bad = bad + 1; }\n");
                        body.Append("        checked = checked + 1;\n");
                    }
            }
        }

        src.Append("\nrealm userspace {\n    entry func Main() {\n");
        src.Append("        let bool eq = false;\n        let bool ne = false;\n");
        src.Append("        let int bad = 0;\n        let int checked = 0;\n");
        src.Append(body);
        src.Append("        Console.PrintLine($\"bad={bad} checked={checked}\");\n    }\n}\n");
        Assert.True(totalPairs > 2000, $"only {totalPairs} comparison pairs were generated");

        var r = HostedRun.BuildAndRun(src.ToString(), gata, cc);
        HostedRun.AssertClean(r);
        Assert.Equal($"bad=0 checked={totalPairs}\n", r.Output);
    }

    /// <summary>
    /// Maps a generated payload type onto one usable in a Hosted program. The stub-only classes
    /// stay, nested unions gain the per-seed prefix their declarations were given.
    /// </summary>
    private static string Hosted(string type, int seed) =>
        type.StartsWith('U') ? $"S{seed}_{type}" : type == "Colour" ? "int" : type;

    /// <summary>
    /// Builds a literal for a payload type in the Hosted program.
    /// </summary>
    private static string HostedValue(string type, int seed, Generator gen, List<Union> unions)
    {
        switch (type)
        {
            case "int": return gen.Next(3).ToString();
            case "int64": return $"({gen.Next(3)} as int64)";
            case "bool": return gen.Next(2) == 0 ? "true" : "false";
            case "char": return "'a'";
            case "float": return $"{gen.Next(2)}.5f";
            case "double": return $"{gen.Next(2)}.5";
            case "Colour": return gen.Next(2).ToString();
            case "Plain": return "new Plain()";
            case "Valued": return "new Valued()";
            case "[2]int": return $"[{gen.Next(3)}, {gen.Next(3)}]";
            case "[3]bool": return "[true, false, true]";
            default:
            {
                var nested = unions.First(u => u.Name == type);
                var v = nested.Variants.FirstOrDefault(x => x.FieldTypes.Count == 0) ?? nested.Variants[0];
                var args = v.FieldTypes.Select(t => HostedValue(t, seed, gen, unions));
                return $"S{seed}_{nested.Name}.{v.Name}({string.Join(", ", args)})";
            }
        }
    }
}
