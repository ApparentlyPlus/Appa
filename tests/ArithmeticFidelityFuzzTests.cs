namespace Appa.Tests;

using System.Diagnostics;
using System.Numerics;
using System.Text;

/// <summary>
/// Execution-differential fuzzer over integer arithmetic.
/// </summary>
public class ArithmeticFidelityFuzzTests
{
    private const int Expressions = 400;
    private const int Seed = 20260730;

    private sealed record Prim(string Name, int Bits, bool Signed, int Rank);

    private static readonly Prim[] Types =
    [
        new("sbyte",  8,  true,  2), new("byte",   8,  false, 2),
        new("short",  16, true,  3), new("ushort", 16, false, 3),
        new("int",    32, true,  4), new("uint",   32, false, 4),
        new("int64",  64, true,  5), new("uint64", 64, false, 5),
    ];

    private static BigInteger Lo(Prim t) => t.Signed ? -BigInteger.Pow(2, t.Bits - 1) : 0;
    private static BigInteger Hi(Prim t) =>
        t.Signed ? BigInteger.Pow(2, t.Bits - 1) - 1 : BigInteger.Pow(2, t.Bits) - 1;
    private static bool Fits(BigInteger v, Prim t) => v >= Lo(t) && v <= Hi(t);

    /// <summary>
    /// Reduces a value into the type's range the way a C cast to a fixed-width integer does.
    /// </summary>
    private static BigInteger Wrap(BigInteger v, Prim t)
    {
        var span = BigInteger.Pow(2, t.Bits);
        v = ((v % span) + span) % span;
        return t.Signed && v >= BigInteger.Pow(2, t.Bits - 1) ? v - span : v;
    }

    private static Prim Result(Prim a, Prim b) => a.Rank >= b.Rank ? a : b;

    /// <summary>
    /// C truncates division toward zero; BigInteger.Divide already does.
    /// </summary>
    private static BigInteger CDiv(BigInteger a, BigInteger b) => BigInteger.Divide(a, b);

    private sealed record Node(string Text, Prim Type, BigInteger Value);

    /// <summary>
    /// A literal written so the checker gives it exactly the type wanted. A bare literal is typed by
    /// its own magnitude - 'int' when it fits, 'int64' otherwise - and a leading '-' is an operator
    /// over the magnitude rather than part of it, so only non-negative in-range values go bare.
    /// </summary>
    private static Node Leaf(Random rng, Prim t)
    {
        BigInteger[] choices = [0, 1, 2, Lo(t), Hi(t), RandomIn(rng, t)];
        var v = choices[rng.Next(choices.Length)];
        bool bare = t.Name == "int" && v >= 0;
        return new Node(bare ? $"({v})" : $"(({v}) as {t.Name})", t, v);
    }

    private static BigInteger RandomIn(Random rng, Prim t)
    {
        var span = Hi(t) - Lo(t);
        var bytes = span.ToByteArray();
        rng.NextBytes(bytes);
        bytes[^1] &= 0x7f;
        return Lo(t) + new BigInteger(bytes) % (span + 1);
    }

    /// <summary>
    /// Picks an operator that is free of undefined behaviour for these two operands, or gives up.
    /// Everything skipped here is skipped because C declines to define it, not because Gata does.
    /// </summary>
    private static Node? Binary(Random rng, Node l, Node r)
    {
        foreach (var op in Shuffled(rng, ["+", "-", "*", "/", "%", "&", "|", "^", "<<", ">>"]))
        {
            bool shift = op is "<<" or ">>";
            var t = shift ? l.Type : Result(l.Type, r.Type);

            if (op is "/" or "%" && l.Type.Signed != r.Type.Signed)
            {
                var signed = l.Type.Signed ? l.Type : r.Type;
                var uns = l.Type.Signed ? r.Type : l.Type;
                if (t.Signed == false || uns.Rank >= signed.Rank) continue;
            }

            var lv = Wrap(l.Value, t);
            var rv = shift ? r.Value : Wrap(r.Value, t);
            BigInteger v;

            switch (op)
            {
                case "/" or "%":
                    if (rv == 0) continue;
                    if (t.Signed && lv == Lo(t) && rv == -1) continue;  // overflows; C leaves it undefined
                    v = op == "/" ? CDiv(lv, rv) : lv - CDiv(lv, rv) * rv;
                    break;
                case "<<" or ">>":
                    if (rv < 0 || rv >= t.Bits || lv < 0) continue;
                    v = op == "<<" ? lv << (int)rv : lv >> (int)rv;
                    break;
                case "+": v = lv + rv; break;
                case "-": v = lv - rv; break;
                case "*": v = lv * rv; break;
                default:
                    if (lv < 0 || rv < 0) continue;
                    v = op == "&" ? lv & rv : op == "|" ? lv | rv : lv ^ rv;
                    break;
            }

            if (t.Signed && t.Bits >= 32 && !Fits(v, t)) continue;
            return new Node($"({l.Text} {op} {r.Text})", t, Wrap(v, t));
        }
        return null;
    }

    private static Node? Unary(Random rng, Node e)
    {
        if (rng.Next(2) == 0 && e.Type.Signed)
        {
            if (e.Type.Bits >= 32 && e.Value == Lo(e.Type)) return null;   // -INT_MIN is undefined
            return new Node($"(-{e.Text})", e.Type, Wrap(-e.Value, e.Type));
        }
        return new Node($"(~{e.Text})", e.Type, Wrap(-e.Value - 1, e.Type));
    }

