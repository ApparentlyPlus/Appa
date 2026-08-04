namespace Appa.Tests;

/// <summary>
/// Reference-counting lifetimes across every feature that can own an object.
/// </summary>
public class ArcLifetimeTests
{
    private const string Prelude = """
        import Console;
        import String;
        import List;

        class Census { public int made; public int dropped; func _init() { self.made = 0; self.dropped = 0; } }

        class Tracked {
            public Census c;
            public int v;
            func _init(Census c, int v) { self.c = c; self.v = v; c.made = c.made + 1; }
            func _deinit() { self.c.dropped = self.c.dropped + 1; }
        }

        class Box[T] { public T v; func _init(T x) { self.v = x; } public T func Get() { return self.v; } }

        union Payload { None, One(Tracked t), Two(Tracked a, Tracked b) }

        class Adder { public Tracked t; func _init(Tracked t) { self.t = t; }
            public operator Adder func +(Adder o) { return new Adder(new Tracked(self.t.c, self.t.v + o.t.v)); } }

        throws Tracked func MaybeTracked(Census c, int v) {
            if (v % 3 == 0) { throw; }
            return new Tracked(c, v);
        }
        """;

    /// <summary>
    /// Each path allocates, uses and drops Tracked objects through one language feature.
    /// </summary>
    private static readonly (string Name, string Body)[] Paths =
    [
        ("plain",        "let Tracked t = new Tracked(c, i); return t.v;"),
        ("reassign",     "let Tracked t = new Tracked(c, i); t = new Tracked(c, i + 1); return t.v;"),
        ("selfassign",   "let Tracked t = new Tracked(c, i); t = t; return t.v;"),
        ("ternary",      "let Tracked t = i % 2 == 0 ? new Tracked(c, i) : new Tracked(c, i + 1); return t.v;"),
        ("generic",      "let Box[Tracked] b = new Box[Tracked](new Tracked(c, i)); return b.Get().v;"),
        ("nestedgeneric","let Box[Box[Tracked]] b = new Box[Box[Tracked]](new Box[Tracked](new Tracked(c, i))); return b.Get().Get().v;"),
        ("union1",       "let Payload p = Payload.One(new Tracked(c, i)); " +
                         "match (p) { case None { return 0; } case One(t) { return t.v; } case Two(a, b) { return a.v + b.v; } }"),
        ("union2",       "let Payload p = Payload.Two(new Tracked(c, i), new Tracked(c, i + 1)); " +
                         "match (p) { case None { return 0; } case One(t) { return t.v; } case Two(a, b) { return a.v + b.v; } }"),
        ("unionreassign","let Payload p = Payload.One(new Tracked(c, i)); p = Payload.None(); " +
                         "match (p) { case None { return 0; } case One(t) { return t.v; } case Two(a, b) { return a.v + b.v; } }"),
        ("list",         "let List[Tracked] xs = new List[Tracked](); " +
                         "for (let int k = 0; k < 4; k++) { xs.Add(new Tracked(c, i + k)); } " +
                         "let int t = 0; for x in xs { t = t + x.v; } return t;"),
        ("defer",        "let Tracked t = new Tracked(c, i); defer { let Tracked d = new Tracked(c, i); } return t.v;"),
        ("earlyreturn",  "let Tracked t = new Tracked(c, i); if (i % 2 == 0) { let Tracked u = new Tracked(c, i); return u.v; } return t.v;"),
        ("loopchurn",    "let int t = 0; for (let int k = 0; k < 5; k++) { let Tracked x = new Tracked(c, k); t = t + x.v; } return t;"),
        ("catchassign",  "let Tracked t = MaybeTracked(c, i) catch { assign new Tracked(c, -1); }; return t.v;"),
        ("trycatch",     "let int r = 0; try { let Tracked t = MaybeTracked(c, i); r = t.v; } catch { r = -1; } return r;"),
        ("throwspast",   "let Tracked live = new Tracked(c, i); let Tracked t = MaybeTracked(c, i) catch { assign new Tracked(c, -1); }; return live.v + t.v;"),
        ("operator",     "let Adder a = new Adder(new Tracked(c, i)); let Adder b = new Adder(new Tracked(c, 1)); return (a + b).t.v;"),
        ("field",        "let Box[Tracked] b = new Box[Tracked](new Tracked(c, i)); b.v = new Tracked(c, i + 1); return b.v.v;"),
        ("interp",       "let Tracked t = new Tracked(c, i); let String s = $\"t{t.v}\"; return s.Length();"),
        ("passaround",   "let Tracked t = new Tracked(c, i); return Consume(t) + Borrow(t);"),
        ("nestedcall",   "return Consume(new Tracked(c, i));"),
        ("switchcase",   "let Tracked t = new Tracked(c, i); switch (i % 2) { case 0 { return t.v; } default { return t.v + 1; } }"),
        ("whilebreak",   "let int t = 0; let int k = 0; while (true) { let Tracked x = new Tracked(c, k); t = t + x.v; k = k + 1; if (k > 3) { break; } } return t;"),
        ("unsafeblock",  "let Tracked t = new Tracked(c, i); let int r = 0; unsafe { r = t.v; } return r;"),
    ];

