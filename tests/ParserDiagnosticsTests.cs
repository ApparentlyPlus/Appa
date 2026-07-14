namespace Appa.Tests;

using Appa;

/// <summary>
/// Parse-time error coverage: targeted messages and dedicated codes for the
/// mistakes users actually make, instead of a generic "expected X, found Y".
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

    /// <summary>
    /// Plain and compound assignments are valid for-loop steps, mirroring the init clause.
    /// </summary>
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

    /// <summary>
    /// A variable declaration makes no sense in the for-loop step and is rejected.
    /// </summary>
    [Fact]
    public void LetInForStepIsRejected()
    {
        var ex = Parse("func F() { for (let int i = 0; i < 5; let int j = 0) { } }");
        Assert.Contains("cannot declare a variable in the for-loop step", ex.Message);
    }

    /// <summary>
    /// A single '=' in a condition suggests '==' instead of a generic paren error.
    /// </summary>
    [Theory]
    [InlineData("func F() { let int x = 1; if (x = 3) { } }")]
    [InlineData("func F() { let int x = 1; while (x = 3) { } }")]
    [InlineData("func F() { for (let int i = 0; i = 5; i++) { } }")]
    public void AssignmentInConditionSuggestsEquality(string src)
    {
        var ex = Parse(src);
        Assert.Equal(Codes.AssignInExpr, ex.Code);
        Assert.Contains("did you mean '=='?", ex.Message);
    }

    /// <summary>
    /// Expected-token messages spell the token like source code, not the enum name.
    /// </summary>
    [Theory]
    [InlineData("func F() { let int x = 5 }", "expected ';'")]
    [InlineData("func F() { G(1, 2; }", "expected ')'")]
    [InlineData("func F(int x", "expected ')'")]
    [InlineData("class C int x; }", "expected '{'")]
    public void ExpectedTokenMessagesAreHumanReadable(string src, string expected)
    {
        Assert.Contains(expected, Parse(src).Message);
    }

    /// <summary>
    /// Hitting the end of the token stream says "end of file", not an empty quote.
    /// </summary>
    [Fact]
    public void EofReadsAsEndOfFile()
    {
        Assert.Contains("end of file", Parse("func F() {").Message);
    }

    /// <summary>
    /// A colon after a process name must be followed by a mode keyword; a stray
    /// identifier no longer falls through to a confusing '{' error.
    /// </summary>
    [Fact]
    public void ProcessColonWithoutModeIsRejected()
    {
        var ex = Parse("user { process App : sideways { } }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("'foreground' or 'background'", ex.Message);
    }

    /// <summary>
    /// Spelling the process mode both before the keyword and after the colon is rejected.
    /// </summary>
    [Fact]
    public void ProcessModeSpecifiedTwiceIsRejected()
    {
        var ex = Parse("user { foreground process App : background { } }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("mode specified twice", ex.Message);
    }

    /// <summary>
    /// A process declaration without a foreground/background mode -- in either the leading or
    /// the trailing colon spelling -- is rejected outright rather than silently defaulting, since
    /// the mode is a real semantic choice (TTY/keyboard focus, scheduling visibility).
    /// </summary>
    [Fact]
    public void ProcessWithoutModeIsRejected()
    {
        var ex = Parse("user { process App { thread T { entry func Run() { } } } }");
        Assert.Equal(Codes.MissingProcessMode, ex.Code);
        Assert.Contains("missing a foreground/background mode", ex.Message);
        Assert.Contains("foreground process App", ex.Message);
        Assert.Contains("background process App", ex.Message);
    }

    /// <summary>
    /// A bare identifier immediately followed by '{' where 'func' was expected hints that a
    /// 'process' declaration was likely intended, instead of the bare "expected 'func'" message.
    /// </summary>
    [Fact]
    public void MissingProcessKeywordHintsAtFreeFuncError()
    {
        var ex = Parse("user { TicTacToe { thread T { entry func Run() { } } } }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("expected 'func'", ex.Message);
        Assert.Contains("forget 'process'", ex.Message);
        Assert.Contains("TicTacToe", ex.Message);
    }

    /// <summary>
    /// Trailing commas in declaration lists carry the trailing-comma code.
    /// </summary>
    [Theory]
    [InlineData("enum Color { Red, Green, }")]
    [InlineData("union U { A, B, }")]
    [InlineData("union U { A(int x,) }")]
    public void TrailingCommasCarryTheirCode(string src)
    {
        Assert.Equal(Codes.TrailingComma, Parse(src).Code);
    }

    /// <summary>
    /// A statement that starts with a type followed by a name hints at the missing 'let'.
    /// </summary>
    [Fact]
    public void TypeThenNameHintsMissingLet()
    {
        var ex = Parse("func F() { MyType x = 1; }");
        Assert.Equal(Codes.MissingLet, ex.Code);
        Assert.Contains("missing 'let'", ex.Message);
    }

    /// <summary>
    /// Nesting violations carry the invalid-nesting code.
    /// </summary>
    [Theory]
    [InlineData("kernel { user { } }")]
    [InlineData("class A { class B { } }")]
    [InlineData("class A { kernel { } }")]
    [InlineData("user { foreground process P { thread T { thread U { entry func R() { } } } } }")]
    public void NestingViolationsCarryTheirCode(string src)
    {
        Assert.Equal(Codes.InvalidNesting, Parse(src).Code);
    }

    /// <summary>
    /// An annotation on a declaration that cannot use it points at the annotation itself.
    /// </summary>
    [Fact]
    public void RejectedAnnotationPointsAtTheAnnotation()
    {
        var ex = Parse("@keep\nenum Color { Red }");
        Assert.Equal(Codes.BadAnnotation, ex.Code);
        Assert.Equal(0, ex.Span.Start);
    }

    /// <summary>
    /// A trailing '-> type' on a function declaration names the function and the fix.
    /// </summary>
    [Fact]
    public void TrailingReturnTypeNamesTheFunction()
    {
        var ex = Parse("func Foo() -> int { return 1; }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("'Foo'", ex.Message);
        Assert.Contains("before 'func'", ex.Message);
    }

    /// <summary>
    /// Assignments remain valid in the for-loop init clause; only the step rejects them.
    /// </summary>
    [Fact]
    public void AssignmentInForInitStillParses()
    {
        var prog = SingleFileCompile.Parse("func F() { let int i = 0; for (i = 0; i < 5; i++) { } }");
        Assert.IsType<FuncDecl>(prog.Items[0]);
    }

    /// <summary>
    /// Postfix increment and decrement remain valid for-loop steps.
    /// </summary>
    [Theory]
    [InlineData("func F() { for (let int i = 0; i < 5; i++) { } }")]
    [InlineData("func F() { for (let int i = 5; i > 0; i--) { } }")]
    public void PostfixStepStillParses(string src)
    {
        Assert.IsType<FuncDecl>(SingleFileCompile.Parse(src).Items[0]);
    }
}
