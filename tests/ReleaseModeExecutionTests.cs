namespace Appa.Tests;

/// <summary>
/// The same program built twice - Debug and Release - and required to compute the same answers.
/// </summary>
public class ReleaseModeExecutionTests
{
    /// <summary>
    /// The program, one entry per file. Shared with the file-split equivalence test, which builds
    /// the same source as a single file and requires the same answers.
    /// </summary>
    internal static Dictionary<string, string> Files() => new()
    {
        ["census.g"] = """
            import Console;
            import String;

            class Tracked {
                public int v;
                func _init(int v) { self.v = v; }
                func _deinit() { Console.PrintLine($"drop {self.v}"); }
            }

            class Vec {
                public int x;
                func _init(int x) { self.x = x; }
                public operator Vec func +(Vec o) { return new Vec(self.x + o.x); }
                public operator bool func ==(Vec o) { return self.x == o.x; }
            }
            """,

        ["shapes.g"] = """
            import "src/census.g";

            union Shape { None, One(Tracked t), Pair(Tracked a, Tracked b) }

            int func Weigh(Shape s) {
                match (s) {
                    case None { return 0; }
                    case One(t) { return t.v; }
                    case Pair(a, b) { return a.v + b.v; }
                }
            }

            class Box[T] { public T v; func _init(T x) { self.v = x; } public T func Get() { return self.v; } }
            T func Echo[T](T x) { return x; }

            throws int func MaybeHalve(int n) { if (n % 2 == 1) { throw; } return n / 2; }
            """,

        ["main.g"] = """
            import Console;
            import String;
            import List;
            import "src/census.g";
            import "src/shapes.g";

            realm userspace {
                entry func Main() {
                    // Plain ARC through a generic, with the object outliving one scope.
                    {
                        let Box[Tracked] b = new Box[Tracked](new Tracked(1));
                        Console.PrintLine($"box {b.Get().v}");
                    }

                    // A managed union, reassigned so the first payload has to be released.
                    {
                        let Shape s = Shape.Pair(new Tracked(2), new Tracked(3));
                        Console.PrintLine($"pair {Weigh(s)}");
                        s = Shape.One(new Tracked(4));
                        Console.PrintLine($"one {Weigh(s)}");
                    }

                    // Operator overloading and equality, where the temporary is the interesting one.
                    {
                        let Vec a = new Vec(2);
                        let Vec b = new Vec(5);
                        let Vec c = a + b;
                        Console.PrintLine($"vec {c.x} {a == b} {c == new Vec(7)}");
                    }

                    // A container, iterated - the refcount of each element is touched twice.
                    {
                        let List[Tracked] xs = new List[Tracked]();
                        for (let int i = 0; i < 4; i++) { xs.Add(new Tracked(10 + i)); }
                        let int total = 0;
                        for x in xs { total = total + x.v; }
                        Console.PrintLine($"list {xs.Length()} {total}");
                    }

                    // Throws, caught both ways, and a defer whose body allocates.
                    {
                        let int acc = 0;
                        for (let int i = 0; i < 5; i++) {
                            let int h = MaybeHalve(i) catch { assign -1; };
                            acc = acc + h;
                        }
                        let int t = 0;
                        try { t = MaybeHalve(7); } catch { t = -9; }
                        Console.PrintLine($"throws {acc} {t}");
                    }

                    // Integer edges the optimiser is allowed to assume things about.
                    {
                        let int64 big = 9007199254740993;
                        let int neg = -2147483647;
                        let uint u = 4294967295;
                        Console.PrintLine($"nums {big} {neg} {u} {neg / 3} {neg % 3}");
                    }

                    // Strings built at runtime, so the comparison is not a folded literal.
                    {
                        let String s = "ab";
                        for (let int i = 0; i < 3; i++) { s = s + "c"; }
                        Console.PrintLine($"str {s} {s.Length()}");
                    }

                    Console.PrintLine("end");
                }
            }
            """,
    };

    [Fact]
    public void ReleaseComputesWhatDebugComputes()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompilerAcceptingReleaseFlags();
        if (gata == null || cc == null) { Assert.Skip("no Gata checkout, or no C compiler taking the release flags"); return; }

        var files = Files();
        var debug = HostedRun.BuildAndRun(files, gata, cc, release: false);
        HostedRun.AssertClean(debug);

        var release = HostedRun.BuildAndRun(files, gata, cc, release: true);
        Assert.True(release.ExitCode == 0, $"the release build exited {release.ExitCode}:\n{release.Output}");

        var d = Lines(debug.Output);
        var r = Lines(release.Output);

        Assert.True(d.Count > 0, "the debug run printed nothing; the program stopped working");
        Assert.Contains("end", d);
        Assert.Contains("end", r);

        if (d.SequenceEqual(r)) return;

        var diff = new List<string>();
        for (int i = 0; i < Math.Max(d.Count, r.Count); i++)
        {
            string a = i < d.Count ? d[i] : "<none>";
            string b = i < r.Count ? r[i] : "<none>";
            if (a != b) diff.Add($"  line {i + 1}: debug '{a}' vs release '{b}'");
        }
        Assert.Fail($"Release computed something Debug did not ({d.Count} vs {r.Count} lines):\n" +
                    string.Join("\n", diff.Take(20)));
    }

    /// <summary>
    /// The program's own output lines, with sanitizer chatter dropped.
    /// </summary>
    private static List<string> Lines(string output) =>
        [.. output.Split('\n').Select(l => l.Trim())
                  .Where(l => l.Length > 0 && !l.StartsWith('=') && !l.Contains("Sanitizer", StringComparison.Ordinal))];
}
