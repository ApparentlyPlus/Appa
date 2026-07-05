namespace Appa.Tests;

using Appa;

/// <summary>
/// Semantic enforcement coverage for the rules that previously fell through
/// unchecked: postfix operand validation, let-type inference limits, duplicate
/// switch labels, enum/union member hygiene, and control-flow divergence.
/// </summary>
public class SemanticDiagnosticsTests
{
    /// <summary>
    /// Checks the source and asserts it produces at least one error with the code.
    /// </summary>
    private static void AssertError(string code, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.True(diag.HasErrors, $"expected {code} but no errors were produced");
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == code);
    }

    /// <summary>
    /// Checks the source and asserts it produces no errors at all.
    /// </summary>
    private static void AssertClean(string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
    }

    #region Postfix operand validation

    /// <summary>
    /// '++'/'--' on a non-lvalue is rejected instead of emitting broken C.
    /// </summary>
    [Theory]
    [InlineData("kernel { entry func Main() { 5++; } }")]
    [InlineData("int func F() { return 1; } kernel { entry func Main() { F()++; } }")]
    [InlineData("kernel { entry func Main() { (1 + 2)--; } }")]
    public void PostfixOnNonLvalueIsRejected(string src)
    {
        AssertError(Codes.NotAnLvalue, src);
    }

    /// <summary>
    /// '++' on a non-numeric operand is a type error.
    /// </summary>
    [Fact]
    public void PostfixOnNonNumericIsRejected()
    {
        AssertError(Codes.TypeMismatch,
            "kernel { entry func Main() { let bool b = true; b++; } }");
    }

    /// <summary>
    /// Pointer '++' requires an unsafe block; inside one it is accepted.
    /// </summary>
    [Fact]
    public void PointerPostfixRequiresUnsafe()
    {
        AssertError(Codes.UnsafeRequired,
            "kernel { entry func Main() { let int* p = null; p++; } }");
        AssertClean(
            "kernel { entry func Main() { unsafe { let int x = 1; let int* p = &x; p++; } } }");
    }

    /// <summary>
    /// '++' on variables, fields, and elements remains valid.
    /// </summary>
    [Fact]
    public void PostfixOnLvaluesStillChecks()
    {
        AssertClean("""
        class C { int n; public void func Bump() { self.n++; } }
        kernel { entry func Main() {
          let int i = 0; i++; i--;
          let a = [1, 2, 3]; a[0]++;
          let C c = new C(); c.Bump();
        } }
        """);
    }

    #endregion

    #region Let-type inference

    /// <summary>
    /// A let with neither type nor initializer no longer silently becomes 'int'.
    /// </summary>
    [Fact]
    public void LetWithoutTypeOrInitIsRejected()
    {
        AssertError(Codes.CannotInfer, "kernel { entry func Main() { let x; } }");
    }

    /// <summary>
    /// A let initialized from 'null' has no inferable type.
    /// </summary>
    [Fact]
    public void LetFromNullIsRejected()
    {
        AssertError(Codes.CannotInfer, "kernel { entry func Main() { let x = null; } }");
    }

    /// <summary>
    /// A let initialized from a void call no longer silently declares a void local.
    /// </summary>
    [Fact]
    public void LetFromVoidCallIsRejected()
    {
        AssertError(Codes.CannotInfer,
            "void func V() { } kernel { entry func Main() { let x = V(); } }");
    }

    /// <summary>
    /// A typed let without an initializer remains valid.
    /// </summary>
    [Fact]
    public void TypedLetWithoutInitStillChecks()
    {
        AssertClean("kernel { entry func Main() { let int x; x = 5; if (x == 5) { } } }");
    }

    /// <summary>
    /// A typed let initialized from 'null' remains valid for reference types.
    /// </summary>
    [Fact]
    public void TypedLetFromNullStillChecks()
    {
        AssertClean("class Box { int v; } kernel { entry func Main() { let Box b = null; if (b == null) { } } }");
    }

    #endregion

    #region Switch label hygiene

    /// <summary>
    /// The same constant handled by two case arms is a duplicate, whether spelled
    /// as the same literal, split across arms, or as an equal char/int pair.
    /// </summary>
    [Theory]
    [InlineData("kernel { entry func Main() { let int x = 1; switch (x) { case 1 { } case 1 { } } } }")]
    [InlineData("kernel { entry func Main() { let int x = 1; switch (x) { case 1, 2, 1 { } } } }")]
    [InlineData("kernel { entry func Main() { let int x = 1; switch (x) { case 'a' { } case 97 { } } } }")]
    [InlineData("enum E { A, B } kernel { entry func Main() { let E e = E.A; switch (e) { case E.A { } case E.A { } default { } } } }")]
    public void DuplicateSwitchLabelIsRejected(string src)
    {
        AssertError(Codes.DuplicateName, src);
    }

    /// <summary>
    /// Distinct labels across arms remain valid.
    /// </summary>
    [Fact]
    public void DistinctSwitchLabelsStillCheck()
    {
        AssertClean("kernel { entry func Main() { let int x = 1; switch (x) { case 1, 2 { } case 3 { } default { } } } }");
    }

    #endregion

    #region Enum and union hygiene

    /// <summary>
    /// A negative integer literal is a valid explicit enum value.
    /// </summary>
    [Fact]
    public void NegativeEnumValueChecks()
    {
        AssertClean("""
        enum E { Invalid = -1, Zero = 0, One }
        kernel { entry func Main() { let E e = E.Invalid; if (e == E.Invalid) { } } }
        """);
    }

    /// <summary>
    /// A non-literal enum value is still rejected.
    /// </summary>
    [Fact]
    public void NonLiteralEnumValueIsRejected()
    {
        AssertError(Codes.TypeMismatch,
            "enum E { A = 1 + 2 } kernel { entry func Main() { } }");
    }

    /// <summary>
    /// Duplicate enum member names are rejected instead of collapsing silently.
    /// </summary>
    [Fact]
    public void DuplicateEnumMemberIsRejected()
    {
        AssertError(Codes.DuplicateName,
            "enum E { A, B, A } kernel { entry func Main() { } }");
    }

    /// <summary>
    /// Duplicate union variant names are rejected.
    /// </summary>
    [Fact]
    public void DuplicateUnionVariantIsRejected()
    {
        AssertError(Codes.DuplicateName,
            "union U { A(int x), B, A } kernel { entry func Main() { } }");
    }

    #endregion

    #region Control-flow divergence

    /// <summary>
    /// A loop with no exit counts as diverging, so a non-void function ending in
    /// one is not flagged for a missing return.
    /// </summary>
    [Theory]
    [InlineData("int func F() { for (;;) { } } kernel { entry func Main() { let int x = F(); } }")]
    [InlineData("int func F() { while (true) { } } kernel { entry func Main() { let int x = F(); } }")]
    public void InfiniteLoopSatisfiesMissingReturn(string src)
    {
        AssertClean(src);
    }

    /// <summary>
    /// A 'while (true)' that can break out does not count as diverging, so the
    /// missing return is still reported.
    /// </summary>
    [Fact]
    public void BreakableInfiniteLoopStillNeedsReturn()
    {
        AssertError(Codes.MissingReturn,
            "int func F() { while (true) { break; } } kernel { entry func Main() { let int x = F(); } }");
    }

    /// <summary>
    /// A break buried in a nested loop does not escape the outer loop, so the
    /// outer 'while (true)' still diverges.
    /// </summary>
    [Fact]
    public void BreakInNestedLoopDoesNotEscapeOuter()
    {
        AssertClean("""
        int func F() { while (true) { while (true) { break; } } }
        kernel { entry func Main() { let int x = F(); } }
        """);
    }

    #endregion

    #region Match diagnostics

    /// <summary>
    /// Match-arm errors point at the offending case, not the whole match statement.
    /// </summary>
    [Fact]
    public void UnknownVariantErrorPointsAtTheCase()
    {
        var src = """
        union U { A, B }
        kernel { entry func Main() {
          let U u = U.A();
          match (u) { case A { } case B { } case Bogus { } }
        } }
        """;
        var (diag, _) = SingleFileCompile.Check(src);
        var err = diag.All.First(d => d.Code == Codes.UndefinedVariable);
        Assert.Equal(src.IndexOf("case Bogus"), err.Loc.Span.Start);
    }

    #endregion
}
