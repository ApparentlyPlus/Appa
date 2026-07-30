namespace Appa.Tests;

using System.Globalization;

/// <summary>
/// Differential oracle for libgata's Math module against the host's libm.
///
/// Math.g is a hand-translation of fdlibm - a third of the standard library by line count, and
/// almost entirely bit manipulation, which is where transcription slips hide and where nothing
/// else in this suite looks. "It compiles" says nothing about it, and a hand-picked expected
/// value only catches an error someone already suspected. So the reference is the platform's own
/// libm, fed bit-identical inputs and compared on raw bit patterns, which keeps float formatting
/// out of the comparison entirely.
///
/// The tolerance is per-function rather than global. fdlibm targets under an ulp for the
/// transcendentals and a stricter bound is not meaningful, but sqrt is required by IEEE 754 to
/// be correctly rounded, so for that one anything other than bit-equality is a defect.
/// </summary>
public class MathFidelityTests
{
    /// <summary>Gata name, C name, and the largest ulp difference from libm that is not a defect.</summary>
    private static readonly (string Gata, string C, long Tolerance)[] Unary =
    [
        ("Sqrt",  "sqrt",  0),      // IEEE 754 requires correct rounding
        ("Abs",   "fabs",  0),
        ("Floor", "floor", 0),
        ("Ceil",  "ceil",  0),
        ("Trunc", "trunc", 0),
        ("Round", "round", 0),
        ("Log",   "log",   1),
        ("Exp",   "exp",   1),
        ("Log1p", "log1p", 1),
        ("Expm1", "expm1", 1),
        ("Asin",  "asin",  1),
        ("Acos",  "acos",  1),
        ("Atan",  "atan",  1),
        ("Sinh",  "sinh",  1),
        ("Cosh",  "cosh",  1),
        ("Tanh",  "tanh",  1),
        ("Asinh", "asinh", 1),
        ("Acosh", "acosh", 1),
        ("Atanh", "atanh", 1),
        // Sin/Cos/Tan need argument reduction for huge inputs, where fdlibm's reduction is
        // less precise than glibc's. A handful of ulp there is inherent to the port, not a slip.
        ("Sin",   "sin",   16),
        ("Cos",   "cos",   16),
        ("Tan",   "tan",   16),
    ];

    private static readonly (string Gata, string C, long Tolerance)[] Binary =
    [
        ("CopySign", "copysign", 0),
        ("Mod",      "fmod",     0),
        ("Atan2",    "atan2",    1),
        ("Pow",      "pow",      8),   // hi/lo splitting degrades near the overflow boundary
    ];

    /// <summary>
    /// The values worth testing: IEEE special cases, the exact thresholds fdlibm's own branch
    /// structure keys on, and both sides of every one of them. A uniform random sample would
    /// essentially never land on these, and they are where the branch selection goes wrong.
    /// </summary>
    private static double[] Inputs()
    {
        var v = new List<double>
        {
            0.0, -0.0, 1.0, -1.0, 2.0, -2.0, 0.5, -0.5, 3.0, -3.0, 10.0,
            double.PositiveInfinity, double.NegativeInfinity, double.NaN,
            double.Epsilon, -double.Epsilon,                 // smallest subnormal
            2.2250738585072014e-308, 2.2250738585072011e-308, // smallest normal / largest subnormal
            double.MaxValue, double.MinValue,
            Math.PI, Math.PI / 2, Math.PI / 4, 2 * Math.PI, -Math.PI, Math.E, Math.Log(2),
            709.782712893384, 709.7827128933841,             // exp/cosh overflow boundary
            -745.1332191019411, -745.1332191019412,
            0.9999999999999999, 1.0000000000000002, 0.41421356237309503,
            1e-300, 1e300, 1e-30, 1e30, 1e-10, 0.3, -0.3, 2.5, -2.5, 1.5,
            1e7, 1e9, 1e15, 1e16, 1e22,                      // argument-reduction ladder
            Math.Pow(2, 52), Math.Pow(2, 53), Math.Pow(2, 63),
        };

        // Both neighbours of every binade boundary, where rounding decisions are hardest and
        // where sqrt's rounding step was systematically wrong.
        for (int e = 1; e < 2047; e += 37)
        {
            long b = (long)e << 52;
            v.Add(BitConverter.Int64BitsToDouble(b));
            v.Add(BitConverter.Int64BitsToDouble(b + 1));
            v.Add(BitConverter.Int64BitsToDouble(b + (1L << 52) - 1));
        }

        var rng = new Random(20260730);
        for (int i = 0; i < 200; i++)
        {
            v.Add(rng.NextDouble() * 200 - 100);
            v.Add((rng.NextDouble() * 20 - 10) * Math.Pow(2, rng.Next(-1000, 1000)));
        }
        return [.. v];
    }

