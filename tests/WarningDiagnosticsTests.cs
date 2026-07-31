namespace Appa.Tests;

using Appa;

/// <summary>
/// The lint-grade diagnostics: warnings for code that compiles but almost certainly does not mean
/// what it says, plus the one UB error decidable from literals. Each is paired with a negative
/// case, since an unsilenceable warning is worse than none.
/// </summary>
public class WarningDiagnosticsTests
{
    /// <summary>
    /// Checks the source and returns every diagnostic carrying the given code.
    /// </summary>
    private static Diagnostic[] Of(string code, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        return [.. diag.All.Where(d => d.Code == code)];
    }

    /// <summary>
    /// Asserts the source produces exactly one warning with the code, and returns it.
    /// </summary>
    private static Diagnostic AssertWarns(string code, string src)
    {
        var hits = Of(code, src);
        Assert.True(hits.Length == 1,
            $"expected exactly one {code}, got {hits.Length}: " +
            string.Join("; ", hits.Select(h => h.Message)));
        Assert.Equal(Severity.Warning, hits[0].Severity);
        return hits[0];
    }

    /// <summary>
    /// Asserts the source produces no diagnostic at all with the given code.
    /// </summary>
    private static void AssertNoWarn(string code, string src)
    {
        var hits = Of(code, src);
        Assert.True(hits.Length == 0,
            $"expected no {code}, got: " + string.Join("; ", hits.Select(h => h.Message)));
    }

    /// <summary>
    /// Asserts the source produces at least one error with the code.
    /// </summary>
    private static void AssertError(string code, string src)
    {
        var hits = Of(code, src);
        Assert.True(hits.Length > 0, $"expected {code} but it was not reported");
        Assert.Contains(hits, h => h.Severity == Severity.Error);
    }

    #region G070 shadowed variable

    [Fact]
    public void ShadowingAnOuterLocalWarns()
    {
        var d = AssertWarns(Codes.ShadowedVariable,
            "realm kernel { entry func Main() { let x = 1; { let x = 2; let y = x; } let z = x; } }");
        Assert.Contains("shadows", d.Message);
    }

    /// <summary>
    /// Sibling scopes do not nest, so reusing a name across them is not shadowing. Redeclaring in
    /// the *same* scope stays a hard error, not a warning.
    /// </summary>
    [Fact]
    public void SiblingScopesAreNotShadowing()
    {
        AssertNoWarn(Codes.ShadowedVariable,
            "realm kernel { entry func Main() { { let x = 1; let a = x; } { let x = 2; let b = x; } } }");
        AssertError(Codes.DuplicateName,
            "realm kernel { entry func Main() { let x = 1; let x = 2; } }");
        AssertNoWarn(Codes.ShadowedVariable,
            "realm kernel { entry func Main() { let x = 1; let x = 2; } }");
    }

    #endregion

    #region G071 self-assignment

    [Fact]
    public void SelfAssignmentWarns()
    {
        AssertWarns(Codes.SelfAssignment,
            "realm kernel { entry func Main() { let x = 1; x = x; } }");
        AssertNoWarn(Codes.SelfAssignment,
            "realm kernel { entry func Main() { let x = 1; let y = 2; x = y; } }");
    }

    [Fact]
    public void FieldSelfAssignDiffersFromParam()
    {
        AssertWarns(Codes.SelfAssignment,
            "class C { int n; public void func M() { self.n = self.n; } } " +
            "realm kernel { entry func Main() { let c = new C(); c.M(); } }");
        AssertNoWarn(Codes.SelfAssignment,
            "class C { int n; public void func M(int n) { self.n = n; } } " +
            "realm kernel { entry func Main() { let c = new C(); c.M(1); } }");
    }

    [Fact]
    public void IndexSelfAssignNeedsLiterals()
    {
        // Computed indices may denote different elements on each side, so only two identical
        // literal subscripts are enough to call it a self-assignment.
        AssertNoWarn(Codes.SelfAssignment,
            "realm kernel { entry func Main() { let int[4] a; let i = 0; let j = 1; a[i] = a[j]; } }");
    }

    #endregion

    #region G072 statement with no effect