    private const string Extra = """
        int func Consume(Tracked t) { return t.v; }
        int func Borrow(Tracked t) { return t.v * 2; }
        """;

    [Fact]
    public void CensusBalances()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no checkout/compiler"); return; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Prelude);
        sb.AppendLine(Extra);

        // One function per path, each with its own census, so a mismatch names the feature.
        for (int p = 0; p < Paths.Length; p++)
            sb.AppendLine($"int func Path{p}(Census c, int i) {{ {Paths[p].Body} }}");

        sb.AppendLine("realm userspace {");
        sb.AppendLine("    entry func Main() {");
        for (int p = 0; p < Paths.Length; p++)
        {
            sb.AppendLine($"        {{");
            sb.AppendLine($"            let Census c{p} = new Census();");
            sb.AppendLine($"            let int acc{p} = 0;");
            sb.AppendLine($"            for (let int i = 0; i < 6; i++) {{ acc{p} = acc{p} + Path{p}(c{p}, i); }}");
            sb.AppendLine($"            Console.PrintLine($\"{Paths[p].Name} made={{c{p}.made}} dropped={{c{p}.dropped}} acc={{acc{p}}}\");");
            sb.AppendLine($"        }}");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");

        var r = HostedRun.BuildAndRun(sb.ToString(), gata, cc);

        var bad = new List<string>();
        var seen = new HashSet<string>();
        foreach (var line in r.Output.Split('\n'))
        {
            var t = line.Trim();
            if (!t.Contains("made=")) continue;
            var parts = t.Split(' ');
            string name = parts[0];
            seen.Add(name);
            int made = int.Parse(parts[1]["made=".Length..]);
            int dropped = int.Parse(parts[2]["dropped=".Length..]);
            if (made != dropped) bad.Add($"[{name}] made {made}, dropped {dropped} — {(made > dropped ? "LEAK" : "OVER-RELEASE")}");
            if (made == 0) bad.Add($"[{name}] allocated nothing; the path did not run");
        }

        foreach (var (name, _) in Paths)
            if (!seen.Contains(name)) bad.Add($"[{name}] produced no output");


        HostedRun.AssertClean(r);
        Assert.True(bad.Count == 0, $"{bad.Count} unbalanced paths:\n" + string.Join("\n", bad));
    }

    /// <summary>
    /// Owned locals are released in LIFO order, the reverse of declaration.
    /// </summary>
    [Fact]
    public void ReverseReleaseOrder()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no checkout/compiler"); return; }

