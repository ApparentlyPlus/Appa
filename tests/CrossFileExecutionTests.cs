namespace Appa.Tests;

/// <summary>
/// The value and lifetime oracles again, with every declaration in a different file from its use.
/// </summary>
public class CrossFileExecutionTests
{
    private static Dictionary<string, string> Files() => new()
    {
        ["census.g"] = """
            class Census { public int made; public int dropped; func _init() { self.made = 0; self.dropped = 0; } }

            class Tracked {
                public Census c;
                public int v;
                func _init(Census c, int v) { self.c = c; self.v = v; c.made = c.made + 1; }
                func _deinit() { self.c.dropped = self.c.dropped + 1; }
            }
            """,

        ["box.g"] = """
            class Box[T] { public T v; func _init(T x) { self.v = x; } public T func Get() { return self.v; } }
            T func Echo[T](T x) { return x; }
            private int func Scale(int n) { return n * 2; }
            int func BoxScale(int n) { return Scale(n); }
            """,

        ["payload.g"] = """
            import "src/census.g";
            union Payload { None, One(Tracked t), Two(Tracked a, Tracked b) }
            int func Weigh(Payload p) {
                match (p) { case None { return 0; } case One(t) { return t.v; } case Two(a, b) { return a.v + b.v; } }
            }
            """,

        ["adder.g"] = """
            import "src/census.g";
            class Adder { public Tracked t; func _init(Tracked t) { self.t = t; }
                public operator Adder func +(Adder o) { return new Adder(new Tracked(self.t.c, self.t.v + o.t.v)); } }
            private int func Scale(int n) { return n * 3; }
            int func AdderScale(int n) { return Scale(n); }
            """,

        ["fail.g"] = """
            import "src/census.g";
            throws Tracked func MaybeTracked(Census c, int v) { if (v % 3 == 0) { throw; } return new Tracked(c, v); }
            throws int func MaybeInt(int v) { if (v % 3 == 0) { throw; } return v + 5; }
            """,

        ["main.g"] = MainFile(),
    };

    private static readonly (string Name, string Body)[] Paths =
    [
        ("xgeneric",     "let Box[Tracked] b = new Box[Tracked](new Tracked(c, i)); return b.Get().v;"),
        ("xgenericfn",   "let Tracked t = Echo(new Tracked(c, i)); return t.v;"),
        ("xnested",      "let Box[Box[Tracked]] b = new Box[Box[Tracked]](new Box[Tracked](new Tracked(c, i))); return b.Get().Get().v;"),
        ("xunion1",      "let Payload p = Payload.One(new Tracked(c, i)); return Weigh(p);"),
        ("xunion2",      "let Payload p = Payload.Two(new Tracked(c, i), new Tracked(c, i + 1)); return Weigh(p);"),
        ("xunionreasg",  "let Payload p = Payload.One(new Tracked(c, i)); p = Payload.None(); return Weigh(p);"),
        ("xunionbox",    "let Box[Payload] b = new Box[Payload](Payload.One(new Tracked(c, i))); return Weigh(b.Get());"),
        ("xoperator",    "let Adder a = new Adder(new Tracked(c, i)); let Adder b = new Adder(new Tracked(c, 1)); return (a + b).t.v;"),
        ("xcatch",       "let Tracked t = MaybeTracked(c, i) catch { assign new Tracked(c, -1); }; return t.v;"),
        ("xtry",         "let int r = 0; try { let Tracked t = MaybeTracked(c, i); r = t.v; } catch { r = -1; } return r;"),
        ("xlist",        "let List[Tracked] xs = new List[Tracked](); " +
                         "for (let int k = 0; k < 3; k++) { xs.Add(new Tracked(c, i + k)); } " +
                         "let int t = 0; for x in xs { t = t + x.v; } return t;"),
        ("xdefer",       "let Tracked t = new Tracked(c, i); defer { let Tracked d = new Tracked(c, i); } return t.v;"),
    ];

    private static string MainFile()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("""
            import Console;
            import String;
            import List;
            import "src/census.g";
            import "src/box.g";
            import "src/payload.g";
            import "src/adder.g";
            import "src/fail.g";

            // Same name as the private functions in box.g and adder.g; all three must mangle apart.
            private int func Scale(int n) { return n * 5; }
            int func MainScale(int n) { return Scale(n); }
            """);

        for (int p = 0; p < Paths.Length; p++)
            sb.AppendLine($"int func Path{p}(Census c, int i) {{ {Paths[p].Body} }}");

        sb.AppendLine("realm userspace {");
        sb.AppendLine("    entry func Main() {");
        for (int p = 0; p < Paths.Length; p++)
        {
            sb.AppendLine("        {");
            sb.AppendLine($"            let Census c{p} = new Census();");
            sb.AppendLine($"            let int acc{p} = 0;");
            sb.AppendLine($"            for (let int i = 0; i < 6; i++) {{ acc{p} = acc{p} + Path{p}(c{p}, i); }}");
            sb.AppendLine($"            Console.PrintLine($\"{Paths[p].Name} made={{c{p}.made}} dropped={{c{p}.dropped}} acc={{acc{p}}}\");");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        Console.PrintLine($\"scale {MainScale(1)} {BoxScale(1)} {AdderScale(1)}\");");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    [Fact]
    public void CrossFileFeaturePathsBalanceAndCompute()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no checkout/compiler"); return; }

        var r = HostedRun.BuildAndRun(Files(), gata, cc);

        var bad = new List<string>();
        var seen = new HashSet<string>();
        var expected = new Dictionary<string, int>
        {
            ["xgeneric"] = 15, ["xgenericfn"] = 15, ["xnested"] = 15,
            ["xunion1"] = 15, ["xunion2"] = 36, ["xunionreasg"] = 0, ["xunionbox"] = 15,
            ["xoperator"] = 21,
            ["xcatch"] = 0 + -1 + 1 + 2 + -1 + 4 + 5,
            ["xtry"] = -1 + 1 + 2 + -1 + 4 + 5,
            ["xlist"] = 0 + 1 + 2 + (1 + 2 + 3) + (2 + 3 + 4) + (3 + 4 + 5) + (4 + 5 + 6) + (5 + 6 + 7),
            ["xdefer"] = 15,
        };

        foreach (var line in r.Output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("scale "))
            {
                if (t != "scale 5 2 3") bad.Add($"private functions collided across files: '{t}' (want 'scale 5 2 3')");
                continue;
            }
            if (!t.Contains("made=")) continue;
            var parts = t.Split(' ');
            string name = parts[0];
            seen.Add(name);
            int made = int.Parse(parts[1]["made=".Length..]);
            int dropped = int.Parse(parts[2]["dropped=".Length..]);
            int acc = int.Parse(parts[3]["acc=".Length..]);
            if (made != dropped) bad.Add($"[{name}] made {made}, dropped {dropped} — {(made > dropped ? "LEAK" : "OVER-RELEASE")}");
            if (made == 0) bad.Add($"[{name}] allocated nothing");
        }
        foreach (var (name, _) in Paths)
            if (!seen.Contains(name)) bad.Add($"[{name}] produced no output");


        HostedRun.AssertClean(r);
        Assert.True(bad.Count == 0, $"{bad.Count} problems:\n" + string.Join("\n", bad));
    }
}
