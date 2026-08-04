namespace Appa.Tests;

using System.Text;

/// <summary>
/// A differential over crossed language features: does a program that chains several of them still
/// compute the right number?
/// </summary>
public class CrossFeatureDifferentialTests
{
    private sealed record Frag(
        string Name,
        string Decls,
        Func<string, string> Apply,
        Func<int, int> Eval);

    private static List<Frag> Frags() =>
    [
        new("add","", x => $"(({x}) + 7)", v => v + 7),
        new("mul","", x => $"(({x}) * 3)", v => v * 3),
        new("ternary","", x => $"((({x}) % 2) == 0 ? (({x}) / 2) : (({x}) * 2 + 1))", v => v % 2 == 0 ? v / 2 : v * 2 + 1),
        new("cast64","", x => $"((({x}) as int64) * (2 as int64)) as int",v => (int)((long)v * 2L)),
        new("castdouble","", x => $"(((({x}) as double) * 1.5) as int)", v => (int)((double)v * 1.5)),
        new("charmath", "", x => $"(((('a' as int) + ({x}) % 5) as char) as int)", v => 'a' + v % 5),
        new("method", "class Acc { public int n; func _init(int s) { self.n = s; } " +
            "public int func Bump(int d) { self.n = self.n + d; return self.n; } }", x => $"NewAcc({x})", v => v + 4),
        new("genclass", "class Hold[T] { public T v; func _init(T x) { self.v = x; } " + "public T func Get() { return self.v; } }",
            x => $"(new Hold[int](({x}) + 1)).Get()", v => v + 1),
        new("genfunc", "T func Ident[T](T a) { return a; } int func Doubler(int a) { return a * 2; }",
            x => $"Ident(Doubler({x}))", v => v * 2),
        new("union", "union Sig { Off, On(int lvl) }\n" + "int func Weigh(Sig s) { match (s) { case Off { return 0; } case On(l) { return l + 1; } } }",
            x => $"Weigh(((({x}) % 3) == 0 ? Sig.Off() : Sig.On({x})))", v => v % 3 == 0 ? 0 : v + 1),
        new("enumsw", "enum Col { Red, Green, Blue }\n" + "int func ColVal(Col c) { switch (c) { case Col.Red { return 1; } " +
            "case Col.Green { return 2; } default { return 3; } } }", x => $"ColVal(((({x}) % 2) == 0 ? Col.Red : Col.Blue))", v => v % 2 == 0 ? 1 : 3),
        new("funcptr", "int func Tripler(int a) { return a * 3; }", x => $"CallFp(Tripler, {x})",v => v * 3),
        new("throwsok", "throws int func MayFail(int a) { if (a < 0) { throw; } return a + 5; }\n" +
            "int func CatchOk(int a) { let int r = MayFail(a) catch { assign -1; }; return r; }", x => $"CatchOk({x})", v => v >= 0 ? v + 5 : -1),
        new("throwsfail", "throws int func AlwaysFail(int a) { if (a > -999999) { throw; } return a; }\n" +
            "int func CatchFail(int a) { let int r = AlwaysFail(a) catch { assign a + 2; }; return r; }", x => $"CatchFail({x})",v => v + 2),
        new("trycatch", "throws int func MayFail2(int a) { if (a < 0) { throw; } return a + 6; }", x => $"TryIt({x})",v => v >= 0 ? v + 6 : -2),
        new("deferloop", "", x => $"DeferSum({x})", v => { int s = 0; for (int i = 0; i < 3; i++) s += v + i; return s; }),
        new("recurse", "", x => $"Fib(((({x}) % 10) + 10) % 10)", v => { int n = ((v % 10) + 10) % 10; int a = 0, b = 1; 
            for (int i = 0; i < n; i++) (a, b) = (b, a + b); return a; }),
        new("refparam", "void func AddTo(ref int slot, int d) { slot = slot + d; }", x => $"ViaRef({x})", v => v + 9),
        new("array", "", x => $"ArrSum({x})", v => v + (v + 1) + (v + 2) + (v + 3)),
        new("oper", "class Vec { public int n; func _init(int a) { self.n = a; } " + "public operator Vec func +(Vec o) { return new Vec(self.n + o.n); } }",
            x => $"((new Vec({x})) + (new Vec(3))).n", v => v + 3),
        new("strlen", "", x => $"StrLen({x})", v => $"v{v}".Length),
        new("listsum", "", x => $"ListSum({x})", v => v + (v + 1) + (v + 2)),
        new("unsafeptr", "", x => $"ViaPtr({x})", v => v + 11),
    ];