    [Theory]
    [InlineData("realm kernel { entry func Main() { let a = 1; a + 1; } }")]
    [InlineData("realm kernel { entry func Main() { let a = 1; a; } }")]
    [InlineData("realm kernel { entry func Main() { let a = 1; let b = 2; a == b; } }")]
    public void PureExpressionStatementWarns(string src)
    {
        AssertWarns(Codes.NoEffect, src);
    }

    [Fact]
    public void ComparisonStatementHintsAssign()
    {
        var d = AssertWarns(Codes.NoEffect,
            "realm kernel { entry func Main() { let a = 1; let b = 2; a == b; } }");
        Assert.Contains(d.Hints, h => h.Contains("'='"));
    }

    [Fact]
    public void DiscardedCallResultDoesNotWarn()
    {
        AssertNoWarn(Codes.NoEffect,
            "int func F() { return 1; } realm kernel { entry func Main() { F(); } }");
    }

    [Fact]
    public void MutatingStatementsDoNotWarn()
    {
        AssertNoWarn(Codes.NoEffect,
            "realm kernel { entry func Main() { let a = 1; a = 2; a++; let b = a; } }");
    }

    #endregion

    #region G073 / G078 constant and self-comparing conditions

    [Fact]
    public void LiteralIfConditionWarns()
    {
        var t = AssertWarns(Codes.ConstantCondition,
            "realm kernel { entry func Main() { if (true) { let a = 1; let b = a; } } }");
        Assert.Contains("always true", t.Message);

        var f = AssertWarns(Codes.ConstantCondition,
            "realm kernel { entry func Main() { if (false) { let a = 1; let b = a; } } }");
        Assert.Contains("always false", f.Message);
    }

    [Fact]
    public void InfiniteLoopFormsDoNotWarn()
    {
        AssertNoWarn(Codes.ConstantCondition,
            "realm kernel { entry func Main() { while (true) { break; } } }");
        AssertNoWarn(Codes.ConstantCondition,
            "realm kernel { entry func Main() { for (let i = 0; ; i = i + 1) { break; } } }");
    }

    [Fact]
    public void SelfComparisonWarnsInLoopsToo()
    {
        AssertWarns(Codes.SelfComparison,
            "realm kernel { entry func Main() { let a = 1; if (a == a) { let b = 1; let c = b; } } }");
        AssertWarns(Codes.SelfComparison,
            "realm kernel { entry func Main() { let a = 1; while (a < a) { break; } } }");
        AssertNoWarn(Codes.SelfComparison,
            "realm kernel { entry func Main() { let a = 1; let b = 2; if (a == b) { let c = 1; let d = c; } } }");
    }

    #endregion

    #region G074 redundant cast

    [Fact]
    public void SameTypeCastOnAValueWarns()
    {
        AssertWarns(Codes.RedundantCast,
            "realm kernel { entry func Main() { let int a = 1; let b = (a as int); } }");
    }

    /// <summary>
    /// A cast on a literal pins that literal's width where inference would otherwise choose it,
    /// which is deliberate in bit-manipulation code. libgata relies on this exemption.
    /// </summary>
    [Fact]
    public void SameTypeCastOnALiteralIsExempt()
    {
        AssertNoWarn(Codes.RedundantCast,
            "realm kernel { entry func Main() { let a = (0x00100000 as int); let b = a; } }");
    }

    [Fact]
    public void WideningCastDoesNotWarn()
    {
        AssertNoWarn(Codes.RedundantCast,
            "realm kernel { entry func Main() { let int a = 1; let b = (a as int64); } }");
    }

    #endregion

    #region G075 division by a literal zero

    [Theory]
    [InlineData("realm kernel { entry func Main() { let a = 1; let b = a / 0; } }")]
    [InlineData("realm kernel { entry func Main() { let a = 1; let b = a % 0; } }")]
    public void DivisionByLiteralZeroIsAnError(string src)
    {
        AssertError(Codes.DivisionByZero, src);
    }

