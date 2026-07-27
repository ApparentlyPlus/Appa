namespace Appa.Tests;

using Appa;

/// <summary>
/// Coverage for the lint-grade diagnostics: the warnings that flag code which compiles
/// but almost certainly does not mean what it says (G070-G080), and the single
/// undefined-behaviour error that is decidable from literals alone (G075).
///
/// Every warning here is paired with at least one negative case. A warning that cannot
/// be silenced by writing the code correctly is worse than no warning at all, so the
/// "clean" assertions are the load-bearing half of this file.
/// </summary>
public class WarningDiagnosticsTests
{
    /// <summary>Checks the source and returns every diagnostic carrying the given code.</summary>
    private static Diagnostic[] Of(string code, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        return [.. diag.All.Where(d => d.Code == code)];
    }

    /// <summary>Asserts the source produces exactly one warning with the code, and returns it.</summary>
    private static Diagnostic AssertWarns(string code, string src)
    {
        var hits = Of(code, src);
        Assert.True(hits.Length == 1,
            $"expected exactly one {code}, got {hits.Length}: " +
            string.Join("; ", hits.Select(h => h.Message)));
        Assert.Equal(Severity.Warning, hits[0].Severity);
        return hits[0];
    }

    /// <summary>Asserts the source produces no diagnostic at all with the given code.</summary>
    private static void AssertNoWarn(string code, string src)
    {
        var hits = Of(code, src);
        Assert.True(hits.Length == 0,
            $"expected no {code}, got: " + string.Join("; ", hits.Select(h => h.Message)));
    }

