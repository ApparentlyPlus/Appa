namespace Appa.Tests;

using Appa;

/// <summary>
/// Parser coverage for the 'as' conversion operator: 'operator Target func as(Source s)'.
/// 'as' is always a one-parameter static factory (converting its parameter to self); arity
/// itself is a semantic concern (see AsOperatorSemanticTests), but this covers that the 'as'
/// keyword is accepted at all as an operator symbol, and that its declaration shape parses like
/// any other operator's.
/// </summary>
public class AsOperatorParserTests
{
    [Fact]
    public void AsOperatorParsesWithParameterAndReturnType()
    {
        var prog = SingleFileCompile.Parse("""
            class Wrapper {
                int v;
                operator Wrapper func as(int i) { return self; }
            }
            """);
        var cls = Assert.IsType<ClassDecl>(prog.Items[0]);
        var op = Assert.IsType<OperatorDecl>(cls.Members[1]);
        Assert.Equal("as", op.Op);
        Assert.Single(op.Params);
        Assert.Equal("int", op.Params[0].Type.ToSpecString());
        Assert.Equal("Wrapper", op.ReturnType?.ToSpecString());
    }

    [Fact]
    public void AsOperatorAcceptsClassParameterType()
    {
        var prog = SingleFileCompile.Parse("""
            class Box { int v; }
            class Wrapper {
                Box b;
                operator Wrapper func as(Box b) { return self; }
            }
            """);
        var cls = Assert.IsType<ClassDecl>(prog.Items[1]);
        var op = Assert.IsType<OperatorDecl>(cls.Members[1]);
        Assert.Equal("as", op.Op);
        Assert.Equal("Box", op.Params[0].Type.ToSpecString());
    }

    [Fact]
    public void AsOperatorReturnTypeIsOptionalInGrammar()
    {
        var prog = SingleFileCompile.Parse("""
            class Wrapper {
                int v;
                operator func as(int i) { return self; }
            }
            """);
        var cls = Assert.IsType<ClassDecl>(prog.Items[0]);
        var op = Assert.IsType<OperatorDecl>(cls.Members[1]);
        Assert.Equal("as", op.Op);
        Assert.Null(op.ReturnType);
    }

    [Fact]
    public void MultipleAsOperatorsParseAsSeparateMembers()
    {
        var prog = SingleFileCompile.Parse("""
            class Wrapper {
                int v;
                operator Wrapper func as(int i) { return self; }
                operator Wrapper func as(char c) { return self; }
            }
            """);
        var cls = Assert.IsType<ClassDecl>(prog.Items[0]);
        var op1 = Assert.IsType<OperatorDecl>(cls.Members[1]);
        var op2 = Assert.IsType<OperatorDecl>(cls.Members[2]);
        Assert.Equal("int", op1.Params[0].Type.ToSpecString());
        Assert.Equal("char", op2.Params[0].Type.ToSpecString());
    }

    [Fact]
    public void AsIsRecognizedAsAnOperatorKeywordNotAnIdentifier()
    {
        var prog = SingleFileCompile.Parse("""
            class Wrapper {
                int v;
                operator Wrapper func as(int i) { return self; }
            }
            """);
        var cls = Assert.IsType<ClassDecl>(prog.Items[0]);
        Assert.Equal(2, cls.Members.Length);
    }
}
