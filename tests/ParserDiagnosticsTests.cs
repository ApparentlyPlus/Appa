namespace Appa.Tests;

using Appa;

/// <summary>
/// Parse-time error coverage: targeted messages and dedicated codes for the mistakes users actually
/// make, instead of a generic "expected X, found Y".
/// </summary>
public class ParserDiagnosticsTests
{
    /// <summary>
    /// Parses and returns the ParseException the source must produce.
    /// </summary>
    private static ParseException Parse(string src)
    {
        return Assert.Throws<ParseException>(() => SingleFileCompile.Parse(src));
    }

    [Theory]
    [InlineData("func F() { for (let int i = 0; i < 5; i = i + 1) { } }")]
    [InlineData("func F() { for (let int i = 0; i < 5; i += 1) { } }")]
    [InlineData("func F() { for (let int i = 10; i > 0; i >>= 1) { } }")]
    public void AssignmentInForStepParses(string src)
    {
        var func = Assert.IsType<FuncDecl>(SingleFileCompile.Parse(src).Items[0]);
        var forStmt = Assert.IsType<ForStmt>(((BlockBody)func.Body).Block.Stmts[0]);
        Assert.IsType<AssignStmt>(forStmt.Step);
    }

    [Fact]
    public void LetInForStepIsRejected()
    {
        var ex = Parse("func F() { for (let int i = 0; i < 5; let int j = 0) { } }");
        Assert.Contains("cannot declare a variable in the for-loop step", ex.Message);
    }

    [Theory]
    [InlineData("func F() { let int x = 1; if (x = 3) { } }")]
    [InlineData("func F() { let int x = 1; while (x = 3) { } }")]
    [InlineData("func F() { for (let int i = 0; i = 5; i++) { } }")]
    public void AssignInConditionSuggestsEquality(string src)
    {
        var ex = Parse(src);
        Assert.Equal(Codes.AssignInExpr, ex.Code);
        Assert.DoesNotContain("did you mean", ex.Message);
        Assert.Contains("did you mean '=='?", ex.Hints);
    }

    [Theory]
    [InlineData("func F() { let int x = 5 }", "expected ';'")]
    [InlineData("func F() { G(1, 2; }", "expected ')'")]
    [InlineData("func F(int x", "expected ')'")]
    [InlineData("class C int x; }", "expected '{'")]
    public void ExpectedTokenMessagesAreReadable(string src, string expected)
    {
        Assert.Contains(expected, Parse(src).Message);
    }

    [Fact]
    public void EofReadsAsEndOfFile()
    {
        Assert.Contains("end of file", Parse("func F() {").Message);
    }