    private static string Hex(double d) =>
        "0x" + BitConverter.DoubleToUInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture) + "ULL";

    [Fact]
    public void MathMatchesLibmWithinDocumentedTolerance()
    {
        var gata = HostedRun.FindGataCheckout();
        if (gata == null) return;
        var cc = HostedRun.FindCompiler();
        if (cc == null) return;

        var xs = Inputs();
        var pairs = new List<(double A, double B)>();
        var rng = new Random(7);
        for (int i = 0; i < 150; i++) pairs.Add((xs[rng.Next(xs.Length)], xs[rng.Next(xs.Length)]));
        // pow's special-case table is the densest in the file, so cover it exhaustively.
        foreach (var a in (double[])[0.0, -0.0, 1.0, -1.0, 2.0, -2.0, 0.5,
                                     double.PositiveInfinity, double.NegativeInfinity, double.NaN])
            foreach (var b in (double[])[0.0, -0.0, 1.0, -1.0, 2.0, 3.0, -3.0, 0.5,
                                         double.PositiveInfinity, double.NegativeInfinity,
                                         double.NaN, 1e300, Math.Pow(2, 53)])
                pairs.Add((a, b));

        // Both programs emit "tag hi lo" lines, the result split into two halves because Gata
        // has no unsigned 64-bit formatting and a decimal double would defeat the point.
        var g = new System.Text.StringBuilder();
        var c = new System.Text.StringBuilder();
        g.AppendLine("import Math;");
        g.AppendLine("import Console;");
        g.AppendLine("uint64 func tobits(double x) { unsafe { let p = (&x) as uint64*; return *p; } }");
        g.AppendLine("double func frombits(uint64 u) { unsafe { let p = (&u) as double*; return *p; } }");
        g.AppendLine("void func emit(String t, double r) {");
        g.AppendLine("    let uint64 b = tobits(r);");
        g.AppendLine("    Console.PrintLine($\"{t} {((b >> 32) & 0xFFFFFFFFULL) as int64} " +
                     "{(b & 0xFFFFFFFFULL) as int64}\");");
        g.AppendLine("}");
        g.AppendLine("realm userspace {");
        g.AppendLine("entry func Main() {");

        c.AppendLine("#include <math.h>\n#include <stdio.h>\n#include <stdint.h>\n#include <string.h>");
        c.AppendLine("static void emit(const char* t, double r){uint64_t b;memcpy(&b,&r,8);" +
                     "printf(\"%s %lld %lld\\n\",t,(long long)((b>>32)&0xFFFFFFFFULL)," +
                     "(long long)(b&0xFFFFFFFFULL));}");
        c.AppendLine("static double frombits(uint64_t u){double d;memcpy(&d,&u,8);return d;}");
        c.AppendLine("int main(void){");

        for (int i = 0; i < xs.Length; i++)
            foreach (var (gn, cn, _) in Unary)
            {
                g.AppendLine($"    emit(\"{gn}/{i}\", Math.{gn}(frombits({Hex(xs[i])})));");
                c.AppendLine($"    emit(\"{gn}/{i}\", {cn}(frombits({Hex(xs[i])})));");
            }
        for (int i = 0; i < pairs.Count; i++)
            foreach (var (gn, cn, _) in Binary)
            {
                g.AppendLine($"    emit(\"{gn}/{i}\", Math.{gn}(frombits({Hex(pairs[i].A)}), " +
                             $"frombits({Hex(pairs[i].B)})));");
                c.AppendLine($"    emit(\"{gn}/{i}\", {cn}(frombits({Hex(pairs[i].A)}), " +
                             $"frombits({Hex(pairs[i].B)})));");
            }
        g.AppendLine("}\n}");
        c.AppendLine("return 0;}");

        var result = HostedRun.BuildAndRun(g.ToString(), gata, cc);
        HostedRun.AssertClean(result);

        using var work = TempDir.Create("appa-libm-ref-");
        File.WriteAllText(work.Combine("ref.c"), c.ToString());
        var (refCode, refOut) = HostedRun.Run(cc, "-std=c11 -O1 -o ref ref.c -lm", work.Path);
        Assert.True(refCode == 0, $"the libm reference program did not compile:\n{refOut}");
        var (runCode, refText) = HostedRun.Run(work.Combine("ref"), "", work.Path);
        Assert.True(runCode == 0, $"the libm reference program exited {runCode}");

        var expected = Parse(refText);
        var actual = Parse(result.Output);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, actual.Count);

        var tolerance = Unary.Concat(Binary).ToDictionary(t => t.Gata, t => t.Tolerance);
        var failures = new List<string>();
        foreach (var (tag, want) in expected)
        {
            ulong got = actual[tag];
            long d = Ulps(got, want);
            long limit = tolerance[tag[..tag.IndexOf('/')]];
            if (d > limit)
                failures.Add($"  {tag}: gata={BitConverter.UInt64BitsToDouble(got):R} " +
                             $"libm={BitConverter.UInt64BitsToDouble(want):R} " +
                             $"({(d == long.MaxValue ? "not comparable" : d + " ulp")}, limit {limit})");
        }
        Assert.True(failures.Count == 0,
            $"{failures.Count} results differ from libm by more than the documented tolerance:\n" +
            string.Join("\n", failures.Take(25)));
    }

    private static Dictionary<string, ulong> Parse(string text)
    {
        var d = new Dictionary<string, ulong>();
        foreach (var line in text.Split('\n'))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 3 || !p[0].Contains('/')) continue;
            if (!long.TryParse(p[1], out long hi) || !long.TryParse(p[2], out long lo)) continue;
            d[p[0]] = ((ulong)hi & 0xFFFFFFFF) << 32 | ((ulong)lo & 0xFFFFFFFF);
        }
        return d;
    }

    private static bool IsNaN(ulong b) => double.IsNaN(BitConverter.UInt64BitsToDouble(b));

    /// <summary>
    /// Distance in representable steps. Bit patterns are mapped to a monotone integer first so
    /// that the distance is a subtraction even across zero; NaN compares equal only to NaN.
    /// </summary>
    private static long Ulps(ulong a, ulong b)
    {
        if (IsNaN(a) || IsNaN(b)) return IsNaN(a) && IsNaN(b) ? 0 : long.MaxValue;
        static long Order(ulong x) =>
            (x & 0x8000000000000000UL) != 0 ? -(long)(x & 0x7FFFFFFFFFFFFFFFUL) : (long)x;
        return Math.Abs(Order(a) - Order(b));
    }
}
