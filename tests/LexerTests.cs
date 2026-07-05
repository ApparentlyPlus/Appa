namespace Appa.Tests;

using Appa;

/// <summary>
/// Inline string-literal coverage of the lexer: every token kind reachable from a
/// minimal source snippet, plus the interpolation/escape edge cases.
/// </summary>
public class LexerTests
{
    /// <summary>
    /// Tokenizing always ends with a single EOF token, even for empty input.
    /// </summary>
    [Fact]
    public void AlwaysEndsWithEof()
    {
        var tokens = SingleFileCompile.Tokenize("let x = 1;");
        Assert.Equal(TK.EOF, tokens[^1].Kind);
    }

    /// <summary>
    /// Line and block comments are skipped entirely and produce no tokens.
    /// </summary>
    [Theory]
    [InlineData("// a line comment\nlet")]
    [InlineData("/* a block\n   comment */ let")]
    public void CommentsAreSkipped(string src)
    {
        var tokens = SingleFileCompile.Tokenize(src);
        Assert.Equal(TK.Let, tokens[0].Kind);
    }

    /// <summary>
    /// Keyword spellings map to their expected token kind.
    /// </summary>
    [Theory]
    [InlineData("import", nameof(TK.Import))]
    [InlineData("kernel", nameof(TK.Kernel))]
    [InlineData("user", nameof(TK.User))]
    [InlineData("class", nameof(TK.Class))]
    [InlineData("module", nameof(TK.Module))]
    [InlineData("func", nameof(TK.Func))]
    [InlineData("entry", nameof(TK.Entry))]
    [InlineData("return", nameof(TK.Return))]
    [InlineData("if", nameof(TK.If))]
    [InlineData("else", nameof(TK.Else))]
    [InlineData("while", nameof(TK.While))]
    [InlineData("for", nameof(TK.For))]
    [InlineData("in", nameof(TK.In))]
    [InlineData("break", nameof(TK.Break))]
    [InlineData("continue", nameof(TK.Continue))]
    [InlineData("switch", nameof(TK.Switch))]
    [InlineData("case", nameof(TK.Case))]
    [InlineData("try", nameof(TK.Try))]
    [InlineData("catch", nameof(TK.Catch))]
    [InlineData("new", nameof(TK.New))]
    [InlineData("let", nameof(TK.Let))]
    [InlineData("null", nameof(TK.Null))]
    [InlineData("unsafe", nameof(TK.Unsafe))]
    [InlineData("throw", nameof(TK.Throw))]
    [InlineData("sizeof", nameof(TK.Sizeof))]
    [InlineData("default", nameof(TK.Default))]
    [InlineData("defer", nameof(TK.Defer))]
    [InlineData("match", nameof(TK.Match))]
    [InlineData("union", nameof(TK.Union))]
    [InlineData("enum", nameof(TK.Enum))]
    [InlineData("bool", nameof(TK.TBool))]
    [InlineData("int", nameof(TK.TInt))]
    [InlineData("char", nameof(TK.TChar))]
    [InlineData("float", nameof(TK.TFloat))]
    [InlineData("double", nameof(TK.TDouble))]
    [InlineData("short", nameof(TK.TShort))]
    [InlineData("void", nameof(TK.TVoid))]
    [InlineData("static", nameof(TK.Static))]
    [InlineData("public", nameof(TK.Public))]
    [InlineData("private", nameof(TK.Private))]
    [InlineData("throws", nameof(TK.Throws))]
    [InlineData("operator", nameof(TK.Operator))]
    [InlineData("as", nameof(TK.As))]
    [InlineData("ref", nameof(TK.Ref))]
    [InlineData("debug", nameof(TK.Debug))]
    [InlineData("panic", nameof(TK.Panic))]
    public void KeywordsMapToTheirTokenKind(string src, string expectedKind)
    {
        var tokens = SingleFileCompile.Tokenize(src);
        Assert.Equal(Enum.Parse<TK>(expectedKind), tokens[0].Kind);
    }

