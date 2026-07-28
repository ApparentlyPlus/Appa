namespace Appa.Tests;

using Appa;

/// <summary>
/// One trivial test confirming the test project can call into Appa in-process.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void EmptySourceTokenizesToEof()
    {
        var tokens = SingleFileCompile.Tokenize("");
        Assert.Single(tokens);
        Assert.Equal(TK.EOF, tokens[0].Kind);
    }
}