    /// <summary>
    /// Floating-point division by zero is defined as an infinity, and a non-literal divisor cannot
    /// be judged here. Neither is reported.
    /// </summary>
    [Fact]
    public void FloatAndVariableDivisorsAreSilent()
    {
        AssertNoWarn(Codes.DivisionByZero,
            "realm kernel { entry func Main() { let double a = 1.0; let b = a / 0.0; } }");
        AssertNoWarn(Codes.DivisionByZero,
            "realm kernel { entry func Main() { let a = 1; let z = 0; let b = a / z; } }");
    }

    #endregion

    #region G076 unused parameter

    [Fact]
    public void UnusedParameterWarns()
    {
        var d = AssertWarns(Codes.UnusedParameter,
            "int func F(int a, int b) { return a; } " +
            "realm kernel { entry func Main() { let r = F(1, 2); } }");
        Assert.Contains("'b'", d.Message);
    }

    [Fact]
    public void UnderscoreOptsOutOfUnusedWarnings()
    {
        AssertNoWarn(Codes.UnusedParameter,
            "int func F(int a, int _b) { return a; } " +
            "realm kernel { entry func Main() { let r = F(1, 2); } }");
        AssertNoWarn(Codes.UnusedVariable,
            "realm kernel { entry func Main() { let _scratch = 1; } }");
    }

    [Fact]
    public void NativeBodiesSuppressUnusedParams()
    {
        AssertNoWarn(Codes.UnusedParameter,
            "int func F(int a) native { return 0; } " +
            "realm kernel { entry func Main() { let r = F(1); } }");
    }

    [Fact]
    public void InterpolatedUseCountsAsUse()
    {
        AssertNoWarn(Codes.UnusedParameter,
            "String func F(int a) { return $\"{a}\"; } " +
            "realm kernel { entry func Main() { let r = F(1); } }");
    }

    #endregion

    #region G077 unreachable default arm

    [Fact]
    public void DefaultOnAFullyCoveredMatchWarns()
    {
        var d = AssertWarns(Codes.UnreachableCase,
            "union U { A(int x), B(int y) } " +
            "realm kernel { entry func Main() { let u = U.A(1); " +
            "match (u) { case A(x) { let p = x; } case B(y) { let q = y; } default { let r = 0; } } } }");
        Assert.Contains("never run", d.Message);
    }

    [Fact]
    public void NeededDefaultIsSilent()
    {
        AssertNoWarn(Codes.UnreachableCase,
            "union U { A(int x), B(int y) } " +
            "realm kernel { entry func Main() { let u = U.A(1); " +
            "match (u) { case A(x) { let p = x; } default { let r = 0; } } } }");
        AssertError(Codes.NonExhaustiveMatch,
            "union U { A(int x), B(int y) } " +
            "realm kernel { entry func Main() { let u = U.A(1); " +
            "match (u) { case A(x) { let p = x; } } } }");
    }

    #endregion

    #region G080 shift count out of range

    [Theory]
    [InlineData("realm kernel { entry func Main() { let int a = 1; let b = a << 32; } }")]
    [InlineData("realm kernel { entry func Main() { let int a = 1; let b = a >> 99; } }")]
    public void OutOfRangeShiftCountIsAnError(string src)
    {
        AssertError(Codes.BadShiftCount, src);
    }

    /// <summary>
    /// The bound follows the operand's own width, so a count illegal for 'int' is fine for 'int64'.
    /// In-range counts and non-literal counts are never reported.
    /// </summary>
    [Fact]
    public void ShiftBoundFollowsOperandWidth()
    {
        AssertNoWarn(Codes.BadShiftCount,
            "realm kernel { entry func Main() { let int64 a = 1; let b = a << 32; } }");
        AssertNoWarn(Codes.BadShiftCount,
            "realm kernel { entry func Main() { let int a = 1; let b = a << 31; } }");
        AssertNoWarn(Codes.BadShiftCount,
            "realm kernel { entry func Main() { let int a = 1; let n = 40; let b = a << n; } }");
    }

    #endregion

    #region G080 string that looks interpolated

    [Fact]
    public void PlainStringNamingAVariableWarns()
    {
        var d = AssertWarns(Codes.MissingInterpolation,
            "realm kernel { entry func Main() { let count = 1; let s = \"n={count}\"; let t = s; } }");
        Assert.Contains(d.Hints, h => h.Contains("$\""));
    }

