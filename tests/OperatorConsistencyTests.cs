namespace Appa.Tests;

using Appa;

/// <summary>
/// Coverage for the operator-consistency rules: '!=' derives from '==' (and vice versa) by
/// negation when only one of the pair is declared, so a class never silently gets reference
/// identity for one spelling of equality while the other uses its declared value comparison;
/// and comparison operators ('==', '!=', '&lt;', '&gt;', '&lt;=', '&gt;=') always return bool -
/// defaulted when omitted, rejected when explicitly anything else.
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
    /// A class that declares '==' but not '!=' gets '!=' as the negation of its own '==' - a
    /// direct call to the declared operator wrapped in '!' - never the old silent fallback to
    /// reference identity.
    /// </summary>
    [Fact]
    public void NotEqDerivesFromDeclaredEq()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Box {
                public int v;
                public operator bool func ==(Box other) { return self.v == other.v; }
            }
            kernel { entry func Main() {
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

    /// <summary>
    /// Symmetrically, a class that declares '!=' but not '==' gets '==' as the negation of its
    /// declared '!='.
    /// </summary>
    [Fact]
    public void EqDerivesFromDeclaredNotEq()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Box {
                public int v;
                public operator bool func !=(Box other) { return self.v != other.v; }
            }
            kernel { entry func Main() {
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

    /// <summary>
    /// When both are declared, each dispatches to its own declaration directly - no negation
    /// wrapper on either.
    /// </summary>
    [Fact]
    public void DeclaringBothUsesEachDirectly()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Box {
                public int v;
                public operator bool func ==(Box other) { return self.v == other.v; }
                public operator bool func !=(Box other) { return self.v != other.v; }
            }
            kernel { entry func Main() {
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

    /// <summary>
    /// A class with neither declared keeps the existing reference-identity comparison for both -
    /// derivation only kicks in when there's a declared operator to derive from.
    /// </summary>
    [Fact]
    public void NoDeclarationKeepsReferenceIdentity()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Box { public int v; }
            kernel { entry func Main() {
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

    /// <summary>
    /// The derived operator type-checks its argument against the declared operator's parameter,
    /// same as a direct call would.
    /// </summary>
    [Fact]
    public void DerivedNotEqChecksArgumentType()
    {
        AssertError(Codes.ArgTypeMismatch, """
            class Box {
                public int v;
                public operator bool func ==(Box other) { return self.v == other.v; }
            }
            class Other { public int v; }
            kernel { entry func Main() {
                let Box a = new Box();
                let Other o = new Other();
                let bool ne = a != o;
            } }
            """);
    }

    #endregion

    #region Comparison operators return bool

    /// <summary>
    /// A comparison operator with an explicit non-bool return type is rejected - comparisons
    /// produce truth values, and the '=='/'!=' derivation is only sound over bool.
    /// </summary>
    [Theory]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("<=")]
    [InlineData(">=")]
    public void ComparisonOperatorWithNonBoolReturnIsRejected(string op)
    {
        AssertError(Codes.TypeMismatch, $$"""
            class Box {
                int v;
                public operator int func {{op}}(Box other) { return 1; }
            }
            """);
    }

    /// <summary>
    /// A comparison operator with no return type defaults to bool, not to the owner class the
    /// way value-producing operators do.
    /// </summary>
    [Fact]
    public void ComparisonOperatorReturnTypeDefaultsToBool()
    {
        AssertClean("""
            class Box {
                public int v;
                public operator func <(Box other) { return self.v < other.v; }
            }
            kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                let bool lt = a < b;
            } }
            """);
    }

    #endregion

    #region Unary and postfix operator overloading

    /// <summary>
    /// '!', '~', and unary '-' dispatch to a class's 0-param operator overload, same as binary
    /// operators dispatch on the left operand's class.
    /// </summary>
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
            kernel { entry func Main() {
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

    /// <summary>
    /// A class may declare unary '-' (0 params) and binary '-' (1 param) side by side; each use
    /// site picks by arity.
    /// </summary>
    [Fact]
    public void UnaryAndBinaryMinusCoexist()
    {
        AssertClean("""
            class Box {
                public int v;
                public operator Box func -() { return new Box(); }
                public operator Box func -(Box other) { return new Box(); }
            }
            kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                let Box neg = -a;
                let Box diff = a - b;
            } }
            """);
    }

    /// <summary>
    /// '++'/'--' overloads are 0-param mutators of self.
    /// </summary>
    [Fact]
    public void PostfixIncrementOverloadDispatches()
    {
        AssertClean("""
            class Counter {
                public int v;
                public operator func ++() { self.v = self.v + 1; }
                public operator func --() { self.v = self.v - 1; }
            }
            kernel { entry func Main() {
                let Counter c = new Counter();
                c++;
                c--;
            } }
            """);
    }

    /// <summary>
    /// '!' must return bool; '++'/'--' mutate in place and must return void.
    /// </summary>
    [Theory]
    [InlineData("operator int func !() { return 1; }")]
    [InlineData("operator int func ++() { return 1; }")]
    [InlineData("operator int func --() { return 1; }")]
    public void UnaryOverloadWrongReturnTypeIsRejected(string decl)
    {
        AssertError(Codes.TypeMismatch, $$"""
            class Box {
                int v;
                {{decl}}
            }
            """);
    }

    /// <summary>
    /// The 0-param operators reject a declared parameter - self is the operand.
    /// </summary>
    [Theory]
    [InlineData("operator func !(Box other) { return true; }")]
    [InlineData("operator Box func ~(Box other) { return other; }")]
    [InlineData("operator func ++(Box other) { }")]
    public void UnaryOverloadWithParameterIsRejected(string decl)
    {
        AssertError(Codes.WrongArgCount, $$"""
            class Box {
                int v;
                {{decl}}
            }
            """);
    }

    /// <summary>
    /// Unary overloads on a class without one still fail with the pre-existing operand-type
    /// errors - dispatch only fires when the overload exists.
    /// </summary>
    [Fact]
    public void UnaryOnClassWithoutOverloadStillRejected()
    {
        AssertError(Codes.TypeMismatch, """
            class Box { public int v; }
            kernel { entry func Main() {
                let Box a = new Box();
                let bool r = !a;
            } }
            """);
    }

    #endregion

    #region Operator visibility

    /// <summary>
    /// Operators are private by default like every other member: using one from outside its
    /// declaring class without 'public' is a PrivateMember error.
    /// </summary>
    [Theory]
    [InlineData("let Box c = a + b;")]
    [InlineData("let bool e = a == b;")]
    [InlineData("let bool n = a != b;")] // derived from the private '==' - equally private
    public void PrivateOperatorIsRejectedOutsideItsClass(string use)
    {
        AssertError(Codes.PrivateMember, $$"""
            class Box {
                public int v;
                operator Box func +(Box other) { return self; }
                operator bool func ==(Box other) { return self.v == other.v; }
            }
            kernel { entry func Main() {
                let Box a = new Box();
                let Box b = new Box();
                {{use}}
            } }
            """);
    }

    /// <summary>
    /// A private '[]'/'[]=' pair is equally inaccessible from outside.
    /// </summary>
    [Fact]
    public void PrivateIndexOperatorIsRejectedOutsideItsClass()
    {
        AssertError(Codes.PrivateMember, """
            class Box {
                public int v;
                operator int func [](int i) { return self.v; }
            }
            kernel { entry func Main() {
                let Box b = new Box();
                let int x = b[0];
            } }
            """);
    }

    /// <summary>
    /// A private 'as' conversion cannot be invoked from outside its declaring class.
    /// </summary>
    [Fact]
    public void PrivateAsOperatorIsRejectedOutsideItsClass()
    {
        AssertError(Codes.PrivateMember, """
            class Wrapper {
                int v;
                operator Wrapper func as(int i) { return new Wrapper(); }
            }
            kernel { entry func Main() {
                let Wrapper w = 5 as Wrapper;
            } }
            """);
    }

    /// <summary>
    /// A private operator is freely usable from inside its own class - private means private to
    /// the class, exactly as it does for fields and methods.
    /// </summary>
    [Fact]
    public void PrivateOperatorIsUsableInsideItsClass()
    {
        AssertClean("""
            class Box {
                public int v;
                operator Box func +(Box other) { return self; }
                public Box func Twice() { return self + self; }
            }
            kernel { entry func Main() {
                let Box a = new Box();
                let Box b = a.Twice();
            } }
            """);
    }

    /// <summary>
    /// 'static' has no meaning on an operator declaration - operators define their own
    /// self/static shape ('as' is static, everything else is an instance operator).
    /// </summary>
    [Fact]
    public void StaticModifierOnOperatorIsRejected()
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