        var r = HostedRun.BuildAndRun("""
            import Console;
            import String;

            class Node {
                public int v;
                func _init(int v) { self.v = v; }
                func _deinit() { Console.PrintLine($"drop {self.v}"); }
            }

            void func Straight() {
                let Node a = new Node(1);
                let Node b = new Node(2);
                let Node c = new Node(3);
                Console.PrintLine("straight-end");
            }

            void func Nested() {
                let Node a = new Node(4);
                {
                    let Node b = new Node(5);
                    let Node c = new Node(6);
                    Console.PrintLine("inner-end");
                }
                Console.PrintLine("outer-end");
            }

            void func EarlyReturn() {
                let Node a = new Node(7);
                let Node b = new Node(8);
                Console.PrintLine("before-return");
                return;
            }

            void func Loop() {
                for (let int i = 0; i < 2; i++) {
                    let Node a = new Node(10 + i * 2);
                    let Node b = new Node(11 + i * 2);
                    Console.PrintLine($"iter {i}");
                }
            }

            realm userspace {
                entry func Main() { Straight(); Nested(); EarlyReturn(); Loop(); }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        var lines = r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        Assert.Equal(
        [
            "straight-end", "drop 3", "drop 2", "drop 1",
            "inner-end", "drop 6", "drop 5", "outer-end", "drop 4",
            "before-return", "drop 8", "drop 7",
            "iter 0", "drop 11", "drop 10",
            "iter 1", "drop 13", "drop 12",
        ], lines);
    }

    /// <summary>
    /// 'for x in' over a fixed array of managed elements must retain the element into the loop
    /// variable, because the loop variable is an owned local and its scope-exit release will run
    /// either way.
    /// </summary>
    [Fact]
    public void FixedArrayIterationHoldsOn()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no checkout/compiler"); return; }

        var r = HostedRun.BuildAndRun("""
            import Console;
            import String;

            class Item {
                public int v;
                func _init(int v) { self.v = v; }
                func _deinit() { Console.PrintLine($"drop {self.v}"); }
            }

            realm userspace {
                entry func Main() {
                    let [3]Item xs = default([3]Item);
                    xs[0] = new Item(1);
                    xs[1] = new Item(2);
                    xs[2] = new Item(3);

                    // Two passes. One pass alone would not tell a premature free from a correct
                    // run: the read that faults is the one after the element was dropped.
                    for x in xs { Console.PrintLine($"a{x.v}"); }
                    for x in xs { Console.PrintLine($"b{x.v}"); }

                    // And the values must survive being iterated, not merely be readable.
                    let int total = 0;
                    for x in xs { total = total + x.v; }
                    Console.PrintLine($"total={total}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        var lines = r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.DoesNotContain(lines, l => l.StartsWith("drop ", StringComparison.Ordinal));
        Assert.Equal(["a1", "a2", "a3", "b1", "b2", "b3", "total=6"], lines);
    }

    /// <summary>
    /// A field's inline initialiser must actually reach the object.
    /// </summary>
    [Fact]
    public void InlineFieldInit()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no checkout/compiler"); return; }

        var r = HostedRun.BuildAndRun("""
            import Console;
            import String;

            class Config {
                public n = 7;
                public flag = true;
                public ch = 'z';
                public d = 1.5;
                public int unset;
                func _init() { }
            }

            class Mixed {
                public kept = 3;
                public int fromCtor;
                public overwritten = 1;
                func _init(int m) { self.fromCtor = m; self.overwritten = 2; }
            }

            realm userspace {
                entry func Main() {
                    let Config c = new Config();
                    Console.PrintLine($"n={c.n} flag={c.flag} ch={c.ch} d={c.d} unset={c.unset}");
                    let Mixed m = new Mixed(9);
                    Console.PrintLine($"kept={m.kept} fromCtor={m.fromCtor} overwritten={m.overwritten}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        var lines = r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        Assert.Equal(
        [
            "n=7 flag=true ch=z d=1.5 unset=0",
            "kept=3 fromCtor=9 overwritten=2",
        ], lines);
    }
}
