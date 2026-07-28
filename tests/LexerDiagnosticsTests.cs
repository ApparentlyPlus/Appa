namespace Appa.Tests;

using Appa;

/// <summary>
/// Lex-time error coverage: every malformed input class carries its dedicated diagnostic code on
/// the thrown ParseException, not the generic G000/G044.
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

    [Fact]
    public void UnterminatedBlockCommentIsAnError()
    {
        var ex = Lex("let x = 1; /* never closed");
        Assert.Equal(Codes.UnterminatedLiteral, ex.Code);
        Assert.Contains("block comment", ex.Message);
    }

    [Theory]
    [InlineData("/**/let")]
    [InlineData("/* x */let")]
    [InlineData("/* * / ** */let")]
    public void ClosedBlockCommentLexesCleanly(string src)
    {
        Assert.Equal(TK.Let, SingleFileCompile.Tokenize(src)[0].Kind);
    }

    [Fact]
    public void BareHexPrefixIsAnError()
    {
        var ex = Lex("let x = 0x;");
        Assert.Equal(Codes.BadNumber, ex.Code);
    }

    [Theory]
    [InlineData("123abc")]
    [InlineData("0xFFg")]
    [InlineData("1.5fx")]
    [InlineData("42e")]
    public void IdentifierGluedToNumberIsAnError(string src)
    {
        Assert.Equal(Codes.BadNumber, Lex(src).Code);
    }

    [Fact]
    public void DotAfterIntegerIsNotTheNumber()
    {
        var tokens = SingleFileCompile.Tokenize("42.ToString");
        Assert.Equal(TK.IntLit, tokens[0].Kind);
        Assert.Equal(TK.Dot, tokens[1].Kind);
        Assert.Equal(TK.Ident, tokens[2].Kind);
    }

    [Theory]
    [InlineData("@intrinsic")]
    [InlineData("@intrinsic()")]
    [InlineData("@intrinsic(alloc")]
    [InlineData("@preamble")]
    public void MalformedAnnotationArgIsAnError(string src)
    {
        Assert.Equal(Codes.BadAnnotation, Lex(src).Code);
    }

    [Fact]
    public void UnknownAnnotationHasItsOwnCode()
    {
        Assert.Equal(Codes.BadAnnotation, Lex("@bogus").Code);
    }

    [Theory]
    [InlineData("\"never closed")]
    [InlineData("'a")]
    [InlineData("''")]
    [InlineData("'ab'")]
    [InlineData("$\"never closed")]
    [InlineData("$\"open {x\"")]
    [InlineData("native { int x;")]
    public void UnterminatedLiteralsCarryACode(string src)
    {
        Assert.Equal(Codes.UnterminatedLiteral, Lex(src).Code);
    }

    [Theory]
    [InlineData("\"bad\\qescape\"")]
    [InlineData("'\\q'")]
    [InlineData("$\"bad\\qescape\"")]
    public void BadEscapesCarryTheirCode(string src)
    {
        Assert.Equal(Codes.BadEscape, Lex(src).Code);
    }

    [Fact]
    public void SegmentSpanPointsAtItsSegment()
    {
        var tokens = SingleFileCompile.Tokenize("$\"ab{n}\"");
        Assert.Equal(TK.StrLit, tokens[1].Kind);
        Assert.Equal(2, tokens[1].Span.Start);
    }
}
