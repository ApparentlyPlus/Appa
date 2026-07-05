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
    /// A non-constant enum value is rejected.
    /// </summary>
    [Fact]
    public void NonConstEnumValueIsStillRejected()
    {
        AssertError(Codes.TypeMismatch,
            "enum E { A = \"str\" } kernel { entry func Main() { } }");
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

    #region For-step assignment

    /// <summary>
    /// A for-step assignment resolves, lowers, and emits as an inline C step expression.
    /// </summary>
    [Fact]
    public void ForStepAssignmentEmitsInline()
    {
        var files = SingleFileCompile.Emit("""
        @preamble(kernel)
        native { }
        kernel { entry func Main() {
          let int sum = 0;
          for (let int i = 0; i < 100; i = i + 1) { sum = sum + i; }
          if (sum > 0) { } else { }
        } }
        """);
        Assert.NotEmpty(files);
        Assert.Contains(files, f => f.Content.Contains("i = (i + 1))"));
    }

    /// <summary>
    /// A for-step assignment goes through full statement checking: bad types and
    /// non-lvalue targets are rejected the same as anywhere else.
    /// </summary>
    [Theory]
    [InlineData("kernel { entry func Main() { for (let int i = 0; i < 5; i = \"x\") { } } }")]
    [InlineData("kernel { entry func Main() { for (let int i = 0; i < 5; i &= 1.5) { } } }")]
    public void ForStepAssignmentIsTypeChecked(string src)
    {
        AssertError(Codes.TypeMismatch, src);
    }

    #endregion

    #region Enum const folding

    /// <summary>
    /// Constant expressions in enum values fold at compile time, including
    /// references to earlier members of the same enum.
    /// </summary>
    [Fact]
    public void EnumConstExprsFold()
    {
        var (diag, module) = SingleFileCompile.Check("""
        enum Flags {
          None = 0,
          Read = 1 << 0,
          Write = 1 << 1,
          Exec = 1 << 2,
          All = Read | Write | Exec,
          Also = Flags.All,
          Neg = -(2 * 3),
          Ch = 'x',
          Masked = ~0 & 15
        }
        kernel { entry func Main() { let Flags f = Flags.All; if (f == Flags.All) { } } }
        """);
        Assert.False(diag.HasErrors, string.Join("; ", diag.All.Select(d => d.Message)));
        var e = module!.Enums.Single();
        string? ValueOf(string name) => e.Members.Single(m => m.Item1 == name).Item2;
        Assert.Equal("7", ValueOf("All"));
        Assert.Equal("7", ValueOf("Also"));
        Assert.Equal("-6", ValueOf("Neg"));
        Assert.Equal("120", ValueOf("Ch"));
        Assert.Equal("15", ValueOf("Masked"));
    }

    /// <summary>
    /// Implicit members count from the previous folded value, so a later reference
    /// to an implicit member folds to the right number.
    /// </summary>
    [Fact]
    public void ImplicitEnumMembersFoldFromPreviousValue()
    {
        var (diag, module) = SingleFileCompile.Check("""
        enum E { A = 10, B, C = B + 5 }
        kernel { entry func Main() { let E e = E.C; if (e == E.C) { } } }
        """);
        Assert.False(diag.HasErrors);
        Assert.Equal("16", module!.Enums.Single().Members.Single(m => m.Item1 == "C").Item2);
    }

    /// <summary>
    /// Non-constant enum values are still rejected: unknown names, forward
    /// references, and division by zero.
    /// </summary>
    [Theory]
    [InlineData("enum E { A = x + 1 } kernel { entry func Main() { } }")]
    [InlineData("enum E { A = B + 1, B } kernel { entry func Main() { } }")]
    [InlineData("enum E { A = 1 / 0 } kernel { entry func Main() { } }")]
    public void NonConstEnumValueIsRejected(string src)
    {
        AssertError(Codes.TypeMismatch, src);
    }

    #endregion

    #region Operator overload checking

    private const string VecDecl = """
    class Vec {
      public int x;
      func _init(int a) { self.x = a; }
      operator func +(Vec other) -> Vec { return new Vec(self.x + other.x); }
    }
    """;

    /// <summary>
    /// The right operand of a user-defined operator is checked against the
    /// operator's declared parameter type.
    /// </summary>
    [Theory]
    [InlineData("let Vec c = a + 5;")]
    [InlineData("let Vec c = a + true;")]
    public void OperatorOperandTypeIsChecked(string stmt)
    {
        AssertError(Codes.ArgTypeMismatch, VecDecl + $$"""
        kernel { entry func Main() {
          let Vec a = new Vec(1);
          {{stmt}}
        } }
        """);
    }

    /// <summary>
    /// Compound assignment through an operator overload checks the operand too.
    /// </summary>
    [Fact]
    public void CompoundOperatorOperandTypeIsChecked()
    {
        AssertError(Codes.ArgTypeMismatch, VecDecl + """
        kernel { entry func Main() {
          let Vec a = new Vec(1);
          a += 5;
        } }
        """);
    }

    /// <summary>
    /// A well-typed operand still resolves cleanly.
    /// </summary>
    [Fact]
    public void MatchingOperatorOperandStillChecks()
    {
        AssertClean(VecDecl + """
        kernel { entry func Main() {
          let Vec a = new Vec(1);
          let Vec b = new Vec(2);
          let Vec c = a + b;
          a += b;
          if (c.x >= 0) { } else { }
        } }
        """);
    }

    /// <summary>
    /// Operator declarations enforce their arity: one parameter for binary and '[]',
    /// two for '[]='. A wrong-arity indexer no longer crashes the resolver at use sites.
    /// </summary>
    [Theory]
    [InlineData("class C { int v; operator func +(C a, C b) -> C { return a; } } kernel { entry func Main() { } }")]
    [InlineData("class C { int v; operator func [](int i, int j) -> int { return 0; } } kernel { entry func Main() { let C c = new C(); let int x = c[0]; } }")]
    [InlineData("class C { int v; operator func []=(int i) { } } kernel { entry func Main() { let C c = new C(); c[0] = 1; } }")]
    public void OperatorArityIsEnforced(string src)
    {
        AssertError(Codes.WrongArgCount, src);
    }

    #endregion
}
