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

    /// <summary>
    /// 'public' on a free function changes nothing - a free function is already visible to every
    /// importer - so it is rejected the way 'public class C' already was. Accepting it was worse
    /// than redundant: it let a file mark some free functions 'public' and not others, which reads
    /// as if the bare ones were restricted.
    /// </summary>
    [Theory]
    [InlineData("public int func F() { return 1; }")]
    [InlineData("public throws int func F() { throw; }")]
    [InlineData("public T func F[T](T x) { return x; }")]
    [InlineData("realm kernel { public int func F() { return 1; } entry func Main() { } }")]
    [InlineData("realm kernel { entry func Main() { } background process P { " +
                "public int func F() { return 1; } thread T { entry func R() { } } } }")]
    public void PublicOnAFreeFunctionIsRejected(string src)
    {
        var ex = Parse(src);
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("'public' has no meaning on a free function", ex.Message);
    }

    /// <summary>
    /// The two spellings that still mean something there, and the one member position where
    /// 'public' is the modifier that does the work - so the rule above cannot have reached it.
    /// </summary>
    [Theory]
    [InlineData("int func F() { return 1; }")]
    [InlineData("private int func F() { return 1; }")]
    [InlineData("class C { public int v; public int func F() { return self.v; } }")]
    [InlineData("module M { public static int func F() { return 1; } }")]
    public void TheSpellingsThatStillMeanSomethingAreAccepted(string src)
    {
        SingleFileCompile.Parse(src);   // throws ParseException on failure
    }

    [Fact]
    public void ProcessColonWithoutModeIsRejected()
    {
        var ex = Parse("realm userspace { process App : sideways { } }");
        Assert.Equal(Codes.BadDeclHeader, ex.Code);
        Assert.Contains("'foreground' or 'background'", ex.Message);
    }

    [Fact]
    public void DuplicateProcessModeIsRejected()
    {
        var ex = Parse("realm userspace { foreground process App : background { } }");
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
        var ex = Parse("realm userspace { process App { thread T { entry func Run() { } } } }");
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
        var ex = Parse("realm userspace { TicTacToe { thread T { entry func Run() { } } } }");
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
    [InlineData("realm kernel { realm userspace { } }")]
    [InlineData("class A { class B { } }")]
    [InlineData("class A { realm kernel { } }")]
    [InlineData("realm userspace { foreground process P { thread T { thread U { entry func R() { } } } } }")]
    public void NestingViolationsCarryTheirCode(string src)
    {
        Assert.Equal(Codes.InvalidNesting, Parse(src).Code);
    }

    [Theory]
    [InlineData("kernel { entry func Main() { } }")]
    [InlineData("realm kernel { entry func Main() { } } kernel { }")]
    public void BareKernelAsksForTheRealmKeyword(string src)
    {
        var ex = Parse(src);
        Assert.Equal(Codes.MissingRealmKeyword, ex.Code);
        Assert.Contains(ex.Hints, h => h.Contains("realm kernel"));
    }

    [Theory]
    [InlineData("realm potato { }")]
    [InlineData("realm { }")]
    [InlineData("realm user { }")]
    public void UnknownRealmNamesAreRejected(string src)
    {
        Assert.Equal(Codes.UnknownRealm, Parse(src).Code);
    }

    /// <summary>
    /// 'userspace' is close enough to 'user' that the old spelling should be suggested, not just
    /// rejected - this is the first place Suggest is wired into a realm diagnostic.
    /// </summary>
    [Fact]
    public void ANearMissRealmNameSuggestsTheRealOne()
    {
        var ex = Parse("realm userspac { }");
        Assert.Equal(Codes.UnknownRealm, ex.Code);
        Assert.Contains(ex.Hints, h => h.Contains("userspace"));
    }

    /// <summary>
    /// The point of introducing 'realm': three of the most ordinary identifiers in systems code
    /// stopped being reserved words, in every position a name can appear.
    /// </summary>
    [Theory]
    [InlineData("realm kernel { entry func Main() { let int user = 1; } }")]
    [InlineData("realm kernel { entry func Main() { let int process = 1; } }")]
    [InlineData("realm kernel { entry func Main() { let int thread = 1; } }")]
    [InlineData("class C { int user; int process; int thread; }")]
    [InlineData("void func F(int user, int process, int thread) { }")]
    [InlineData("class C { public void func user() { } }")]
    public void ContextualKeywordsParseAsIdentifiers(string src)
    {
        SingleFileCompile.Parse(src); // must not throw
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


    /// <summary>
    /// 'for (x in xs)' is the range loop spelled the way C# and Python spell it. The C-style reading
    /// then failed on the 'in' with "expected ';'", naming a token the author was never reaching for.
    /// The 'let' variant is the same habit arriving from a language that declares the binding.
    /// </summary>
    [Theory]
    [InlineData("func F() { for (v in xs) { } }")]
    [InlineData("func F() { for (let v in xs) { } }")]
    public void ParenthesisedForInNamesTheRealForm(string src)
    {
        var ex = Parse(src);
        Assert.Contains("without parentheses", ex.Message);
        Assert.Contains("for x in xs", string.Join(" ", ex.Hints ?? []));
    }

    /// <summary>
    /// The loops that are spelled correctly must still parse. The check keys on 'in' right after the
    /// paren, so an ordinary C-style header has to be untouched by it.
    /// </summary>
    [Theory]
    [InlineData("func F() { for v in xs { } }")]
    [InlineData("func F() { for (let int i = 0; i < 2; i = i + 1) { } }")]
    [InlineData("func F() { for (i = 0; i < 2; i = i + 1) { } }")]
    [InlineData("func F() { for (;;) { } }")]
    public void TheLoopFormsThatAreSpelledRightStillParse(string src) =>
        Assert.NotEmpty(SingleFileCompile.Parse(src).Items);
}
