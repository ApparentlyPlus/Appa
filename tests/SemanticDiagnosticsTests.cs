namespace Appa.Tests;

using Appa;

/// <summary>
/// Semantic enforcement coverage for the rules that previously fell through unchecked: postfix
/// operand validation, let-type inference limits, duplicate switch labels, enum/union member
/// hygiene, and control-flow divergence.
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

    [Theory]
    [InlineData("realm kernel { entry func Main() { 5++; } }")]
    [InlineData("int func F() { return 1; } realm kernel { entry func Main() { F()++; } }")]
    [InlineData("realm kernel { entry func Main() { (1 + 2)--; } }")]
    public void PostfixOnNonLvalueIsRejected(string src)
    {
        AssertError(Codes.NotAnLvalue, src);
    }

    [Fact]
    public void PostfixOnNonNumericIsRejected()
    {
        AssertError(Codes.TypeMismatch,
            "realm kernel { entry func Main() { let bool b = true; b++; } }");
    }

    [Fact]
    public void PointerPostfixRequiresUnsafe()
    {
        AssertError(Codes.UnsafeRequired,
            "realm kernel { entry func Main() { let int* p = null; p++; } }");
        AssertClean(
            "realm kernel { entry func Main() { unsafe { let int x = 1; let int* p = &x; p++; } } }");
    }

    [Fact]
    public void PostfixOnLvaluesStillChecks()
    {
        AssertClean("""
        class C { int n; public void func Bump() { self.n++; } }
        realm kernel { entry func Main() {
          let int i = 0; i++; i--;
          let a = [1, 2, 3]; a[0]++;
          let C c = new C(); c.Bump();
        } }
        """);
    }

    #endregion

    #region Let-type inference

    [Fact]
    public void LetWithoutTypeOrInitIsRejected()
    {
        AssertError(Codes.CannotInfer, "realm kernel { entry func Main() { let x; } }");
    }

    [Fact]
    public void LetFromNullIsRejected()
    {
        AssertError(Codes.CannotInfer, "realm kernel { entry func Main() { let x = null; } }");
    }

    [Fact]
    public void LetFromVoidCallIsRejected()
    {
        AssertError(Codes.CannotInfer,
            "void func V() { } realm kernel { entry func Main() { let x = V(); } }");
    }

    [Fact]
    public void TypedLetWithoutInitStillChecks()
    {
        AssertClean("realm kernel { entry func Main() { let int x; x = 5; if (x == 5) { } } }");
    }

    [Fact]
    public void TypedLetFromNullStillChecks()
    {
        AssertClean("class Box { int v; } realm kernel { entry func Main() { let Box b = null; if (b == null) { } } }");
    }

    #endregion

    #region Field-type inference

    /// <summary>
    /// A field with a literal initializer and no type infers its type from the literal, exactly
    /// like 'let'. Each literal kind maps to its natural type, verified by using the field where
    /// that type is required.
    /// </summary>
    [Theory]
    [InlineData("v = 5;", "let int x = c.v;")]
    [InlineData("v = -5;", "let int x = c.v;")]
    [InlineData("v = 1.5;", "let double x = c.v;")]
    [InlineData("v = true;", "let bool x = c.v;")]
    [InlineData("v = 'x';", "let char x = c.v;")]
    public void FieldInfersFromItsLiteral(string field, string use)
    {
        AssertClean($$"""
            class C { public {{field}} }
            realm kernel { entry func Main() {
                let C c = new C();
                {{use}}
            } }
            """);
    }

    [Fact]
    public void NonLiteralFieldCannotInfer()
    {
        AssertError(Codes.CannotInfer, "class C { v = 1 + 2; }");
    }

    [Fact]
    public void InferredFieldWorksInsideItsClass()
    {
        AssertClean("""
            class Counter {
                count = 0;
                public void func Bump() { self.count = self.count + 1; }
                public int func Value() { return self.count; }
            }
            realm kernel { entry func Main() {
                let Counter c = new Counter();
                c.Bump();
                let int v = c.Value();
            } }
            """);
    }

    #endregion

    #region Switch label hygiene

    [Theory]
    [InlineData("realm kernel { entry func Main() { let int x = 1; switch (x) { case 1 { } case 1 { } } } }")]
    [InlineData("realm kernel { entry func Main() { let int x = 1; switch (x) { case 1, 2, 1 { } } } }")]
    [InlineData("realm kernel { entry func Main() { let int x = 1; switch (x) { case 'a' { } case 97 { } } } }")]
    [InlineData("enum E { A, B } realm kernel { entry func Main() { let E e = E.A; switch (e) { case E.A { } case E.A { } default { } } } }")]
    public void DuplicateSwitchLabelIsRejected(string src)
    {
        AssertError(Codes.DuplicateName, src);
    }

    [Fact]
    public void DistinctSwitchLabelsStillCheck()
    {
        AssertClean("realm kernel { entry func Main() { let int x = 1; switch (x) { case 1, 2 { } case 3 { } default { } } } }");
    }

    #endregion

    #region Enum and union hygiene

    [Fact]
    public void NegativeEnumValueChecks()
    {
        AssertClean("""
        enum E { Invalid = -1, Zero = 0, One }
        realm kernel { entry func Main() { let E e = E.Invalid; if (e == E.Invalid) { } } }
        """);
    }

    [Fact]
    public void NonConstEnumValueIsStillRejected()
    {
        AssertError(Codes.TypeMismatch,
            "enum E { A = \"str\" } realm kernel { entry func Main() { } }");
    }

    [Fact]
    public void DuplicateEnumMemberIsRejected()
    {
        AssertError(Codes.DuplicateName,
            "enum E { A, B, A } realm kernel { entry func Main() { } }");
    }

    [Fact]
    public void DuplicateUnionVariantIsRejected()
    {
        AssertError(Codes.DuplicateName,
            "union U { A(int x), B, A } realm kernel { entry func Main() { } }");
    }

    /// <summary>
    /// Naming a variant without calling it must produce one diagnostic saying so, not a cascade:
    /// 'U.Nil' used to give three errors for one missing pair of parentheses, none mentioning
    /// unions. The count is asserted, since good wording is little use buried.
    /// </summary>
    [Fact]
    public void VariantWithoutCallGivesOneError()
    {
        var (diag, _) = SingleFileCompile.Check(
            "union U { A(int x), Nil } realm kernel { entry func Main() { let U q = U.Nil; } }");

        var errors = diag.All.Where(d => d.Severity == Severity.Error).ToList();
        Assert.Single(errors);
        Assert.Equal(Codes.UndefinedVariable, errors[0].Code);
        Assert.Contains("union variant", errors[0].Message);
        Assert.Contains(errors[0].Hints, h => h.Contains("U.Nil()"));
    }

    /// <summary>
    /// A bad type argument is one mistake, reported once, naming the instantiation. A stamped
    /// instance lives in the template's file, so without both the author gets copies of one
    /// complaint in source they never wrote: 'Map[String, int]' gave seven.
    /// </summary>
    [Fact]
    public void BadTypeArgReportedOnce()
    {
        var (diag, _) = SingleFileCompile.Check(
            "class C { public int n; } " +
            "class G[T] { " +
            "  public usize func A(T x) { unsafe { return x as usize; } } " +
            "  public usize func B(T x) { unsafe { return x as usize; } } " +
            "  public usize func C2(T x) { unsafe { return x as usize; } } } " +
            "realm kernel { entry func Main() { let G[C] g = new G[C](); } }");

        var errors = diag.All.Where(d => d.Severity == Severity.Error).ToList();
        Assert.Single(errors);
        Assert.Equal(Codes.InvalidCast, errors[0].Code);
        Assert.Contains(errors[0].Hints, h => h.Contains("G[C]"));
    }

    [Fact]
    public void UnknownVariantNamesTheUnion()
    {
        var (diag, _) = SingleFileCompile.Check(
            "union U { A(int x), Nil } realm kernel { entry func Main() { let U q = U.Zzz; } }");

        var errors = diag.All.Where(d => d.Severity == Severity.Error).ToList();
        Assert.Single(errors);
        Assert.Contains("has no variant 'Zzz'", errors[0].Message);
    }

    #endregion

    #region Control-flow divergence

    [Theory]
    [InlineData("int func F() { for (;;) { } } realm kernel { entry func Main() { let int x = F(); } }")]
    [InlineData("int func F() { while (true) { } } realm kernel { entry func Main() { let int x = F(); } }")]
    public void InfiniteLoopSatisfiesReturn(string src)
    {
        AssertClean(src);
    }

    [Fact]
    public void BreakableLoopStillNeedsReturn()
    {
        AssertError(Codes.MissingReturn,
            "int func F() { while (true) { break; } } realm kernel { entry func Main() { let int x = F(); } }");
    }

    [Fact]
    public void BreakDoesNotEscapeTheOuterLoop()
    {
        AssertClean("""
        int func F() { while (true) { while (true) { break; } } }
        realm kernel { entry func Main() { let int x = F(); } }
        """);
    }

    #endregion

    #region Match diagnostics

    [Fact]
    public void UnknownVariantPointsAtTheCase()
    {
        var src = """
        union U { A, B }
        realm kernel { entry func Main() {
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

    [Fact]
    public void ForStepAssignmentEmitsInline()
    {
        var files = SingleFileCompile.Emit("""
        @preamble(kernel)
        native { }
        realm kernel { entry func Main() {
          let int sum = 0;
          for (let int i = 0; i < 100; i = i + 1) { sum = sum + i; }
          if (sum > 0) { } else { }
        } }
        """);
        Assert.NotEmpty(files);
        Assert.Contains(files, f => f.Content.Contains("i = (i + 1))"));
    }

    [Theory]
    [InlineData("realm kernel { entry func Main() { for (let int i = 0; i < 5; i = \"x\") { } } }")]
    [InlineData("realm kernel { entry func Main() { for (let int i = 0; i < 5; i &= 1.5) { } } }")]
    public void ForStepAssignmentIsTypeChecked(string src)
    {
        AssertError(Codes.TypeMismatch, src);
    }

    #endregion

    #region Enum const folding

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
        realm kernel { entry func Main() { let Flags f = Flags.All; if (f == Flags.All) { } } }
        """);
        Assert.False(diag.HasErrors, string.Join("; ", diag.All.Select(d => d.Message)));
        var e = module!.Enums.Single();
        string? ValueOf(string name) => e.Members.Single(m => m.Name == name).CValue;
        Assert.Equal("7", ValueOf("All"));
        Assert.Equal("7", ValueOf("Also"));
        Assert.Equal("-6", ValueOf("Neg"));
        Assert.Equal("120", ValueOf("Ch"));
        Assert.Equal("15", ValueOf("Masked"));
    }

    [Fact]
    public void ImplicitEnumMembersFold()
    {
        var (diag, module) = SingleFileCompile.Check("""
        enum E { A = 10, B, C = B + 5 }
        realm kernel { entry func Main() { let E e = E.C; if (e == E.C) { } } }
        """);
        Assert.False(diag.HasErrors);
        Assert.Equal("16", module!.Enums.Single().Members.Single(m => m.Name == "C").CValue);
    }

    [Theory]
    [InlineData("enum E { A = x + 1 } realm kernel { entry func Main() { } }")]
    [InlineData("enum E { A = B + 1, B } realm kernel { entry func Main() { } }")]
    [InlineData("enum E { A = 1 / 0 } realm kernel { entry func Main() { } }")]
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
      public operator Vec func +(Vec other) { return new Vec(self.x + other.x); }
    }
    """;

    [Theory]
    [InlineData("let Vec c = a + 5;")]
    [InlineData("let Vec c = a + true;")]
    public void OperatorOperandTypeIsChecked(string stmt)
    {
        AssertError(Codes.ArgTypeMismatch, VecDecl + $$"""
        realm kernel { entry func Main() {
          let Vec a = new Vec(1);
          {{stmt}}
        } }
        """);
    }

    [Fact]
    public void CompoundOperandTypeIsChecked()
    {
        AssertError(Codes.ArgTypeMismatch, VecDecl + """
        realm kernel { entry func Main() {
          let Vec a = new Vec(1);
          a += 5;
        } }
        """);
    }

    [Fact]
    public void MatchingOperandStillChecks()
    {
        AssertClean(VecDecl + """
        realm kernel { entry func Main() {
          let Vec a = new Vec(1);
          let Vec b = new Vec(2);
          let Vec c = a + b;
          a += b;
          if (c.x >= 0) { } else { }
        } }
        """);
    }

    /// <summary>
    /// Operator declarations enforce their arity: one parameter for binary and '[]', two for '[]='.
    /// A wrong-arity indexer no longer crashes the resolver at use sites.
    /// </summary>
    [Theory]
    [InlineData("class C { int v; operator C func +(C a, C b) { return a; } } realm kernel { entry func Main() { } }")]
    [InlineData("class C { int v; operator int func [](int i, int j) { return 0; } } realm kernel { entry func Main() { let C c = new C(); let int x = c[0]; } }")]
    [InlineData("class C { int v; operator func []=(int i) { } } realm kernel { entry func Main() { let C c = new C(); c[0] = 1; } }")]
    public void OperatorArityIsEnforced(string src)
    {
        AssertError(Codes.WrongArgCount, src);
    }

    #endregion

    #region Diagnostic hint placement

    /// <summary>
    /// An undefined-method "did you mean" suggestion is carried in the diagnostic's separate Hints
    /// array (rendered on its own "= help:" line), never spliced into the message text itself - the
    /// message states the problem outright, the hint is a distinct, optional line.
    /// </summary>
    [Fact]
    public void MethodSuggestionIsAHint()
    {
        var (diag, _) = SingleFileCompile.Check("""
            class Console { public static void func Home() { } }
            realm kernel { entry func Main() { Console.Hme(); } }
            """);
        var d = Assert.Single(diag.All, x => x.Code == Codes.UndefinedMethod);
        Assert.DoesNotContain("did you mean", d.Message);
        Assert.Contains("did you mean 'Home'?", d.Hints);
    }

    [Fact]
    public void NoCloseCandidateGivesNoHint()
    {
        var (diag, _) = SingleFileCompile.Check("""
            class Console { public static void func Home() { } }
            realm kernel { entry func Main() { Console.ZzzCompletelyUnrelated(); } }
            """);
        var d = Assert.Single(diag.All, x => x.Code == Codes.UndefinedMethod);
        Assert.Empty(d.Hints);
    }

    #endregion

    #region 'Name[x]' read as an index rather than a type reference

    // 'Maybe[int].Found(7)' and 'arr[i].field' are the same tokens, so the parser keeps both
    // readings and the resolver picks one. These pin the choice, because getting it wrong is
    // invisible on working code and only shows up on the diagnostics for broken code.

    /// <summary>
    /// A field indexed without 'self.' still gets the diagnostic that names the fix. 'items[i].x'
    /// also parses as a type reference, and reading it that way reports "'items_i' is a type" - a
    /// mangled name nobody wrote - instead of the line saying what to do.
    /// </summary>
    [Fact]
    public void IndexedFieldMissingSelfNamesTheFix()
    {
        // The trailing '.x' is what makes this ambiguous: without it the brackets can only be an
        // index, and the type reading is never attempted.
        var (diag, _) = SingleFileCompile.Check("""
            class Pt { public int x; func _init() { } }
            class Holder {
                [2]Pt items;
                func _init() { }
                public int func Bad() { let int i = 0; return items[i].x; }
            }
            realm kernel { entry func Main() { } }
            """);
        Assert.Contains(diag.All, d => d.Code == Codes.UndefinedVariable && d.Message.Contains("self.items"));
        Assert.DoesNotContain(diag.All, d => d.Message.Contains("items_i") || d.Message.Contains("is a type"));
    }

    /// <summary>
    /// A misspelled name reads as an index, so it is reported as the undefined variable it is
    /// rather than as a generic type nobody declared.
    /// </summary>
    [Fact]
    public void IndexedUnknownNameIsUndefined()
    {
        var (diag, _) = SingleFileCompile.Check("""
            class Pt { public int x; func _init() { } }
            realm kernel { entry func Main() {
                let [2]Pt items = default([2]Pt);
                let int i = 0;
                let int n = itemz[i].x;
            } }
            """);
        Assert.Contains(diag.All, d => d.Code == Codes.UndefinedVariable && d.Message.Contains("'itemz'"));
        Assert.DoesNotContain(diag.All, d => d.Message.Contains("itemz_i") || d.Message.Contains("is a type"));
    }

    /// <summary>
    /// Scope only decides between the two readings when both are possible. 'Opt[bool]' has no index
    /// reading, so a local called Opt cannot turn it into one - even though it does shadow the type
    /// wherever the brackets could go either way.
    /// </summary>
    [Fact]
    public void ShadowingLocalNeedsAnIndexReading()
    {
        AssertClean("""
            union Opt[V] { Some(V v), None }
            realm kernel { entry func Main() {
                let [2]int Opt = default([2]int);
                let int i = 0;
                let int shadowed = Opt[i];
                let Opt[bool] o = Opt[bool].Some(true);
            } }
            """);
    }

    /// <summary>
    /// A type reference naming no instantiable generic says which of the three things went wrong,
    /// never in terms of a mangled name. All three used to give one message built from Mangled,
    /// naming a type nobody wrote and, for two of them, the wrong problem.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { entry func Main() { let int n = Nope[int].A(); } }",
                "unknown generic type 'Nope'")]
    [InlineData("union U { A(int v), B } realm kernel { entry func Main() { let U x = U[int].A(1); } }",
                "'U' is not generic")]
    [InlineData("class Box[T] { public T v; func _init() { } } " +
                "realm kernel { entry func Main() { let Box[int] b = new Box[int](); let int n = Box[int].Nope(); } }",
                "'Box[int]' is not a union")]
    public void TypeRefErrorsNameWhatWasWritten(string src, string expected)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.Contains(diag.All, d => d.Message.Contains(expected));
        Assert.DoesNotContain(diag.All, d => d.Message.Contains("Nope_int") || d.Message.Contains("U_int")
                                             || d.Message.Contains("Box_int"));
    }

    #endregion

    #region Expected-type inference for generic union variants

    // A payload-free variant says nothing about the type argument, so the instantiation comes
    // from the enclosing binding. That expectation must not outlive the position it came from.

    private const string OptDecl = """
        union Opt[V] { Some(V v), None }
        int func Take(Opt[int] o) { return 1; }
        Opt[bool] func Wrap(int n) { return Opt.None(); }
        """;

    /// <summary>
    /// An enclosing binding's expected type does not decide a nested call's argument. In 'let
    /// Opt[bool] s = Wrap(Take(Opt.None()));' Take needs Opt[int], and the inherited Opt[bool]
    /// decided it silently, rejecting the program for a mismatch it does not have.
    /// </summary>
    [Fact]
    public void ExpectationDoesNotDecideAnArgument()
    {
        var (diag, _) = SingleFileCompile.Check(OptDecl + """
            realm kernel { entry func Main() { let Opt[bool] s = Wrap(Take(Opt.None())); } }
            """);
        var first = diag.All.First(d => d.Severity == Severity.Error);
        Assert.Equal(Codes.CannotInfer, first.Code);
        Assert.Contains(first.Hints, h => h.Contains("explicit type"));
    }

    /// <summary>
    /// Naming the instantiation at the argument settles it, in that same position.
    /// </summary>
    [Fact]
    public void ExplicitInstantiationSettlesAnArg()
    {
        AssertClean(OptDecl + """
            realm kernel { entry func Main() { let Opt[bool] s = Wrap(Take(Opt[int].None())); } }
            """);
    }

    /// <summary>
    /// The expectation still reaches the position it belongs to: a let and a return.
    /// </summary>
    [Fact]
    public void ExpectationDecidesItsOwnPosition()
    {
        AssertClean(OptDecl + """
            realm kernel { entry func Main() { let Opt[int] a = Opt.None(); let int n = Take(a); } }
            """);
    }

    #endregion
}