    [Fact]
    public void NonInterpolationBracesAreSilent()
    {
        AssertNoWarn(Codes.MissingInterpolation,
            "realm kernel { entry func Main() { let s = \"{notAVariable}\"; let t = s; } }");
        AssertNoWarn(Codes.MissingInterpolation,
            "realm kernel { entry func Main() { let count = 1; let s = $\"n={count}\"; let t = s; } }");
        AssertNoWarn(Codes.MissingInterpolation,
            "realm kernel { entry func Main() { let s = \"struct { int x; }\"; let t = s; } }");
    }

    #endregion

    #region Hint rendering

    [Fact]
    public void HintsRenderBelowTheCaret()
    {
        var (diag, _) = SingleFileCompile.Check(
            "realm kernel { entry func Main() { let a = 1; let b = a / 0; } }");
        var d = Assert.Single(diag.All.Where(x => x.Code == Codes.DivisionByZero));
        Assert.NotEmpty(d.Hints);
        foreach (var h in d.Hints) Assert.DoesNotContain(h, d.Message);

        var text = diag.Render(d);
        var lines = text.Split('\n');
        int msgLine = Array.FindIndex(lines, l => l.Contains(d.Message));
        int helpLine = Array.FindIndex(lines, l => l.Contains("help"));
        Assert.True(msgLine >= 0 && helpLine > msgLine,
            $"help line must follow the message line; got msg={msgLine} help={helpLine}");
        Assert.Contains(d.Hints[0], lines[helpLine]);
        // The caret row is drawn between the two.
        Assert.Contains(lines[(msgLine + 1)..helpLine], l => l.Contains('^'));
    }

    [Fact]
    public void MultipleHintsEachGetTheirOwnLine()
    {
        var bag = new DiagnosticBag(new SourceSet());
        var sources = bag.Sources;
        sources.Add("t.g", "let x = 1;\n");
        bag.Error(Codes.Syntax, "t.g", new TextSpan(0, 3), "something went wrong",
            ["first suggestion", "second suggestion"]);
        var lines = bag.Render(bag.All[0]).Split('\n');
        Assert.Equal(2, lines.Count(l => l.Contains("help")));
        Assert.Contains(lines, l => l.Contains("first suggestion") && !l.Contains("second"));
        Assert.Contains(lines, l => l.Contains("second suggestion") && !l.Contains("first"));
    }

    #endregion

    #region Regression guard

    /// <summary>
    /// Collects every static-call target in a module, so a call can be checked against the set of
    /// symbols the module actually defines.
    /// </summary>
    private sealed class CallTargetCollector : IrWalker
    {
        public readonly List<string> Targets = [];
        public void Collect(IrStmt s) => WalkStmt(s);
        protected override void WalkExpr(IrExpr e)
        {
            if (e is IrStaticCall sc) Targets.Add(sc.CName);
            base.WalkExpr(e);
        }
    }

    /// <summary>
    /// Calling a *private* generic must emit a call to the symbol its instantiation is defined
    /// under. The call site and the later generic drain mangle separately, and when they disagreed
    /// Dce dropped the definition and the C failed to link.
    /// </summary>
    [Theory]
    [InlineData("private")]
    [InlineData("")]   // no modifier: the visible-to-importers case, which 'public' cannot spell
    public void InstancesAreCalledByTheirOwnName(string vis)
    {
        var (diag, module) = SingleFileCompile.Check(
            $"{vis} T func G[T](T a) {{ return a; }} " +
            "realm kernel { entry func Main() { let x = G(1); let y = x; } }");
        Assert.False(diag.HasErrors);

        var defined = module!.FreeFunctions.Select(f => f.CName).ToHashSet(StringComparer.Ordinal);
        var collector = new CallTargetCollector();
        foreach (var f in module.FreeFunctions)
            if (f.Body != null) collector.Collect(f.Body);

        // Names are densified by this point, so the instantiation is matched by structure, not
        // spelling: the entry point calls exactly one thing, and that thing must be defined.
        Assert.NotEmpty(collector.Targets);
        Assert.All(collector.Targets, t => Assert.Contains(t, defined));
    }