    /// <summary>Asserts the source produces at least one error with the code.</summary>
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
            "kernel { entry func Main() { let x = 1; { let x = 2; let y = x; } let z = x; } }");
        Assert.Contains("shadows", d.Message);
    }

    /// <summary>
    /// Sibling scopes do not nest, so reusing a name across them is not shadowing.
    /// Redeclaring in the *same* scope stays a hard error, not a warning.
    /// </summary>
    [Fact]
    public void SiblingScopesAndSameScopeRedeclarationAreNotShadowing()
    {
        AssertNoWarn(Codes.ShadowedVariable,
            "kernel { entry func Main() { { let x = 1; let a = x; } { let x = 2; let b = x; } } }");
        AssertError(Codes.DuplicateName,
            "kernel { entry func Main() { let x = 1; let x = 2; } }");
        AssertNoWarn(Codes.ShadowedVariable,
            "kernel { entry func Main() { let x = 1; let x = 2; } }");
    }

    #endregion

    #region G071 self-assignment

    [Fact]
    public void SelfAssignmentWarns()
    {
        AssertWarns(Codes.SelfAssignment,
            "kernel { entry func Main() { let x = 1; x = x; } }");
        AssertNoWarn(Codes.SelfAssignment,
            "kernel { entry func Main() { let x = 1; let y = 2; x = y; } }");
    }

    [Fact]
    public void FieldSelfAssignmentIsDistinguishedFromParameterAssignment()
    {
        AssertWarns(Codes.SelfAssignment,
            "class C { int n; public void func M() { self.n = self.n; } } " +
            "kernel { entry func Main() { let c = new C(); c.M(); } }");
        AssertNoWarn(Codes.SelfAssignment,
            "class C { int n; public void func M(int n) { self.n = n; } } " +
            "kernel { entry func Main() { let c = new C(); c.M(1); } }");
    }

    [Fact]
    public void IndexSelfAssignmentRequiresLiteralIndices()
    {
        // Computed indices may denote different elements on each side, so only two identical
        // literal subscripts are enough to call it a self-assignment.
        AssertNoWarn(Codes.SelfAssignment,
            "kernel { entry func Main() { let int[4] a; let i = 0; let j = 1; a[i] = a[j]; } }");
    }

    #endregion

    #region G072 statement with no effect

    [Theory]
    [InlineData("kernel { entry func Main() { let a = 1; a + 1; } }")]
    [InlineData("kernel { entry func Main() { let a = 1; a; } }")]
    [InlineData("kernel { entry func Main() { let a = 1; let b = 2; a == b; } }")]
    public void PureExpressionStatementWarns(string src)
    {
        AssertWarns(Codes.NoEffect, src);
    }

    [Fact]
    public void ComparisonStatementHintsAtAssignment()
    {
        var d = AssertWarns(Codes.NoEffect,
            "kernel { entry func Main() { let a = 1; let b = 2; a == b; } }");
        Assert.Contains(d.Hints, h => h.Contains("'='"));
    }

    [Fact]
    public void DiscardedCallResultDoesNotWarn()
    {
        AssertNoWarn(Codes.NoEffect,
            "int func F() { return 1; } kernel { entry func Main() { F(); } }");
    }

    [Fact]
    public void MutatingStatementsDoNotWarn()
    {
        AssertNoWarn(Codes.NoEffect,
            "kernel { entry func Main() { let a = 1; a = 2; a++; let b = a; } }");
    }

    #endregion

    #region G073 / G078 constant and self-comparing conditions

    [Fact]
    public void LiteralIfConditionWarns()
    {
        var t = AssertWarns(Codes.ConstantCondition,
            "kernel { entry func Main() { if (true) { let a = 1; let b = a; } } }");
        Assert.Contains("always true", t.Message);

        var f = AssertWarns(Codes.ConstantCondition,
            "kernel { entry func Main() { if (false) { let a = 1; let b = a; } } }");
        Assert.Contains("always false", f.Message);
    }

    [Fact]
    public void InfiniteLoopFormsDoNotWarn()
    {
        AssertNoWarn(Codes.ConstantCondition,
            "kernel { entry func Main() { while (true) { break; } } }");
        AssertNoWarn(Codes.ConstantCondition,
            "kernel { entry func Main() { for (let i = 0; ; i = i + 1) { break; } } }");
    }

    [Fact]
    public void SelfComparisonWarnsIncludingInLoops()
    {
        AssertWarns(Codes.SelfComparison,
            "kernel { entry func Main() { let a = 1; if (a == a) { let b = 1; let c = b; } } }");
        AssertWarns(Codes.SelfComparison,
            "kernel { entry func Main() { let a = 1; while (a < a) { break; } } }");
        AssertNoWarn(Codes.SelfComparison,
            "kernel { entry func Main() { let a = 1; let b = 2; if (a == b) { let c = 1; let d = c; } } }");
    }

    #endregion

    #region G074 redundant cast

    [Fact]
    public void SameTypeCastOnAValueWarns()
    {
        AssertWarns(Codes.RedundantCast,
            "kernel { entry func Main() { let int a = 1; let b = (a as int); } }");
    }

    /// <summary>
    /// A cast on a literal pins that literal's width where inference would otherwise choose
    /// it, which is deliberate in bit-manipulation code. libgata relies on this exemption.
    /// </summary>
    [Fact]
    public void SameTypeCastOnALiteralIsExempt()
    {
        AssertNoWarn(Codes.RedundantCast,
            "kernel { entry func Main() { let a = (0x00100000 as int); let b = a; } }");
    }

    [Fact]
    public void WideningCastDoesNotWarn()
    {
        AssertNoWarn(Codes.RedundantCast,
            "kernel { entry func Main() { let int a = 1; let b = (a as int64); } }");
    }

    #endregion

    #region G075 division by a literal zero

    [Theory]
    [InlineData("kernel { entry func Main() { let a = 1; let b = a / 0; } }")]
    [InlineData("kernel { entry func Main() { let a = 1; let b = a % 0; } }")]
    public void IntegerDivisionByLiteralZeroIsAnError(string src)
    {
        AssertError(Codes.DivisionByZero, src);
    }

    /// <summary>
    /// Floating-point division by zero is defined as an infinity, and a non-literal divisor
    /// cannot be judged here. Neither is reported.
    /// </summary>
    [Fact]
    public void FloatAndVariableDivisorsAreNotReported()
    {
        AssertNoWarn(Codes.DivisionByZero,
            "kernel { entry func Main() { let double a = 1.0; let b = a / 0.0; } }");
        AssertNoWarn(Codes.DivisionByZero,
            "kernel { entry func Main() { let a = 1; let z = 0; let b = a / z; } }");
    }

    #endregion

    #region G076 unused parameter

    [Fact]
    public void UnusedParameterWarns()
    {
        var d = AssertWarns(Codes.UnusedParameter,
            "int func F(int a, int b) { return a; } " +
            "kernel { entry func Main() { let r = F(1, 2); } }");
        Assert.Contains("'b'", d.Message);
    }

    [Fact]
    public void UnderscorePrefixOptsOutOfUnusedWarnings()
    {
        AssertNoWarn(Codes.UnusedParameter,
            "int func F(int a, int _b) { return a; } " +
            "kernel { entry func Main() { let r = F(1, 2); } }");
        AssertNoWarn(Codes.UnusedVariable,
            "kernel { entry func Main() { let _scratch = 1; } }");
    }

    [Fact]
    public void NativeBodiesSuppressUnusedParameterWarnings()
    {
        AssertNoWarn(Codes.UnusedParameter,
            "int func F(int a) native { return 0; } " +
            "kernel { entry func Main() { let r = F(1); } }");
    }

    [Fact]
    public void InterpolatedUseCountsAsUse()
    {
        AssertNoWarn(Codes.UnusedParameter,
            "String func F(int a) { return $\"{a}\"; } " +
            "kernel { entry func Main() { let r = F(1); } }");
    }

    #endregion

    #region G077 unreachable default arm

    [Fact]
    public void DefaultOnAFullyCoveredMatchWarns()
    {
        var d = AssertWarns(Codes.UnreachableCase,
            "union U { A(int x), B(int y) } " +
            "kernel { entry func Main() { let u = U.A(1); " +
            "match (u) { case A(x) { let p = x; } case B(y) { let q = y; } default { let r = 0; } } } }");
        Assert.Contains("never run", d.Message);
    }

    [Fact]
    public void NeededDefaultIsSilentAndMissingOneStaysAnError()
    {
        AssertNoWarn(Codes.UnreachableCase,
            "union U { A(int x), B(int y) } " +
            "kernel { entry func Main() { let u = U.A(1); " +
            "match (u) { case A(x) { let p = x; } default { let r = 0; } } } }");
        AssertError(Codes.NonExhaustiveMatch,
            "union U { A(int x), B(int y) } " +
            "kernel { entry func Main() { let u = U.A(1); " +
            "match (u) { case A(x) { let p = x; } } } }");
    }

    #endregion

    #region G080 shift count out of range

    [Theory]
    [InlineData("kernel { entry func Main() { let int a = 1; let b = a << 32; } }")]
    [InlineData("kernel { entry func Main() { let int a = 1; let b = a >> 99; } }")]
    public void OutOfRangeShiftCountIsAnError(string src)
    {
        AssertError(Codes.BadShiftCount, src);
    }

    /// <summary>
    /// The bound follows the operand's own width, so a count illegal for 'int' is fine for
    /// 'int64'. In-range counts and non-literal counts are never reported.
    /// </summary>
    [Fact]
    public void ShiftCountBoundFollowsOperandWidth()
    {
        AssertNoWarn(Codes.BadShiftCount,
            "kernel { entry func Main() { let int64 a = 1; let b = a << 32; } }");
        AssertNoWarn(Codes.BadShiftCount,
            "kernel { entry func Main() { let int a = 1; let b = a << 31; } }");
        AssertNoWarn(Codes.BadShiftCount,
            "kernel { entry func Main() { let int a = 1; let n = 40; let b = a << n; } }");
    }

    #endregion

    #region G080 string that looks interpolated

    [Fact]
    public void PlainStringNamingAnInScopeVariableWarns()
    {
        var d = AssertWarns(Codes.MissingInterpolation,
            "kernel { entry func Main() { let count = 1; let s = \"n={count}\"; let t = s; } }");
        Assert.Contains(d.Hints, h => h.Contains("$\""));
    }

    [Fact]
    public void BracesThatCannotBeInterpolationAreSilent()
    {
        AssertNoWarn(Codes.MissingInterpolation,
            "kernel { entry func Main() { let s = \"{notAVariable}\"; let t = s; } }");
        AssertNoWarn(Codes.MissingInterpolation,
            "kernel { entry func Main() { let count = 1; let s = $\"n={count}\"; let t = s; } }");
        AssertNoWarn(Codes.MissingInterpolation,
            "kernel { entry func Main() { let s = \"struct { int x; }\"; let t = s; } }");
    }

    #endregion

    #region Hint rendering

    [Fact]
    public void HintsRenderOnTheirOwnLinesBelowTheCaret()
    {
        var (diag, _) = SingleFileCompile.Check(
            "kernel { entry func Main() { let a = 1; let b = a / 0; } }");
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
    /// Collects every static-call target in a module, so a call can be checked against the
    /// set of symbols the module actually defines.
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
    /// Calling a *private* generic function must emit a call to the symbol the instantiation is
    /// actually defined under. The call site mangles the instantiation itself, while the body is
    /// emitted later by the generic drain through the private-free-function mangling; when those
    /// two disagreed the call named a symbol nothing defined, Dce then dropped the definition as
    /// unreferenced, and the emitted C failed to link. Public generics never had the problem,
    /// so both are checked here.
    /// </summary>
    [Theory]
    [InlineData("private")]
    [InlineData("public")]
    public void GenericInstantiationsAreCalledUnderTheNameTheyAreDefinedAs(string vis)
    {
        var (diag, module) = SingleFileCompile.Check(
            $"{vis} T func G[T](T a) {{ return a; }} " +
            "kernel { entry func Main() { let x = G(1); let y = x; } }");
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
    /// which is structurally pure. The user still wrote a call, so it must not be reported as
    /// a no-effect statement - this shape appears throughout libgata's containers.
    /// </summary>
    [Fact]
    public void ArcReleaseOfAnUnmanagedValueIsNotAPointlessStatement()
    {
        AssertNoWarn(Codes.NoEffect,
            "kernel { entry func Main() { unsafe { let int x = 1; release(x); } } }");
    }

    #endregion
}
