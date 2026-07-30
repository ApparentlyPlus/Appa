namespace Appa.Tests;

using Appa;

/// <summary>
/// Cases where the program compiled and ran but computed the wrong answer, or where C reinterpreted
/// something Gata had already decided. Each was found by running the same emitted C at several
/// optimisation levels and under two compilers, which is the only oracle that sees them - a suite
/// that stops at "gcc accepted it" cannot.
/// </summary>
public class SemanticFidelityTests
{
    private static (string?, string?) Environment() => (HostedRun.FindGataCheckout(), HostedRun.FindCompiler());

    private static void AssertClean(string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
    }

    private static Diagnostic AssertOne(string code, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        var hits = diag.All.Where(d => d.Code == code).ToList();
        Assert.True(hits.Count == 1, $"expected one {code}, got {hits.Count}: " +
            string.Join("; ", diag.All.Select(d => $"{d.Code} {d.Message}")));
        return hits[0];
    }

    #region Emitted C must mean what the Gata literal said

    /// <summary>
    /// C rewrites nine '??X' sequences before anything else runs, so a Gata string containing one
    /// printed a different character. Verbatim emission made the literal a C source construct.
    /// </summary>
    [Fact]
    public void TrigraphSequencesSurviveEmission()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            realm userspace { entry func Main() {
                Console.PrintLine("A??(B??)C");
                Console.PrintLine("??= ??/ ??' ??! ??< ??> ??-");
                Console.PrintLine($"interp ??( {1 + 1}");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("A??(B??)C\n??= ??/ ??' ??! ??< ??> ??-\ninterp ??( 2\n", r.Output);
    }

    /// <summary>
    /// Locals are the only names emitted verbatim, so one spelled like an object-like macro from a
    /// standard header expanded mid-declaration. They get the same trailing underscore the C
    /// keywords already got.
    /// </summary>
    [Theory]
    [InlineData("EOF")]
    [InlineData("SEEK_SET")]
    [InlineData("INT_MAX")]
    [InlineData("errno")]
    [InlineData("stdin")]
    [InlineData("NAN")]
    public void LocalsMayBeNamedAfterStandardMacros(string name)
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun($$"""
            import Console;
            int func Twice(int {{name}}) { return {{name}} * 2; }
            realm userspace { entry func Main() {
                let int {{name}} = 21;
                Console.PrintLine($"{Twice({{name}})}");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("42\n", r.Output);
    }

    #endregion

    #region Values the standard library rendered wrongly

    /// <summary>
    /// Both formatters took the magnitude by negating, which has no result for the most negative
    /// value - so the digit loop saw a value still below zero and emitted nothing. The output
    /// differed per optimisation level, which is what an unrepresentable negation buys.
    /// </summary>
    [Fact]
    public void MostNegativeIntegersRoundTrip()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import Int;
            import Long;
            realm userspace { entry func Main() {
                let int m = -2147483648;
                Console.PrintLine($"i {Int.ToString(m)} {Int.ToString(Int.Parse("-2147483648"))} {Int.ToString(2147483647)}");
                Console.PrintLine($"l {Long.ToString(Long.Parse("-9223372036854775808"))} {Long.ToString(Long.Parse("9223372036854775807"))}");
                Console.PrintLine($"n {Int.ToString(0)} {Int.ToString(-5)}");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("i -2147483648 -2147483648 2147483647\n" +
                     "l -9223372036854775808 9223372036854775807\n" +
                     "n 0 -5\n", r.Output);
    }

    /// <summary>
    /// Every numeric went to the signed formatter, so an unsigned value came back with its high bit
    /// read as a sign. 'byte' and 'ushort' were right only because they widen into int losslessly.
    /// </summary>
    [Fact]
    public void UnsignedValuesFormatAsUnsigned()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            realm userspace { entry func Main() {
                Console.PrintLine($"{(0 - 1) as uint}");
                Console.PrintLine($"{(0 as uint64) - (1 as uint64)}");
                Console.PrintLine($"{(0 as usize) - (1 as usize)}");
                Console.PrintLine($"{200 as byte} {60000 as ushort} {0 as uint} {-5} {2147483647}");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("4294967295\n18446744073709551615\n18446744073709551615\n" +
                     "200 60000 0 -5 2147483647\n", r.Output);
    }

    #endregion

    #region Constants checked against the type that will hold them

    /// <summary>
    /// The conversion is well defined and silent, and the C compiler only objects under warning
    /// flags the GatOS build does not pass - so 'let byte b = 300;' stored 44 and said nothing.
    /// </summary>
    [Theory]
    [InlineData("let int x = 5000000000;", "int")]
    [InlineData("let byte b = 300;", "byte")]
    [InlineData("let short s = 70000;", "short")]
    [InlineData("let char c = 300;", "char")]
    [InlineData("let uint u = -1;", "uint")]
    [InlineData("let sbyte v = -129;", "sbyte")]
    public void OutOfRangeLiteralIsRejected(string decl, string type)
    {
        var d = AssertOne(Codes.TypeMismatch, $"realm kernel {{ entry func Main() {{ {decl} }} }}");
        Assert.Contains("does not fit", d.Message);
        Assert.Contains(type, d.Message);
    }

    /// <summary>
    /// The boundaries themselves stay legal - and the most negative one only became writable with
    /// this check, because a negated literal was ranked as int64 and so fit no smaller type.
    /// </summary>
    [Theory]
    [InlineData("let int x = -2147483648; let int y = x;")]
    [InlineData("let int x = 2147483647; let int y = x;")]
    [InlineData("let byte b = 255; let byte c = b;")]
    [InlineData("let sbyte v = -128; let sbyte w = v;")]
    [InlineData("let uint u = 4294967295; let uint w = u;")]
    [InlineData("let int64 n = -9223372036854775808; let int64 m = n;")]
    public void BoundaryLiteralsAreAccepted(string body) =>
        AssertClean($"realm kernel {{ entry func Main() {{ {body} }} }}");

    [Fact]
    public void MostNegativeIntLiteralRunsCorrectly()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            realm userspace { entry func Main() {
                let int m = -2147483648;
                let sbyte s = -128;
                Console.PrintLine($"{m} {s}");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("-2147483648 -128\n", r.Output);
    }

    /// <summary>
    /// Enum members are read as int wherever they are used, so a wider constant was truncated at
    /// every use rather than reported at the declaration that picked it. The implicit successor of
    /// the largest int overflows the same way.
    /// </summary>
    [Theory]
    [InlineData("enum E { A = 99999999999 }")]
    [InlineData("enum E { A = 2147483647, B }")]
    public void OutOfRangeEnumValueIsRejected(string decl)
    {
        var d = AssertOne(Codes.TypeMismatch, $"realm kernel {{ {decl} entry func Main() {{ }} }}");
        Assert.Contains("does not fit in 'int'", d.Message);
    }

    [Theory]
    [InlineData("enum E { A = 2147483647 }")]
    [InlineData("enum E { A = -2147483648 }")]
    [InlineData("enum E { A, B, C }")]
    public void InRangeEnumValuesAreAccepted(string decl) =>
        AssertClean($"realm kernel {{ {decl} entry func Main() {{ }} }}");

    #endregion

    #region Declarations that leak by construction

    /// <summary>
    /// A fixed array is raw storage with no destructor, so elements it still holds when it dies are
    /// leaked. Stores into one are counted correctly, so this is a leak and never a dangling read -
    /// which is why it is a warning naming the alternative rather than an error.
    /// </summary>
    [Fact]
    public void ManagedFixedArrayWarns()
    {
        var d = AssertOne(Codes.ManagedFixedArray,
            "class Res { public int n; }\n" +
            "realm kernel { class Box { public [2]Res slots; } entry func Main() { let Box b = new Box(); } }");
        Assert.Contains("never released", d.Message);
        Assert.Contains(d.Hints, h => h.Contains("List[Res]"));
    }

    [Fact]
    public void UnmanagedFixedArrayIsSilent() =>
        Assert.DoesNotContain(SingleFileCompile.Check(
            "realm kernel { entry func Main() { let [4]int nums = [1,2,3,4]; let int f = nums[0]; } }")
            .Diag.All, d => d.Code == Codes.ManagedFixedArray);

    #endregion

    #region Declaration headers written the other way round

    /// <summary>
    /// 'throws' and 'entry' were only accepted in one order, so the other produced a parse error
    /// about a type name instead of the rule that actually forbids the pair.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { throws entry func Main() { } }")]
    [InlineData("realm kernel { entry throws func Main() { } }")]
    public void ThrowsOnAnEntryIsRejectedInEitherOrder(string src) =>
        AssertOne(Codes.BadEntrySignature, src);

    [Theory]
    [InlineData("throws entry func R() { }")]
    [InlineData("entry throws func R() { }")]
    public void ThrowsOnAThreadEntryIsRejectedInEitherOrder(string entry)
    {
        var d = AssertOne(Codes.BadEntrySignature,
            "realm kernel { entry func Main() { } }\n" +
            $"realm userspace {{ foreground process P {{ thread T {{ {entry} }} }} }}");
        Assert.Contains("thread entry", d.Message);
    }

    #endregion
}