    /// <summary>
    /// The lowered form of an ARC 'release' on an unmanaged value is a bare '(void)x' discard,
    /// which is structurally pure. The user still wrote a call, so it must not be reported as a
    /// no-effect statement - this shape appears throughout libgata's containers.
    /// </summary>
    [Fact]
    public void UnmanagedReleaseIsNotNoEffect()
    {
        AssertNoWarn(Codes.NoEffect,
            "realm kernel { entry func Main() { unsafe { let int x = 1; release(x); } } }");
    }

    #endregion

    #region Union comparison hazards

    // Union equality is generated, so what it does to each payload is invisible at the
    // comparison. These two say so where it will not mean "holds the same value". Both are paired
    // with a negative case, since an unsilenceable warning is worse than none.

    private const string PlainClass = "class Plain { public int n; } ";
    private const string ValuedClass =
        "class Valued { public int n; public operator bool func ==(Valued o) { return self.n == o.n; } } ";

    [Fact]
    public void UncomparableClassPayloadWarns()
    {
        var w = AssertWarns(Codes.IdentityPayloadComparison,
            PlainClass + "union U { P(Plain p), K(int n) } " +
            "realm kernel { entry func Main() { let bool b = U.K(1) == U.K(2); } }");

        Assert.Contains("'U'", w.Message);
        Assert.Contains("'P.p'", w.Message);
        Assert.Contains("identity", w.Message);
        Assert.Contains(w.Hints, h => h.Contains("'=='"));
    }

    /// <summary>
    /// The negative case that matters most: declaring '==' on the payload class is the fix the
    /// warning suggests, so it has to actually silence it.
    /// </summary>
    [Fact]
    public void ClassPayloadWithEqualityIsSilent()
    {
        AssertNoWarn(Codes.IdentityPayloadComparison,
            ValuedClass + "union U { V(Valued v), K(int n) } " +
            "realm kernel { entry func Main() { let bool b = U.K(1) == U.K(2); } }");
    }

    /// <summary>
    /// A generic instantiation as a payload must not warn, however it is reached. This is the shape
    /// of a recursive sum type, and putting one in a List stamps an IndexOf that made the warning
    /// fire inside List.g - where nothing the author writes can silence it.
    /// </summary>
    [Fact]
    public void GenericPayloadIsSilent()
    {
        AssertNoWarn(Codes.IdentityPayloadComparison,
            "class Crate[T] { public T item; } union U { K(Crate[int] c), N(int n) } " +
            "realm kernel { entry func Main() { let bool b = U.N(1) == U.N(2); } }");
    }

    /// <summary>
    /// The exemption above is for generic instantiations only - an ordinary class payload with no
    /// '==' is still actionable, and still reported, even alongside an exempt one.
    /// </summary>
    [Fact]
    public void OrdinaryPayloadWarnsBesideExempt()
    {
        var w = AssertWarns(Codes.IdentityPayloadComparison,
            PlainClass + "class Crate[T] { public T item; } " +
            "union U { K(Crate[int] c), P(Plain p), N(int n) } " +
            "realm kernel { entry func Main() { let bool b = U.N(1) == U.N(2); } }");

        Assert.Contains("'P.p'", w.Message);
        Assert.DoesNotContain("Crate", w.Message);
    }

    [Fact]
    public void UnionWithNoClassPayloadIsSilent()
    {
        AssertNoWarn(Codes.IdentityPayloadComparison,
            "union U { A(int n), B([2]int a), C } " +
            "realm kernel { entry func Main() { let bool b = U.C() == U.A(1); } }");
    }

    /// <summary>
    /// Declaring such a union is fine; only comparing one is worth a word. A union nobody compares
    /// behaves exactly as before and must stay silent.
    /// </summary>
    [Fact]
    public void UncomparedUnionIsSilent()
    {
        AssertNoWarn(Codes.IdentityPayloadComparison,
            PlainClass + "union U { P(Plain p), K(int n) } " +
            "realm kernel { entry func Main() { let U u = U.K(1); } }");
    }