    /// <summary>
    /// Helpers the fragments call, kept out of the fragment table so it stays readable.
    /// </summary>
    private const string Helpers = """
        import Console;
        import String;
        import List;

        int func Norm(int a) { return a % 1000; }
        int func NewAcc(int s) { let Acc a = new Acc(s); return a.Bump(4); }
        int func CallFp(func(int) -> int f, int a) { return f(a); }
        int func TryIt(int a) { let int r = 0; try { r = MayFail2(a); } catch { r = -2; } return r; }
        int func DeferSum(int a) {
            let int total = 0;
            for (let int i = 0; i < 3; i++) { defer { } total = total + a + i; }
            return total;
        }
        int func Fib(int n) { if (n < 2) { return n; } return Fib(n - 1) + Fib(n - 2); }
        int func ViaRef(int a) { let int slot = a; AddTo(ref slot, 9); return slot; }
        int func ArrSum(int a) {
            let [4]int xs = [a, a + 1, a + 2, a + 3];
            let int t = 0;
            for (let int i = 0; i < 4; i++) { t = t + xs[i]; }
            return t;
        }
        int func StrLen(int a) { let String s = $"v{a}"; return s.Length(); }
        int func ListSum(int a) {
            let List[int] xs = new List[int]();
            xs.Add(a); xs.Add(a + 1); xs.Add(a + 2);
            let int t = 0;
            for v in xs { t = t + v; }
            return t;
        }
        int func ViaPtr(int a) { let int slot = a; unsafe { let int* p = &slot; *p = *p + 11; } return slot; }
        """;

    private sealed record GenCase(string Name, List<Frag> Chain, string Body, int Expected);

    /// <summary>
    /// Builds N cases, each a chain of `depth` fragments applied to a seed.
    /// </summary>
    private static List<GenCase> Generate(int count, int depth, int seed)
    {
        var frags = Frags();
        var rng = new Random(seed);
        var cases = new List<GenCase>();
        for (int i = 0; i < count; i++)
        {
            var chain = new List<Frag>();
            for (int d = 0; d < depth; d++) chain.Add(frags[rng.Next(frags.Count)]);

            int start = rng.Next(-20, 40);
            string expr = start.ToString();
            int val = start;
            foreach (var f in chain)
            {
                expr = $"Norm({f.Apply(expr)})";
                val = f.Eval(val) % 1000;
            }

            cases.Add(new GenCase($"c{i}_{string.Join("_", chain.Select(c => c.Name))}", chain, expr, val));
        }
        return cases;
    }

    [Fact]
    public void CrossFeatureDifferential()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no checkout/compiler"); return; }

        var cases = Generate(count: 600, depth: 5, seed: 20260731);
        var decls = new HashSet<string>(
            cases.SelectMany(c => c.Chain).Select(f => f.Decls).Where(s => s.Length > 0));
        var sb = new StringBuilder();
        sb.AppendLine(Helpers);
        foreach (var d in decls) sb.AppendLine(d);
        for (int i = 0; i < cases.Count; i++)
            sb.AppendLine($"int func Case{i}() {{ return {cases[i].Body}; }}");

        sb.AppendLine("realm userspace {");
        sb.AppendLine("    entry func Main() {");
        for (int i = 0; i < cases.Count; i++)
            sb.AppendLine($"        Console.PrintLine($\"{i}={{Case{i}()}}\");");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        var r = HostedRun.BuildAndRun(sb.ToString(), gata, cc);

        var lines = r.Output.Split('\n');
        var got = new Dictionary<int, string>();
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq > 0 && int.TryParse(line[..eq], out int idx)) got[idx] = line[(eq + 1)..].Trim();
        }

        var bad = new List<string>();
        for (int i = 0; i < cases.Count; i++)
        {
            if (!got.TryGetValue(i, out var actual)) { bad.Add($"[{cases[i].Name}] no output"); continue; }
            if (actual != cases[i].Expected.ToString())
                bad.Add($"[{cases[i].Name}] expected {cases[i].Expected}, got {actual}\n    {cases[i].Body}");
        }


        Assert.True(bad.Count == 0, $"{bad.Count} of {cases.Count} mismatched:\n" + string.Join("\n", bad.Take(15)));
    }
}
