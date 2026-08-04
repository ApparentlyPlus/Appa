namespace Appa.Tests;

/// <summary>
/// Execution-level cover for the two ways a call can reach the wrong C symbol: a free function
/// sharing its name with the realm's 'entry func', and an '@extern' declared by more than one file.
/// </summary>
public class SymbolReachabilityExecutionTests
{
    private static Dictionary<string, string> Files() => new()
    {
        ["impl.g"] = """
            native {
                int probe_add(int a, int b) { return a + b; }
                static inline int probe_twice(int a) { return a * 2; }
            }
            """,

        ["a.g"] = """
            import "src/impl.g";
            @extern int func probe_add(int a, int b);
            @extern int func probe_twice(int a);
            int func ViaA() { return probe_add(2, 3); }
            int func TwiceViaA(int n) { return probe_twice(n); }
            """,

        ["b.g"] = """
            import "src/impl.g";
            @extern int func probe_add(int a, int b);
            int func ViaB() { return probe_add(10, 7); }
            """,

        ["named.g"] = """
            int func Main(int n) { return n + 1; }
            """,

        ["main.g"] = """
            import Console;
            import String;
            import "src/impl.g";
            import "src/a.g";
            import "src/b.g";
            import "src/named.g";

            realm userspace {
                entry func Main() {
                    // The entry calls the free functions that share its own name.
                    Console.PrintLine($"named {Main(41)} {Main(1)}");

                    // And reaches it as a value.
                    let func(int) -> int f = Main;
                    Console.PrintLine($"pointer {f(1)}");

                    // The same extern through two declaring files, plus a second extern whose C is
                    // 'static inline' - the case a generated prototype would have conflicted with.
                    Console.PrintLine($"extern {ViaA()} {ViaB()} {TwiceViaA(21)}");

                    let func(int, int) -> int g = probe_add;
                    Console.PrintLine($"externptr {g(20, 22)}");
                }
            }
            """,
    };

    [Fact]
    public void SharedNamesResolveCorrectly()
    {
        var gata = HostedRun.FindGataCheckout();
        var cc = HostedRun.FindCompiler();
        if (gata == null || cc == null) { Assert.Skip("no Gata checkout or C compiler found"); return; }

        var r = HostedRun.BuildAndRun(Files(), gata, cc);
        HostedRun.AssertClean(r);

        var lines = r.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Contains("named 42 2", lines);
        Assert.Contains("pointer 2", lines);
        Assert.Contains("extern 5 17 42", lines);
        Assert.Contains("externptr 42", lines);
    }
}
