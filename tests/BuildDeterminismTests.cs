namespace Appa.Tests;

/// <summary>
/// The same source, compiled by two separate <c>appa</c> processes, must emit byte-identical C.
/// </summary>
public class BuildDeterminismTests
{
    /// <summary>
    /// Broad on purpose: array and function-pointer types and unions (which the emitter orders
    /// through a dictionary), several generic templates stamped over several arguments, overload
    /// sets, operators, process state, and containers - the passes with the most name-keyed
    /// bookkeeping between them.
    /// </summary>
    private static Dictionary<string, string> Files() => new()
    {
        ["types.g"] = """
            import String;

            class A { public int v; func _init(int v) { self.v = v; }
                public operator A func +(A o) { return new A(self.v + o.v); }
                public operator bool func ==(A o) { return self.v == o.v; } }
            class B { public String s; func _init(String s) { self.s = s; } }
            class Box[T] { public T v; func _init(T x) { self.v = x; } public T func Get() { return self.v; } }

            union Shape { None, One(A a), Pair(A a, B b) }
            union Tag[T] { Empty, Full(T v) }

            enum Colour { Red, Green, Blue }
            """,

        ["funcs.g"] = """
            import "src/types.g";

            int func Pick(int a) { return a; }
            int func Pick(int a, int b) { return a + b; }
            int func Pick(int a, int b, int c) { return a + b + c; }

            T func Echo[T](T x) { return x; }
            T func Wrap[T](T x) { let Box[T] b = new Box[T](x); return b.Get(); }

            int func Sum([4]int xs) { return xs[0] + xs[1] + xs[2] + xs[3]; }
            int func Apply(func(int) -> int f, int n) { return f(n); }
            int func Double(int n) { return n * 2; }
            """,

        ["main.g"] = """
            import Console;
            import String;
            import List;
            import Sync;
            import "src/types.g";
            import "src/funcs.g";

            realm userspace {
                background process P {
                    let AtomicInt hits = new AtomicInt();
                    let int seed = 3;
                    int func Bump() { hits.Increment(); return seed; }
                    thread T { entry func Run() { let int z = Bump(); hits.Add(z); } }
                }

                entry func Main() {
                    let int acc = Pick(1) + Pick(1, 2) + Pick(1, 2, 3);
                    acc = acc + Echo(4) + Wrap(5);
                    let Box[A] ba = new Box[A](new A(6));
                    let Box[Box[int]] bb = new Box[Box[int]](new Box[int](7));
                    acc = acc + ba.Get().v + bb.Get().Get();

                    let Shape s = Shape.Pair(new A(8), new B("b"));
                    match (s) { case None { } case One(a) { acc = acc + a.v; } case Pair(a, b) { acc = acc + a.v; } }

                    let Tag[int] t = Tag[int].Full(9);
                    match (t) { case Empty { } case Full(v) { acc = acc + v; } }

                    let [4]int xs = default([4]int);
                    acc = acc + Sum(xs) + Apply(Double, 10);

                    let List[A] la = new List[A]();
                    la.Add(new A(11));
                    let Colour c = Colour.Green;
                    acc = acc + la.Length() + (c as int);

                    Console.PrintLine($"acc={acc}");
                }
            }
            """,
    };

    /// <summary>
    /// Transpiles into <paramref name="dest"/> through the real CLI, in a process of its own.
    /// </summary>
    private static void BuildInto(string dest, string gata)
    {
        Directory.CreateDirectory(Path.Combine(dest, "src"));
        foreach (var (name, text) in Files())
            File.WriteAllText(Path.Combine(dest, "src", name), text);
        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), Path.Combine(dest, "env.g"));
        File.WriteAllText(Path.Combine(dest, "host.gconf"), """
            <appa>
                <ProjectName>host</ProjectName>
                <TargetBackend>Hosted</TargetBackend>
                <BuildMode>Debug</BuildMode>
            </appa>
            """);

        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (code, output) = HostedRun.Run("dotnet",
            $"\"{appaDll}\" build \"{dest}\" --stdlib \"{Path.Combine(gata, "libgata")}\"", dest);
        Assert.True(code == 0, $"appa build failed:\n{output}");
    }

    [Fact]
    public void ByteIdenticalOutput()
    {
        var gata = HostedRun.FindGataCheckout();
        if (gata == null) { Assert.Skip("no Gata checkout"); return; }

        using var one = Scratch.Create("appa-det-a-");
        using var two = Scratch.Create("appa-det-b-");
        BuildInto(one.Path, gata);
        BuildInto(two.Path, gata);

        foreach (var artifact in (string[])["program.c", "shared.h"])
        {
            string a = File.ReadAllText(Path.Combine(one.Path, "transpilation", artifact));
            string b = File.ReadAllText(Path.Combine(two.Path, "transpilation", artifact));
            if (a == b) continue;

            // Name the first differing line rather than dumping two whole translation units.
            var la = a.Split('\n');
            var lb = b.Split('\n');
            int i = 0;
            while (i < la.Length && i < lb.Length && la[i] == lb[i]) i++;
            Assert.Fail(
                $"{artifact} differs between two builds of identical source, first at line {i + 1}:\n" +
                $"  run 1: {(i < la.Length ? la[i] : "<end>")}\n" +
                $"  run 2: {(i < lb.Length ? lb[i] : "<end>")}\n" +
                "a pass is deciding order by walking a string-keyed hash container");
        }
    }
}
