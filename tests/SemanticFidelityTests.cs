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

    #region An expression's declared type is the type it computes at

    /// <summary>
    /// C's integer promotions widen anything below 'int' before the operator runs, so a sub-'int'
    /// result came back unpromoted only when it was stored straight into a narrow variable - the store
    /// truncated and hid it. Used in place the same expression gave C's answer, so 'byte 200 + 200'
    /// read 400 in an interpolation and 144 through a variable. Both are pinned here: the point is
    /// that they agree.
    /// </summary>
    [Fact]
    public void SubIntArithmeticKeepsItsDeclaredWidth()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            realm userspace { entry func Main() {
                let byte a = 200;   let byte b = 200;
                let byte stored = a + b;
                Console.PrintLine($"{stored} {a + b} {a * b}");

                let sbyte c = -100; let sbyte d = -100;
                Console.PrintLine($"{c + d} {-c}");

                let short e = 30000; let short f = 30000;
                let ushort g = 60000; let ushort h = 60000;
                Console.PrintLine($"{e + f} {g + h}");

                let byte z = 0;  let ushort w = 0;
                Console.PrintLine($"{~z} {~w}");

                let byte s = 3;
                Console.PrintLine($"{s << 7}");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("144 144 64\n56 100\n-5536 54464\n255 65535\n128\n", r.Output);
    }

    /// <summary>
    /// The compound forms never went through the binary-operator path, so they had none of its checks
    /// and none of its operand conversion. These are the same values the expanded 'a = a op b' gives.
    /// </summary>
    [Fact]
    public void CompoundAssignmentComputesAtTheTargetsType()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            realm userspace { entry func Main() {
                let byte b = 200;  b += 200;
                let short s = 30000; s += 30000;
                let byte c = 1;    c <<= 7;
                let sbyte q = 100; q *= 2;
                let int i = -10;   i /= 3;
                let uint u = 4000000000; u += 400000000;
                Console.PrintLine($"{b} {s} {c} {q} {i} {u}");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("144 -5536 128 -56 -3 105032704\n", r.Output);
    }

    /// <summary>
    /// Interpolation and '+' concatenation routed every signed value to the 32-bit formatter, which
    /// narrowed anything wider on the way in - and being built straight into the IR, the call never
    /// passed the check that would have reported the narrowing. 5000000000 printed as 705032704.
    /// </summary>
    [Fact]
    public void WideSignedValuesSurviveInterpolation()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import Long;
            int64 func Opaque(int64 x) { return x; }
            realm userspace { entry func Main() {
                let int64 big = Opaque(5000000000);
                let int64 neg = Opaque(-5000000000);
                Console.PrintLine($"{big} {neg}");
                Console.PrintLine("" + big + " " + Long.ToString(big));
                let uint64 u = 18446744073709551615;
                Console.PrintLine($"{u}");
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("5000000000 -5000000000\n5000000000 5000000000\n18446744073709551615\n", r.Output);
    }

    /// <summary>
    /// 'b as String' gave "true" while '$"{b}"' gave "1" - two spellings of one conversion disagreeing,
    /// because 'bool' is in the integer family and fell into the numeric formatter first.
    /// </summary>
    [Fact]
    public void BoolFormatsTheSameWayInterpolatedAndCast()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            bool func Opaque(bool b) { return b; }
            realm userspace { entry func Main() {
                let bool t = Opaque(true);
                Console.PrintLine($"{t} {Opaque(false)} {1 < 2}");
                Console.PrintLine((t as String) + " " + (Opaque(false) as String));
            } }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("true false true\ntrue false\n", r.Output);
    }

    /// <summary>
    /// Mixing signednesses is rejected exactly where the conversion changes what the value means, and
    /// silent where it cannot. Both halves matter: rejecting everything would pass a one-sided test.
    /// </summary>
    [Theory]
    // The operand converting cannot represent its own range in the type it converts to.
    [InlineData(true,  "let int a = -10;  let uint b = 3;    let int r = a / b;")]
    [InlineData(true,  "let int a = -10;  let uint b = 3;    let int r = a % b;")]
    [InlineData(true,  "let int a = -10;  let uint b = 3;    let bool r = a < b;")]
    [InlineData(true,  "let int a = -10;  let uint b = 3;    let bool r = a >= b;")]
    [InlineData(true,  "let sbyte a = -1; let ushort b = 3;  let bool r = b > a;")]
    [InlineData(true,  "let int64 a = -1; let uint64 b = 3;  let int64 r = a / b;")]
    // An unsigned operand widening into a strictly larger signed type loses nothing.
    [InlineData(false, "let int64 a = -10; let uint b = 3;   let int64 r = a / b;")]
    [InlineData(false, "let short a = -10; let byte b = 3;   let bool r = a < b;")]
    // A non-negative constant is representable in either domain.
    [InlineData(false, "let uint hx = 7;  let bool r = hx >= 0x40862E42;")]
    [InlineData(false, "let uint hx = 7;  let uint r = hx / 4;")]
    // The operators whose answer is the same bit pattern either way stay silent.
    [InlineData(false, "let int a = -10;  let uint b = 3;    let int r = a + b;")]
    [InlineData(false, "let int a = -10;  let uint b = 3;    let int r = a * b;")]
    [InlineData(false, "let int a = -10;  let uint b = 3;    let bool r = a == b;")]
    [InlineData(false, "let int a = -10;  let uint b = 3;    let int r = a >> b;")]
    public void MixedSignednessIsRejectedOnlyWhereItChangesTheAnswer(bool rejected, string body)
    {
        string src = $"realm kernel {{ entry func Main() {{ {body} }} }}";
        var (diag, _) = SingleFileCompile.Check(src);
        var hits = diag.All.Where(d => d.Code == Codes.MixedSignedness).ToList();
        Assert.True(rejected == (hits.Count > 0),
            $"expected G095 {(rejected ? "" : "not ")}to fire for: {body}\ngot: " +
            string.Join("; ", diag.All.Select(d => $"{d.Code} {d.Message}")));
    }

    /// <summary>
    /// The compound forms had none of the binary path's operator checks. The shift bound is Gata's own
    /// width, which is why the C compiler cannot stand in for it: 'b &lt;&lt;= 9' on a 'byte' promotes
    /// to 'int' before C sees it, so no warning fires there either.
    /// </summary>
    [Theory]
    [InlineData(Codes.DivisionByZero, "let int a = 8; a /= 0;")]
    [InlineData(Codes.DivisionByZero, "let int a = 8; a %= 0;")]
    [InlineData(Codes.BadShiftCount,  "let byte b = 1; b <<= 9;")]
    [InlineData(Codes.BadShiftCount,  "let int c = 1; c <<= 64;")]
    [InlineData(Codes.BadShiftCount,  "let int c = 1; c >>= -1;")]
    public void CompoundAssignmentGetsTheOperatorChecks(string code, string body) =>
        AssertOne(code, $"realm kernel {{ entry func Main() {{ {body} }} }}");

    /// <summary>
    /// Two 'char' values under '+' add their codepoints and wrap into a 'char', so what looks like
    /// joining text prints one unrelated character. Only that shape is reported: the codepoint
    /// arithmetic itself is how the standard library converts digits, and stays silent.
    /// </summary>
    [Theory]
    [InlineData(true,  "let char a = 'a'; let char b = 'b'; let char r = a + b;")]
    [InlineData(false, "let char a = 'a'; let char b = 'b'; let int r = a as int + b as int;")]
    [InlineData(false, "let char a = 'a'; let char b = 'b'; let char r = a - b;")]
    [InlineData(false, "let int n = 5; let char r = ('0' + n) as char;")]
    [InlineData(false, "let char a = 'a'; let int r = a as int + 1;")]
    public void AddingTwoCharsWarnsThatItIsNotConcatenation(bool warned, string body)
    {
        var (diag, _) = SingleFileCompile.Check($"realm kernel {{ entry func Main() {{ {body} }} }}");
        var hits = diag.All.Where(d => d.Code == Codes.CharArithmetic).ToList();
        Assert.True(warned == (hits.Count > 0),
            $"expected G096 {(warned ? "" : "not ")}to fire for: {body}\ngot: " +
            string.Join("; ", diag.All.Select(d => $"{d.Code} {d.Message}")));
        if (warned) Assert.Contains(hits[0].Hints, h => h.Contains("as String"));
    }

    /// <summary>
    /// A function is not a generic type, so 'Sort[int](xs)' is not a call with type arguments - the
    /// brackets read as an index, which then failed on the type keyword with "expected an expression,
    /// found 'int'". The rule the reader needed was never stated. Only a type keyword triggers it: a
    /// bare identifier has to keep reading as an index, since 'handlers[i](arg)' is a real call.
    /// </summary>
    [Theory]
    [InlineData("Algorithms.Sort[int](xs);")]
    [InlineData("let int m = Max[int](1, 2);")]
    [InlineData("Take[int64, bool](xs);")]
    public void ExplicitTypeArgumentsOnACallAreNamed(string body)
    {
        var d = AssertOne(Codes.ExplicitTypeArgs,
            $"realm kernel {{ entry func Main() {{ {body} }} }}");
        Assert.Contains("inferred", string.Join(" ", d.Hints));
    }

    /// <summary>
    /// The forms next door, which the check must not disturb: calling through an indexed element, a
    /// generic type in 'new', an explicit generic union instantiation, and a plain array index.
    /// </summary>
    [Theory]
    [InlineData("let [2]int a = [1, 2]; let int i = 0; let int v = a[i];")]
    [InlineData("let List[int] xs = new List[int]();")]
    [InlineData("let Maybe[int] m = Maybe[int].Found(7);")]
    public void NeighbouringBracketFormsStillParse(string body) =>
        AssertClean("union Maybe[T] { Found(T v), Missing }\n" +
                    "class List[T] { public func _init() { } }\n" +
                    $"realm kernel {{ entry func Main() {{ {body} }} }}");

    #endregion

    #region Reading a local before anything is stored in it

    private const string DefiniteAssignmentPrelude = """
        throws int func R(int n) { if (n < 0) { throw; } return n; }
        void func Fill(ref int slot) { slot = 9; }
        int func Use(int v) { return v; }

        """;

    /// <summary>
    /// 'let int x;' emits an uninitialised C local, so reading it is undefined behaviour with a value
    /// left over from whatever ran before. Nothing reported it: gcc's own check needs optimisation to
    /// be effective and warning flags the GatOS build does not pass.
    /// </summary>
    [Theory]
    [InlineData("let int x; let int y = Use(x);")]
    [InlineData("let int x; let int y = x + 1;")]
    [InlineData("let int x; if (x > 0) { }")]
    [InlineData("let int x; let int i = 0; while (i < x) { i = i + 1; }")]
    [InlineData("let byte b; let byte c = b;")]
    [InlineData("let double d; let double e = d * 2.0;")]
    // The store happens, but after the read.
    [InlineData("let int x; let int y; y = x; x = 1;")]
    public void ReadingAnUnassignedLocalIsRejected(string body) =>
        AssertOne(Codes.UseBeforeAssignment,
            DefiniteAssignmentPrelude + $"realm kernel {{ entry func Main() {{ {body} }} }}");

    /// <summary>
    /// The half that matters more. The analysis is deliberately one-sided - a branch counts as
    /// assigning if any arm does, a loop counts before its body is walked, an address taken counts -
    /// so it reports only reads no store on any path could have preceded. Everything here has to
    /// stay silent, including the conditional cases a stricter analysis would reject.
    /// </summary>
    [Theory]
    [InlineData("let int a; a = 1; let int u = Use(a);")]
    [InlineData("let int b; if (Use(1) > 0) { b = 2; } let int u = Use(b);")]
    [InlineData("let int c; Fill(ref c); let int u = Use(c);")]
    [InlineData("let int d; unsafe { let int* p = &d; } let int u = Use(d);")]
    [InlineData("let int e; let int i = 0; while (i < 3) { e = 5; i = i + 1; } let int u = Use(e);")]
    [InlineData("let int f; try { f = R(1); } catch { f = 0; } let int u = Use(f);")]
    [InlineData("let int g; g = R(-1) catch { assign 7; }; let int u = Use(g);")]
    [InlineData("let int h; switch (1) { case 1 { h = 1; } default { h = 2; } } let int u = Use(h);")]
    [InlineData("let int k; { k = 8; } let int u = Use(k);")]
    [InlineData("let int m; for (let int j = 0; j < 2; j = j + 1) { m = j; } let int u = Use(m);")]
    [InlineData("let int n; defer { let int u = Use(n); } n = 3;")]
    // A managed local is emitted as NULL, so nothing about reading one is undefined.
    [InlineData("let String s; let bool u = s == null;")]
    // Raw C can store into anything by name.
    [InlineData("let int p; native { } let int u = Use(p);")]
    public void ConservativeCasesStaySilent(string body) =>
        AssertClean(DefiniteAssignmentPrelude + $"realm kernel {{ entry func Main() {{ {body} }} }}");

    #endregion

    #region Control flow the analyses could not see

    /// <summary>
    /// A catch handler can sit at the root of an assignment since assignment-position 'catch' landed,
    /// but the loop analysis only looked for one on a declaration or an expression statement. A 'break'
    /// there was invisible: code after the loop was reported unreachable although it ran, and a
    /// function missing a return on that path was accepted. Neither is caught downstream - the GatOS
    /// build passes no warning flags, so the missing return is silent on the real target.
    /// </summary>
    [Fact]
    public void ABreakInAnAssignmentHandlerExitsItsLoop()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        string body = """
            import Console;
            throws int func R(int n) { if (n < 0) { throw; } return n; }
            int func Count() {
                let int x = 0;
                let int i = 0;
                while (true) {
                    i = i + 1;
                    x = R(3 - i) catch { break; };
                }
                return i * 10 + x;
            }
            realm userspace { entry func Main() { Console.PrintLine($"{Count()}"); } }
            """;

        // Checked without the standard library, which Check does not load, so the analysis under test
        // is reached without Console turning into unresolved-name noise.
        var (diag, _) = SingleFileCompile.Check("""
            throws int func R(int n) { if (n < 0) { throw; } return n; }
            int func Count() {
                let int x = 0;
                let int i = 0;
                while (true) { i = i + 1; x = R(3 - i) catch { break; }; }
                return i * 10 + x;
            }
            realm kernel { entry func Main() { } }
            """);
        Assert.DoesNotContain(diag.All, d => d.Code == Codes.UnreachableCode);
        Assert.False(diag.HasErrors, string.Join("; ", diag.All.Select(d => $"{d.Code} {d.Message}")));

        var r = HostedRun.BuildAndRun(body, gata, cc);
        HostedRun.AssertClean(r);
        Assert.Equal("40\n", r.Output);
    }

    /// <summary>
    /// The other half of the same hole: with the only exit from the loop in an assignment handler, the
    /// function does have a path that falls out without returning, and that must still be reported.
    /// </summary>
    [Fact]
    public void AFunctionLeavingThroughAnAssignmentHandlerStillNeedsAReturn() =>
        AssertOne(Codes.MissingReturn, """
            throws int func R(int n) { if (n < 0) { throw; } return n; }
            int func NoReturn() {
                let int x = 0;
                while (true) { x = R(0 - 1) catch { break; }; }
            }
            realm kernel { entry func Main() { } }
            """);

    /// <summary>
    /// A 'return' inside raw C is not visible to the missing-return check, and conceding it would
    /// excuse every function containing a native block. The rejection stands; the hint names the
    /// whole-body native form instead of leaving the author to guess.
    /// </summary>
    [Fact]
    public void MissingReturnNamesTheNativeBlockItCannotSeeInto()
    {
        var d = AssertOne(Codes.MissingReturn, """
            int func NativeRet() { native { return 42; } }
            realm kernel { entry func Main() { } }
            """);
        Assert.Contains(d.Hints, h => h.Contains("native"));
    }

    #endregion
}
