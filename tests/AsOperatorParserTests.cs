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
    /// <summary>
    /// 'operator Target func as(Source s)' parses as an OperatorDecl with Op "as", one
    /// parameter, and the declared return type.
    /// </summary>
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

    /// <summary>
    /// A user-defined class parameter type parses the same way as a primitive one.
    /// </summary>
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

    /// <summary>
    /// The return type is optional in the grammar - it's the resolver, not the parser, that
    /// requires (and defaults) it to the owner class.
    /// </summary>
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

    /// <summary>
    /// A class may declare several 'as' operators, one per source type - the parser places no
    /// limit on how many; that's a semantic (duplicate-parameter-type) concern, not a syntactic
    /// one.
    /// </summary>
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

    /// <summary>
    /// 'as' is a reserved keyword, so it could never be parsed as a method named "as" - this
    /// only compiles because ParseOperatorSymbol special-cases TK.As.
    /// </summary>
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