    [Fact]
    public void ProcessColonWithoutModeIsRejected()
    {
        var ex = Parse("user { process App : sideways { } }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("'foreground' or 'background'", ex.Message);
    }

    [Fact]
    public void DuplicateProcessModeIsRejected()
    {
        var ex = Parse("user { foreground process App : background { } }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("mode specified twice", ex.Message);
    }

    /// <summary>
    /// A process declaration without a foreground/background mode -- in either the leading or the
    /// trailing colon spelling -- is rejected outright rather than silently defaulting, since the
    /// mode is a real semantic choice (TTY/keyboard focus, scheduling visibility).
    /// </summary>
    [Fact]
    public void ProcessWithoutModeIsRejected()
    {
        var ex = Parse("user { process App { thread T { entry func Run() { } } } }");
        Assert.Equal(Codes.MissingProcessMode, ex.Code);
        Assert.Contains("missing a foreground/background mode", ex.Message);
        // The suggested spellings are hints, rendered on their own "= help:" lines, not
        // crammed into the message text.
        var hint = Assert.Single(ex.Hints);
        Assert.Contains("foreground process App", hint);
        Assert.Contains("background process App", hint);
        Assert.DoesNotContain("foreground process App", ex.Message);
    }

    [Fact]
    public void MissingProcessKeywordHints()
    {
        var ex = Parse("user { TicTacToe { thread T { entry func Run() { } } } }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("expected 'func'", ex.Message);
        Assert.DoesNotContain("forget 'process'", ex.Message);
        Assert.Contains(ex.Hints, h => h.Contains("forget 'process'"));
        Assert.Contains(ex.Hints, h => h.Contains("TicTacToe"));
    }

    [Theory]
    [InlineData("enum Color { Red, Green, }")]
    [InlineData("union U { A, B, }")]
    [InlineData("union U { A(int x,) }")]
    public void TrailingCommasCarryTheirCode(string src)
    {
        Assert.Equal(Codes.TrailingComma, Parse(src).Code);
    }

    [Fact]
    public void TypeThenNameHintsMissingLet()
    {
        var ex = Parse("func F() { MyType x = 1; }");
        Assert.Equal(Codes.MissingLet, ex.Code);
        Assert.Contains("expected a statement", ex.Message);
        Assert.DoesNotContain("missing 'let'", ex.Message);
        Assert.Contains(ex.Hints, h => h.Contains("missing 'let'"));
        Assert.Contains(ex.Hints, h => h.Contains("let MyType"));
    }

    [Theory]
    [InlineData("kernel { user { } }")]
    [InlineData("class A { class B { } }")]
    [InlineData("class A { kernel { } }")]
    [InlineData("user { foreground process P { thread T { thread U { entry func R() { } } } } }")]
    public void NestingViolationsCarryTheirCode(string src)
    {
        Assert.Equal(Codes.InvalidNesting, Parse(src).Code);
    }

    [Fact]
    public void RejectionPointsAtTheAnnotation()
    {
        var ex = Parse("@keep\nenum Color { Red }");
        Assert.Equal(Codes.BadAnnotation, ex.Code);
        Assert.Equal(0, ex.Span.Start);
    }

    [Fact]
    public void TrailingReturnNamesTheFunction()
    {
        var ex = Parse("func Foo() -> int { return 1; }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("'Foo'", ex.Message);
        Assert.Contains("before 'func'", ex.Message);
    }

    /// <summary>
    /// The trailing-return-type mistake gets the same targeted message inside a class as it does
    /// for a free function - not the generic "expected '{'" a bare unhandled '->' would otherwise
    /// produce.
    /// </summary>
    [Fact]
    public void TrailingReturnOnMethodNames()
    {
        var ex = Parse("class C { func Foo() -> int { return 1; } }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("'Foo'", ex.Message);
        Assert.Contains("before 'func'", ex.Message);
    }

    [Fact]
    public void TrailingReturnOnOperatorNames()
    {
        var ex = Parse("class C { operator func +(C other) -> C { return self; } }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("'+'", ex.Message);
        Assert.Contains("after 'operator'", ex.Message);
    }

    [Fact]
    public void LeadingReturnTypeOnOperatorParses()
    {
        var prog = SingleFileCompile.Parse("class C { operator C func +(C other) { return self; } }");
        var cls = Assert.IsType<ClassDecl>(prog.Items[0]);
        var op = Assert.IsType<OperatorDecl>(cls.Members[0]);
        Assert.Equal("+", op.Op);
        Assert.Equal("C", op.ReturnType?.ToSpecString());
    }

    [Fact]
    public void TrailingReturnOnExternNames()
    {
        var ex = Parse("@extern func F() -> int;");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("'F'", ex.Message);
        Assert.Contains("before 'func'", ex.Message);
    }

    [Fact]
    public void LeadingReturnTypeOnExternParses()
    {
        var prog = SingleFileCompile.Parse("@extern int func F();");
        var ext = Assert.IsType<ExternFuncDecl>(prog.Items[0]);
        Assert.Equal("int", ext.ReturnType?.ToSpecString());
    }

    /// <summary>
    /// An operator whose return type is itself a function-pointer type is not mistaken for the
    /// no-return-type form - 'func(' after 'operator' is a type, 'func' followed by a symbol is the
    /// declaration keyword.
    /// </summary>
    [Fact]
    public void FuncPtrOperatorReturnParses()
    {
        var prog = SingleFileCompile.Parse("class C { operator func(int) -> int func +(C other) { return null; } }");
        var cls = Assert.IsType<ClassDecl>(prog.Items[0]);
        var op = Assert.IsType<OperatorDecl>(cls.Members[0]);
        Assert.Equal("+", op.Op);
        Assert.Equal("func(int)->int", op.ReturnType?.ToSpecString());
    }

    [Fact]
    public void AssignmentInForInitStillParses()
    {
        var prog = SingleFileCompile.Parse("func F() { let int i = 0; for (i = 0; i < 5; i++) { } }");
        Assert.IsType<FuncDecl>(prog.Items[0]);
    }

    [Theory]
    [InlineData("func F() { for (let int i = 0; i < 5; i++) { } }")]
    [InlineData("func F() { for (let int i = 5; i > 0; i--) { } }")]
    public void PostfixStepStillParses(string src)
    {
        Assert.IsType<FuncDecl>(SingleFileCompile.Parse(src).Items[0]);
    }
}
