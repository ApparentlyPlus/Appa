namespace Appa.Tests;

using Appa;

/// <summary>
/// Lex-time error coverage: every malformed input class carries its dedicated
/// diagnostic code on the thrown ParseException, not the generic G000/G044.
/// </summary>
public class LexerDiagnosticsTests
{
    /// <summary>
    /// Tokenizes and returns the ParseException the source must produce.
    /// </summary>
    private static ParseException Lex(string src)
    {
        return Assert.Throws<ParseException>(() => SingleFileCompile.Tokenize(src));
    }

    /// <summary>
    /// A block comment that never closes is an error instead of silently swallowing the file.
    /// </summary>
    [Fact]
    public void UnterminatedBlockCommentIsAnError()
    {
        var ex = Lex("let x = 1; /* never closed");
        Assert.Equal(Codes.UnterminatedLiteral, ex.Code);
        Assert.Contains("block comment", ex.Message);
    }

    /// <summary>
    /// A properly closed block comment still lexes cleanly.
    /// </summary>
    [Theory]
    [InlineData("/**/let")]
    [InlineData("/* x */let")]
    [InlineData("/* * / ** */let")]
    public void ClosedBlockCommentLexesCleanly(string src)
    {
        Assert.Equal(TK.Let, SingleFileCompile.Tokenize(src)[0].Kind);
    }

    /// <summary>
    /// A hex prefix with no digits is a malformed number, not an IntLit "0x".
    /// </summary>
    [Fact]
    public void BareHexPrefixIsAnError()
    {
        var ex = Lex("let x = 0x;");
        Assert.Equal(Codes.BadNumber, ex.Code);
    }

    /// <summary>
    /// An identifier glued to a numeric literal is a malformed number, not two tokens.
    /// </summary>
    [Theory]
    [InlineData("123abc")]
    [InlineData("0xFFg")]
    [InlineData("1.5fx")]
    [InlineData("42e")]
    public void IdentifierGluedToNumberIsAnError(string src)
    {
        Assert.Equal(Codes.BadNumber, Lex(src).Code);
    }

    /// <summary>
    /// A dot after an integer that does not start a fraction stays a separate token,
    /// so method calls on literals still lex.
    /// </summary>
    [Fact]
    public void DotAfterIntegerIsNotPartOfTheNumber()
    {
        var tokens = SingleFileCompile.Tokenize("42.ToString");
        Assert.Equal(TK.IntLit, tokens[0].Kind);
        Assert.Equal(TK.Dot, tokens[1].Kind);
        Assert.Equal(TK.Ident, tokens[2].Kind);
    }

    /// <summary>
    /// @intrinsic and @preamble require a parenthesized, non-empty, closed argument.
    /// </summary>
    [Theory]
    [InlineData("@intrinsic")]
    [InlineData("@intrinsic()")]
    [InlineData("@intrinsic(alloc")]
    [InlineData("@preamble")]
    public void MalformedAnnotationArgumentIsAnError(string src)
    {
        Assert.Equal(Codes.BadAnnotation, Lex(src).Code);
    }

    /// <summary>
    /// An unknown annotation name carries the annotation code.
    /// </summary>
    [Fact]
    public void UnknownAnnotationCarriesBadAnnotationCode()
    {
        Assert.Equal(Codes.BadAnnotation, Lex("@bogus").Code);
    }

    /// <summary>
    /// Unterminated literals of every flavor carry the unterminated-literal code.
    /// </summary>
    [Theory]
    [InlineData("\"never closed")]
    [InlineData("'a")]
    [InlineData("''")]
    [InlineData("'ab'")]
    [InlineData("$\"never closed")]
    [InlineData("$\"open {x\"")]
    [InlineData("native { int x;")]
    public void UnterminatedLiteralsCarryTheirCode(string src)
    {
        Assert.Equal(Codes.UnterminatedLiteral, Lex(src).Code);
    }

    /// <summary>
    /// Unrecognized escapes in every string-like context carry the bad-escape code.
    /// </summary>
    [Theory]
    [InlineData("\"bad\\qescape\"")]
    [InlineData("'\\q'")]
    [InlineData("$\"bad\\qescape\"")]
    public void BadEscapesCarryTheirCode(string src)
    {
        Assert.Equal(Codes.BadEscape, Lex(src).Code);
    }

    /// <summary>
    /// A literal segment inside an interpolated string carries its own span,
    /// not the span of the token lexed before it.
    /// </summary>
    [Fact]
    public void InterpolationSegmentSpanPointsAtTheSegment()
    {
        var tokens = SingleFileCompile.Tokenize("$\"ab{n}\"");
        Assert.Equal(TK.StrLit, tokens[1].Kind);
        Assert.Equal(2, tokens[1].Span.Start);
    }
}