    [Fact]
    public void FloatPayloadWarns()
    {
        var w = AssertWarns(Codes.ImprecisePayloadComparison,
            "union U { F(float f), K(int n) } " +
            "realm kernel { entry func Main() { let bool b = U.K(1) == U.K(2); } }");

        Assert.Contains("'F.f'", w.Message);
        Assert.Contains("floating-point", w.Message);
    }

    [Fact]
    public void IntegerPayloadIsSilent()
    {
        AssertNoWarn(Codes.ImprecisePayloadComparison,
            "union U { A(int n), B(int64 m), C } " +
            "realm kernel { entry func Main() { let bool b = U.C() == U.A(1); } }");
    }

    /// <summary>
    /// Hazards inside a nested union are reported against the union actually being compared,
    /// qualified by where they live. Reporting 'Ident.p' unqualified would read as a variant of the
    /// outer union and send the author to the wrong declaration.
    /// </summary>
    [Fact]
    public void NestedHazardsNameTheirOwner()
    {
        var w = AssertWarns(Codes.IdentityPayloadComparison,
            PlainClass + "union Inner { P(Plain p), K(int n) } union Outer { W(Inner i), J(int n) } " +
            "realm kernel { entry func Main() { let bool b = Outer.J(1) == Outer.J(2); } }");

        Assert.Contains("'Outer'", w.Message);
        Assert.Contains("'Inner.P.p'", w.Message);
    }

    /// <summary>
    /// A union cannot contain itself by value, but it can be reached twice through two different
    /// variants of a nested union. The walk must terminate rather than recurse forever.
    /// </summary>
    [Fact]
    public void HazardWalkTerminatesOnSharing()
    {
        var w = AssertWarns(Codes.IdentityPayloadComparison,
            PlainClass + "union Leaf { P(Plain p), K(int n) } " +
            "union Mid { A(Leaf l), B(Leaf l2), C(int n) } " +
            "union Top { X(Mid m), Y(Leaf l), Z(int n) } " +
            "realm kernel { entry func Main() { let bool b = Top.Z(1) == Top.Z(2); } }");

        Assert.Contains("'Top'", w.Message);
    }

    /// <summary>
    /// '!=' generates the same comparison, so it must warn identically.
    /// </summary>
    [Fact]
    public void NotEqualsWarnsAsEqualsDoes()
    {
        AssertWarns(Codes.IdentityPayloadComparison,
            PlainClass + "union U { P(Plain p), K(int n) } " +
            "realm kernel { entry func Main() { let bool b = U.K(1) != U.K(2); } }");
    }

    #endregion

    #region Union comparisons keep the existing lint coverage

    // Making unions comparable moved their '==' off IrBinOp onto a call, so every lint matching
    // IrBinOp silently stopped seeing them - and a warning that stops firing breaks no test.
    // These pin the parity: what is said about 'i == i' must be said about 'u == u'.

    private const string SmallUnion = "union U { A(int n), B } ";

    [Fact]
    public void SelfUnionComparisonWarns()
    {
        AssertWarns(Codes.SelfComparison,
            SmallUnion + "realm kernel { entry func Main() { let U u = U.B(); if (u == u) { } } }");
    }

    [Fact]
    public void SelfUnionNotEqualsWarns()
    {
        AssertWarns(Codes.SelfComparison,
            SmallUnion + "realm kernel { entry func Main() { let U u = U.B(); if (u != u) { } } }");
    }

    [Fact]
    public void TwoUnionsAreNotSelfComparison()
    {
        AssertNoWarn(Codes.SelfComparison,
            SmallUnion + "realm kernel { entry func Main() { let U u = U.B(); let U v = U.A(1); if (u == v) { } } }");
    }

    /// <summary>
    /// A comparison written where an assignment was meant. The hint is asserted too: it is the
    /// entire reason this warning is worth having, and it is selected by recognising the comparison
    /// shape, which a union no longer matches by default.
    /// </summary>
    [Fact]
    public void UnionComparisonStatementHintsAssign()
    {
        var w = AssertWarns(Codes.NoEffect,
            SmallUnion + "realm kernel { entry func Main() { let U u = U.B(); let U v = U.A(1); u == v; } }");

        Assert.Contains(w.Hints, h => h.Contains("use '=' to assign"));
    }

    #endregion
}
