namespace Appa.Tests;

using Appa;

/// <summary>
/// The operator-consistency rules: '!=' derives from '==' by negation when only one is declared, so
/// a class never gets identity for one spelling and value comparison for the other; and every
/// comparison operator returns bool, defaulted when omitted.
/// </summary>
public class OperatorConsistencyTests
{
    private static void AssertClean(string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
    }

    private static void AssertError(string code, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.True(diag.HasErrors, $"expected {code} but no errors were produced");
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == code);
    }

    /// <summary>
    /// Finds the initializer of 'let <paramref name="name"/> = ...' in the entry function.
    /// </summary>
    private static IrExpr EntryDeclInit(IrModule module, string name)
    {
        var entry = module.FreeFunctions.Single(f => f.IsEntry);
        var stmts = Assert.IsType<IrBlock>(entry.Body).Stmts;
        var decl = stmts.OfType<IrDeclVar>().Single(d => d.Name == name);
        Assert.NotNull(decl.Init);
        return decl.Init!;
    }

    #region '!=' / '==' derivation

    /// <summary>
    /// A class that declares '==' but not '!=' gets '!=' as the negation of its own '==' - a direct
    /// call to the declared operator wrapped in '!' - never the old silent fallback to reference
    /// identity.
    /// </summary>
    [Fact]
    public void NotEqDerivesFromDeclaredEq()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Box {
                public int v;
                public operator bool func ==(Box other) { return self.v == other.v; }
            }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                let bool ne = a != b;
            } }
            """);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);

        var init = Assert.IsType<IrUnaryOp>(EntryDeclInit(module!, "ne"));
        Assert.Equal(UnOp.Not, init.Op);
        Assert.IsType<IrStaticCall>(init.Operand);
    }

    [Fact]
    public void EqDerivesFromDeclaredNotEq()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Box {
                public int v;
                public operator bool func !=(Box other) { return self.v != other.v; }
            }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                let bool eq = a == b;
            } }
            """);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);

        var init = Assert.IsType<IrUnaryOp>(EntryDeclInit(module!, "eq"));
        Assert.Equal(UnOp.Not, init.Op);
        Assert.IsType<IrStaticCall>(init.Operand);
    }

    [Fact]
    public void DeclaringBothUsesEachDirectly()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Box {
                public int v;
                public operator bool func ==(Box other) { return self.v == other.v; }
                public operator bool func !=(Box other) { return self.v != other.v; }
            }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                let bool eq = a == b;
                let bool ne = a != b;
            } }
            """);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);

        Assert.IsType<IrStaticCall>(EntryDeclInit(module!, "eq"));
        Assert.IsType<IrStaticCall>(EntryDeclInit(module!, "ne"));
    }

    [Fact]
    public void NoDeclarationKeepsIdentity()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Box { public int v; }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                let bool eq = a == b;
                let bool ne = a != b;
            } }
            """);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);

        Assert.IsType<IrBinOp>(EntryDeclInit(module!, "eq"));
        Assert.IsType<IrBinOp>(EntryDeclInit(module!, "ne"));
    }

    [Fact]
    public void DerivedNotEqChecksArgumentType()
    {
        AssertError(Codes.ArgTypeMismatch, """
            class Box {
                public int v;
                public operator bool func ==(Box other) { return self.v == other.v; }
            }
            class Other { public int v; }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let Other o = new Other();
                let bool ne = a != o;
            } }
            """);
    }

    #endregion

    #region Comparison operators return bool

    [Theory]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("<=")]
    [InlineData(">=")]
    public void ComparisonNonBoolReturnIsRejected(string op)
    {
        AssertError(Codes.TypeMismatch, $$"""
            class Box {
                int v;
                public operator int func {{op}}(Box other) { return 1; }
            }
            """);
    }

    [Fact]
    public void ComparisonReturnDefaultsToBool()
    {
        AssertClean("""
            class Box {
                public int v;
                public operator func <(Box other) { return self.v < other.v; }
            }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                let bool lt = a < b;
            } }
            """);
    }

    #endregion

    #region Unary and postfix operator overloading

    [Theory]
    [InlineData("public operator func !() { return self.v == 0; }", "let bool r = !a;")]
    [InlineData("public operator Box func ~() { return new Box(); }", "let Box r = ~a;")]
    [InlineData("public operator Box func -() { return new Box(); }", "let Box r = -a;")]
    public void UnaryOperatorOverloadDispatches(string decl, string use)
    {
        var (diag, module) = SingleFileCompile.Check($$"""
            class Box {
                public int v;
                {{decl}}
            }
            realm kernel { entry func Main() {
                let Box a = new Box();
                {{use}}
            } }
            """);
        Assert.False(diag.HasErrors, string.Join("; ",
            diag.All.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} {d.Message}")));
        Assert.NotNull(module);
        var entry = module!.FreeFunctions.Single(f => f.IsEntry);
        var stmts = Assert.IsType<IrBlock>(entry.Body).Stmts;
        Assert.IsType<IrStaticCall>(stmts.OfType<IrDeclVar>().Single(d => d.Name == "r").Init);
    }

    [Fact]
    public void UnaryAndBinaryMinusCoexist()
    {
        AssertClean("""
            class Box {
                public int v;
                public operator Box func -() { return new Box(); }
                public operator Box func -(Box other) { return new Box(); }
            }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                let Box neg = -a;
                let Box diff = a - b;
            } }
            """);
    }

    [Fact]
    public void PostfixIncrementDispatches()
    {
        AssertClean("""
            class Counter {
                public int v;
                public operator func ++() { self.v = self.v + 1; }
                public operator func --() { self.v = self.v - 1; }
            }
            realm kernel { entry func Main() {
                let Counter c = new Counter();
                c++;
                c--;
            } }
            """);
    }

    [Theory]
    [InlineData("operator int func !() { return 1; }")]
    [InlineData("operator int func ++() { return 1; }")]
    [InlineData("operator int func --() { return 1; }")]
    public void UnaryOverloadBadReturnIsRejected(string decl)
    {
        AssertError(Codes.TypeMismatch, $$"""
            class Box {
                int v;
                {{decl}}
            }
            """);
    }

    [Theory]
    [InlineData("operator func !(Box other) { return true; }")]
    [InlineData("operator Box func ~(Box other) { return other; }")]
    [InlineData("operator func ++(Box other) { }")]
    public void UnaryOverloadWithParamIsRejected(string decl)
    {
        AssertError(Codes.WrongArgCount, $$"""
            class Box {
                int v;
                {{decl}}
            }
            """);
    }

    [Fact]
    public void UnaryWithoutOverloadIsRejected()
    {
        AssertError(Codes.TypeMismatch, """
            class Box { public int v; }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let bool r = !a;
            } }
            """);
    }

    #endregion

    #region Operator visibility

    [Theory]
    [InlineData("let Box c = a + b;")]
    [InlineData("let bool e = a == b;")]
    [InlineData("let bool n = a != b;")] // derived from the private '==' - equally private
    public void PrivateOperatorIsRejectedOutside(string use)
    {
        AssertError(Codes.PrivateMember, $$"""
            class Box {
                public int v;
                operator Box func +(Box other) { return self; }
                operator bool func ==(Box other) { return self.v == other.v; }
            }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                {{use}}
            } }
            """);
    }

    [Fact]
    public void PrivateIndexIsRejectedOutside()
    {
        AssertError(Codes.PrivateMember, """
            class Box {
                public int v;
                operator int func [](int i) { return self.v; }
            }
            realm kernel { entry func Main() {
                let Box b = new Box();
                let int x = b[0];
            } }
            """);
    }

    [Fact]
    public void PrivateAsIsRejectedOutside()
    {
        AssertError(Codes.PrivateMember, """
            class Wrapper {
                int v;
                operator Wrapper func as(int i) { return new Wrapper(); }
            }
            realm kernel { entry func Main() {
                let Wrapper w = 5 as Wrapper;
            } }
            """);
    }

    [Fact]
    public void PrivateOperatorWorksInside()
    {
        AssertClean("""
            class Box {
                public int v;
                operator Box func +(Box other) { return self; }
                public Box func Twice() { return self + self; }
            }
            realm kernel { entry func Main() {
                let Box a = new Box();
                let Box b = a.Twice();
            } }
            """);
    }

    [Fact]
    public void StaticOnOperatorIsRejected()
    {
        AssertError(Codes.BadDeclHeader, """
            class Box {
                int v;
                static operator Box func +(Box other) { return self; }
            }
            """);
    }

    #endregion
}