    /// <summary>
    /// The width-explicit primitive family (uint, byte, usize, ...) all lex as TPrim,
    /// carrying their real spelling in the token value.
    /// </summary>
    [Theory]
    [InlineData("int64")]
    [InlineData("uint")]
    [InlineData("uint64")]
    [InlineData("ushort")]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("usize")]
    [InlineData("uintptr")]
    public void WidthExplicitPrimitivesLexAsTPrim(string spelling)
    {
        var tokens = SingleFileCompile.Tokenize(spelling);
        Assert.Equal(TK.TPrim, tokens[0].Kind);
        Assert.Equal(spelling, tokens[0].Value);
    }

    /// <summary>
    /// true/false lex as BoolLit, carrying their spelling in the token value.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void BoolLiteralsLexAsBoolLit(string spelling)
    {
        var tokens = SingleFileCompile.Tokenize(spelling);
        Assert.Equal(TK.BoolLit, tokens[0].Kind);
        Assert.Equal(spelling, tokens[0].Value);
    }

    /// <summary>
    /// A plain identifier that is not a keyword lexes as Ident.
    /// </summary>
    [Fact]
    public void PlainIdentifierLexesAsIdent()
    {
        var tokens = SingleFileCompile.Tokenize("myVariable_1");
        Assert.Equal(TK.Ident, tokens[0].Kind);
        Assert.Equal("myVariable_1", tokens[0].Value);
    }

    /// <summary>
    /// Multi-character operators lex as one token each, not as their constituent
    /// single characters.
    /// </summary>
    [Theory]
    [InlineData("+=", nameof(TK.PlusEq), 2)]
    [InlineData("-=", nameof(TK.MinusEq), 2)]
    [InlineData("*=", nameof(TK.StarEq), 2)]
    [InlineData("/=", nameof(TK.SlashEq), 2)]
    [InlineData("%=", nameof(TK.PercentEq), 2)]
    [InlineData("&=", nameof(TK.AmpEq), 2)]
    [InlineData("|=", nameof(TK.PipeEq), 2)]
    [InlineData("^=", nameof(TK.CaretEq), 2)]
    [InlineData("<<=", nameof(TK.ShlEq), 3)]
    [InlineData(">>=", nameof(TK.ShrEq), 3)]
    [InlineData("==", nameof(TK.EqEq), 2)]
    [InlineData("!=", nameof(TK.NotEq), 2)]
    [InlineData("<=", nameof(TK.LtEq), 2)]
    [InlineData(">=", nameof(TK.GtEq), 2)]
    [InlineData("&&", nameof(TK.And), 2)]
    [InlineData("||", nameof(TK.Or), 2)]
    [InlineData("++", nameof(TK.Inc), 2)]
    [InlineData("--", nameof(TK.Dec), 2)]
    [InlineData("->", nameof(TK.Arrow), 2)]
    [InlineData("<<", nameof(TK.Shl), 2)]
    [InlineData(">>", nameof(TK.Shr), 2)]
    public void MultiCharOperatorsLexAsOneToken(string src, string expectedKind, int length)
    {
        var tokens = SingleFileCompile.Tokenize(src);
        Assert.Equal(Enum.Parse<TK>(expectedKind), tokens[0].Kind);
        Assert.Equal(length, tokens[0].Span.Length);
    }

    /// <summary>
    /// Structural punctuation lexes to its own dedicated token kind.
    /// </summary>
    [Theory]
    [InlineData("(", nameof(TK.LParen))]
    [InlineData(")", nameof(TK.RParen))]
    [InlineData("{", nameof(TK.LBrace))]
    [InlineData("}", nameof(TK.RBrace))]
    [InlineData("[", nameof(TK.LBrack))]
    [InlineData("]", nameof(TK.RBrack))]
    [InlineData(";", nameof(TK.Semi))]
    [InlineData(",", nameof(TK.Comma))]
    [InlineData(":", nameof(TK.Colon))]
    [InlineData(".", nameof(TK.Dot))]
    [InlineData("=", nameof(TK.Eq))]
    public void StructuralPunctuationLexesToItsOwnKind(string src, string expectedKind)
    {
        var tokens = SingleFileCompile.Tokenize(src);
        Assert.Equal(Enum.Parse<TK>(expectedKind), tokens[0].Kind);
    }

    /// <summary>
    /// A single-character operator with no compound form falls through to the Punct
    /// catch-all kind, carrying the raw character as its value.
    /// </summary>
    [Theory]
    [InlineData("~")]
    [InlineData("?")]
    public void UnmatchedSingleCharsLexAsPunct(string src)
    {
        var tokens = SingleFileCompile.Tokenize(src);
        Assert.Equal(TK.Punct, tokens[0].Kind);
        Assert.Equal(src, tokens[0].Value);
    }

    /// <summary>
    /// Decimal, hex, and float literals (with e-notation and suffixes) lex with the
    /// full lexeme preserved verbatim in the token value.
    /// </summary>
    [Theory]
    [InlineData("42", nameof(TK.IntLit), "42")]
    [InlineData("0xFF", nameof(TK.IntLit), "0xFF")]
    [InlineData("42ULL", nameof(TK.IntLit), "42ULL")]
    [InlineData("3.14", nameof(TK.FloatLit), "3.14")]
    [InlineData("1e9", nameof(TK.FloatLit), "1e9")]
    [InlineData("1.5e-3f", nameof(TK.FloatLit), "1.5e-3f")]
    public void NumericLiteralsPreserveFullLexeme(string src, string expectedKind, string value)
    {
        var tokens = SingleFileCompile.Tokenize(src);
        Assert.Equal(Enum.Parse<TK>(expectedKind), tokens[0].Kind);
        Assert.Equal(value, tokens[0].Value);
    }

    /// <summary>
    /// A plain string literal lexes as one StrLit token carrying the quotes verbatim.
    /// </summary>
    [Fact]
    public void StringLiteralLexesWithQuotes()
    {
        var tokens = SingleFileCompile.Tokenize("\"hello\"");
        Assert.Equal(TK.StrLit, tokens[0].Kind);
        Assert.Equal("\"hello\"", tokens[0].Value);
    }

    /// <summary>
    /// A recognized escape sequence inside a string literal does not throw.
    /// </summary>
    [Theory]
    [InlineData("\"a\\nb\"")]
    [InlineData("\"tab\\ttab\"")]
    [InlineData("\"quote\\\"quote\"")]
    public void RecognizedStringEscapesAreAccepted(string src)
    {
        var tokens = SingleFileCompile.Tokenize(src);
        Assert.Equal(TK.StrLit, tokens[0].Kind);
    }

    /// <summary>
    /// An unrecognized escape sequence in a string literal is a lex-time error.
    /// </summary>
    [Fact]
    public void UnrecognizedStringEscapeThrows()
    {
        Assert.Throws<ParseException>(() => SingleFileCompile.Tokenize("\"bad\\qescape\""));
    }

    /// <summary>
    /// An unterminated string literal is a lex-time error.
    /// </summary>
    [Fact]
    public void UnterminatedStringThrows()
    {
        Assert.Throws<ParseException>(() => SingleFileCompile.Tokenize("\"never closed"));
    }

    /// <summary>
    /// A character literal decodes to its single Unicode codepoint value.
    /// </summary>
    [Fact]
    public void CharLiteralDecodesToCodepoint()
    {
        var tokens = SingleFileCompile.Tokenize("'a'");
        Assert.Equal(TK.CharLit, tokens[0].Kind);
        Assert.Equal("97", tokens[0].Value);
    }

    /// <summary>
    /// A char literal holding more than one character is a lex-time error.
    /// </summary>
    [Fact]
    public void MultiCharLiteralThrows()
    {
        Assert.Throws<ParseException>(() => SingleFileCompile.Tokenize("'ab'"));
    }

    /// <summary>
    /// An interpolated string with an embedded expression lexes into a
    /// InterpStrStart, string segment, brace-delimited expression tokens, then
    /// InterpStrEnd - not a single opaque token.
    /// </summary>
    [Fact]
    public void InterpolatedStringSplitsIntoSegmentsAndExprTokens()
    {
        var tokens = SingleFileCompile.Tokenize("$\"count={n}\"");
        Assert.Equal(TK.InterpStrStart, tokens[0].Kind);
        Assert.Equal(TK.StrLit, tokens[1].Kind);
        Assert.Equal("\"count=\"", tokens[1].Value);
        Assert.Equal(TK.Punct, tokens[2].Kind);
        Assert.Equal("{", tokens[2].Value);
        Assert.Equal(TK.Ident, tokens[3].Kind);
        Assert.Equal("n", tokens[3].Value);
        Assert.Equal(TK.Punct, tokens[4].Kind);
        Assert.Equal("}", tokens[4].Value);
        Assert.Equal(TK.InterpStrEnd, tokens[5].Kind);
    }

    /// <summary>
    /// Doubled braces inside an interpolated string are a literal-brace escape and
    /// collapse to a single brace in the emitted string segment, rather than opening
    /// an expression hole.
    /// </summary>
    [Fact]
    public void DoubledBracesInInterpolationAreLiteral()
    {
        var tokens = SingleFileCompile.Tokenize("$\"{{literal}}\"");
        Assert.Equal(TK.InterpStrStart, tokens[0].Kind);
        Assert.Equal(TK.StrLit, tokens[1].Kind);
        Assert.Equal("\"{literal}\"", tokens[1].Value);
        Assert.Equal(TK.InterpStrEnd, tokens[2].Kind);
    }

    /// <summary>
    /// A backslash escape inside an interpolated string's literal segment is validated
    /// but kept raw in the token value, same as a plain string literal - decoding is a
    /// later stage's job, not the lexer's.
    /// </summary>
    [Fact]
    public void EscapeInsideInterpolationLiteralSegmentIsKeptRaw()
    {
        var tokens = SingleFileCompile.Tokenize("$\"line1\\nline2\"");
        Assert.Equal(TK.StrLit, tokens[1].Kind);
        Assert.Equal("\"line1\\nline2\"", tokens[1].Value);
    }

    /// <summary>
    /// An unrecognized escape inside an interpolated string's literal segment is a
    /// lex-time error, same as a plain string literal.
    /// </summary>
    [Fact]
    public void UnrecognizedEscapeInsideInterpolationThrows()
    {
        Assert.Throws<ParseException>(() => SingleFileCompile.Tokenize("$\"bad\\qescape\""));
    }

    /// <summary>
    /// @intrinsic/@preamble annotations carry their parenthesized argument in the
    /// token value; @extern/@environment/@keep carry no argument.
    /// </summary>
    [Theory]
    [InlineData("@intrinsic(arc_retain)", nameof(TK.AtIntrinsic), "arc_retain")]
    [InlineData("@preamble(kernel)", nameof(TK.AtPreamble), "kernel")]
    [InlineData("@extern", nameof(TK.AtExtern), "@extern")]
    [InlineData("@environment", nameof(TK.AtEnvironment), "@environment")]
    [InlineData("@keep", nameof(TK.AtKeep), "@keep")]
    public void AnnotationsLexWithTheirArgument(string src, string expectedKind, string value)
    {
        var tokens = SingleFileCompile.Tokenize(src);
        Assert.Equal(Enum.Parse<TK>(expectedKind), tokens[0].Kind);
        Assert.Equal(value, tokens[0].Value);
    }

    /// <summary>
    /// An unrecognized @ annotation is a lex-time error.
    /// </summary>
    [Fact]
    public void UnknownAnnotationThrows()
    {
        Assert.Throws<ParseException>(() => SingleFileCompile.Tokenize("@bogus"));
    }

    /// <summary>
    /// A native { ... } block is captured verbatim as one NativeContent token; braces
    /// inside a string or comment within the block do not affect the balance count.
    /// </summary>
    [Fact]
    public void NativeBlockCapturesBodyVerbatim()
    {
        var tokens = SingleFileCompile.Tokenize("native { int x = 1; /* { */ char c = '{'; }");
        Assert.Equal(TK.NativeContent, tokens[0].Kind);
        Assert.Contains("int x = 1;", tokens[0].Value);
    }

    /// <summary>
    /// native type Name { ... } packs the type name and body into one NativeTypeDecl
    /// token, separated by the unit-separator sentinel.
    /// </summary>
    [Fact]
    public void NativeTypeDeclPacksNameAndBody()
    {
        var tokens = SingleFileCompile.Tokenize("native type Foo { int x; }");
        Assert.Equal(TK.NativeTypeDecl, tokens[0].Kind);
        Assert.StartsWith("Foo\x1F", tokens[0].Value);
    }

    /// <summary>
    /// fields { ... } is captured verbatim as one Fields token, same shape as a
    /// native block.
    /// </summary>
    [Fact]
    public void FieldsBlockCapturesBodyVerbatim()
    {
        var tokens = SingleFileCompile.Tokenize("fields { int x; }");
        Assert.Equal(TK.Fields, tokens[0].Kind);
        Assert.Contains("int x;", tokens[0].Value);
    }
}