    /// <summary>
    /// Builds one expression of the given depth. Each subtree is built exactly once: retrying by
    /// rebuilding the operands would make generation exponential in the depth, since two extreme leaves
    /// can reject every operator and each retry would then rebuild both sides again.
    /// </summary>
    private static Node Build(Random rng, int depth, Prim? want = null)
    {
        var t = want ?? Types[rng.Next(Types.Length)];
        if (depth <= 0) return Leaf(rng, t);

        if (rng.NextDouble() < 0.18 && Unary(rng, Build(rng, depth - 1, t)) is { } u) return u;

        var l = Build(rng, depth - 1, rng.NextDouble() < 0.7 ? t : null);
        var r = Build(rng, depth - 1, rng.NextDouble() < 0.4 ? null : l.Type);
        if (Binary(rng, l, r) is { } e) return e;

        var one = One(l.Type);
        return Binary(rng, l, one)
               ?? new Node($"({l.Text} * {one.Text})", l.Type, l.Value);
    }

    private static Node One(Prim t) =>
        new(t.Name == "int" ? "(1)" : $"((1) as {t.Name})", t, BigInteger.One);

    private static string[] Shuffled(Random rng, string[] items)
    {
        var copy = (string[])items.Clone();
        for (int i = copy.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    /// <summary>
    /// Builds one Gata program printing every generated expression, alongside the reference values.
    /// </summary>
    private static (string Source, string[] Expected, Node[] Nodes) Program()
    {
        var rng = new Random(Seed);
        var nodes = new Node[Expressions];
        for (int i = 0; i < Expressions; i++) nodes[i] = Build(rng, 1 + rng.Next(4));

        var sb = new StringBuilder("import Console;\n\nrealm userspace {\n    entry func Main() {\n");
        for (int i = 0; i < nodes.Length; i++)
            sb.Append($"        Console.PrintLine($\"{i}={{{nodes[i].Text}}}\");\n");
        sb.Append("    }\n}\n");

        return (sb.ToString(), [.. nodes.Select((n, i) => $"{i}={n.Value}")], nodes);
    }

    /// <summary>
    /// Generated arithmetic must give the reference answer at every optimisation level and under every
    /// available compiler. A level-dependent answer means the emitted C relies on something C does not
    /// define; a level-independent wrong answer means the emitter and the type checker disagree, which
    /// is what the promotion and signedness defects were.
    /// </summary>
    [Fact]
    public void ArithmeticMatchesTypes()
    {
        string? gata = HostedRun.FindGataCheckout();
        if (gata == null || HostedRun.FindCompiler() == null) return;

        var (src, expected, nodes) = Program();
        using var work = Scratch.Create("appa-arith-fidelity-");
        Directory.CreateDirectory(work.Combine("src"));
        File.WriteAllText(work.Combine("src", "main.g"), src);
        File.Copy(Path.Combine(gata, "envs", "env.hosted.g"), work.Combine("env.g"));
        File.WriteAllText(work.Combine("host.gconf"), """
            <appa>
                <ProjectName>host</ProjectName>
                <TargetBackend>Hosted</TargetBackend>
                <BuildMode>Debug</BuildMode>
            </appa>
            """);

        var appaDll = Path.Combine(AppContext.BaseDirectory, "Appa.dll");
        var (code, buildOut) = HostedRun.Run("dotnet",
            $"\"{appaDll}\" build \"{work.Path}\" --stdlib \"{Path.Combine(gata, "libgata")}\"", work.Path);
        Assert.True(code == 0, $"generated arithmetic should build, but appa reported:\n{buildOut}");

        string outDir = work.Combine("transpilation");
        int ran = 0;
        foreach (var (cc, opt) in Configs())
        {
            string exe = Path.Combine(outDir, $"prog_{cc}{opt.Replace("-", "")}");
            var (ccCode, ccOut) = HostedRun.Run(cc, $"-std=c11 {opt} -w -I. -o \"{exe}\" program.c -lm", outDir);
            if (ccCode != 0)
            {
                Assert.DoesNotContain("error:", ccOut);
                continue;
            }
            var (_, output) = HostedRun.Run(exe, "", outDir);
            AssertMatches(expected, nodes, output.Replace("\r\n", "\n"), $"{cc} {opt}");
            ran++;
        }
        Assert.True(ran > 0, "no compiler configuration ran, so nothing was actually checked");
    }

    /// <summary>
    /// The compiler and optimisation-level pairs to run, skipping compilers that are not installed.
    /// </summary>
    private static IEnumerable<(string Cc, string Opt)> Configs()
    {
        if (OnPath("gcc")) { yield return ("gcc", "-O0"); yield return ("gcc", "-O2"); }
        if (OnPath("clang")) yield return ("clang", "-O2");
    }

    private static bool OnPath(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Compares run output against the reference line by line, reporting the first few mismatches with
    /// the expression that produced each - a bare value diff over 400 generated expressions is not
    /// something anyone can act on.
    /// </summary>
    private static void AssertMatches(string[] expected, Node[] nodes, string output, string config)
    {
        var actual = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var wrong = new List<string>();
        for (int i = 0; i < expected.Length && wrong.Count < 4; i++)
        {
            string got = i < actual.Length ? actual[i] : "<missing>";
            if (got == expected[i]) continue;
            wrong.Add($"  [{i}] {nodes[i].Type.Name}\n      expected {expected[i]}, got {got}\n" +
                      $"      {nodes[i].Text}");
        }
        Assert.True(wrong.Count == 0,
            $"at {config}, {wrong.Count}+ of {expected.Length} generated expressions computed a value " +
            $"other than the one their declared types give:\n{string.Join("\n", wrong)}");
    }
}
