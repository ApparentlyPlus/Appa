namespace Appa.Tests;

using Appa;

/// <summary>
/// Semantic and codegen coverage for the 'as' conversion operator - user-defined explicit
/// conversions, invoked with the same 'value as Target' syntax as built-in casts.
///
/// 'as' has exactly one shape: a static factory declared on the class being converted TO,
/// converting its one parameter to self ('public operator func as(char c) -> String', String's
/// equivalent of a FromChar factory). Only a class can declare it, and it only ever converts
/// INTO itself - never out of itself to a primitive. That direction (class -> primitive) has
/// no 'as' path at all; it's a named method's job instead (e.g. 'int func ToInt()'), since a
/// primitive can never declare a conversion of its own to fall back to.
/// </summary>
public class AsOperatorSemanticTests
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

    #region Good programs

    /// <summary>
    /// A primitive converting to a class via 'as' type-checks and transpiles cleanly - the only
    /// way to express something like String's FromChar factory.
    /// </summary>
    [Fact]
    public void PrimitiveToClassConversionIsClean()
    {
        AssertClean("""
            class Wrapper {
                int v;
                public operator Wrapper func as(char c) { let Wrapper w = new Wrapper(); w.v = c as int; return w; }
            }
            kernel { entry func Main() {
                let Wrapper w = 'x' as Wrapper;
            } }
            """);
    }

    /// <summary>
    /// A class converting to another class via 'as' type-checks and transpiles cleanly. The
    /// conversion is declared on the destination (Inches), not the source (Centimeters) - the
    /// destination is what says whether it knows how to be built from a given source type.
    /// </summary>
    [Fact]
    public void ClassToClassConversionIsClean()
    {
        AssertClean("""
            class Centimeters { public int v; func _init(int v) { self.v = v; } }
            class Inches {
                int v;
                func _init(int v) { self.v = v; }
                public operator Inches func as(Centimeters c) { return new Inches(c.v / 2); }
            }
            kernel { entry func Main() {
                let Centimeters c = new Centimeters(10);
                let Inches i = c as Inches;
            } }
            """);
    }

    /// <summary>
    /// The return type defaults to the owner class exactly like every other operator's does, so
    /// it doesn't need to be written out - 'as' always converts INTO its declaring class, so the
    /// return type is never meaningfully anything else.
    /// </summary>
    [Fact]
    public void ReturnTypeDefaultsToOwnerClass()
    {
        AssertClean("""
            class Wrapper {
                int v;
                public operator func as(int i) { let Wrapper w = new Wrapper(); w.v = i; return w; }
            }
            kernel { entry func Main() {
                let Wrapper w = 5 as Wrapper;
            } }
            """);
    }

    /// <summary>
    /// A class may declare several 'as' overloads distinguished by parameter (source) type, and
    /// each is independently selectable by the cast's source expression's type.
    /// </summary>
    [Fact]
    public void MultipleOverloadsDistinguishedByParameterTypeAreEachSelectable()
    {
        AssertClean("""
            class Box { public int v; }
            class Wrapper {
                int v;
                public operator Wrapper func as(char c) { let Wrapper w = new Wrapper(); w.v = c as int; return w; }
                public operator Wrapper func as(int i) { let Wrapper w = new Wrapper(); w.v = i; return w; }
                public operator Wrapper func as(Box b) { let Wrapper w = new Wrapper(); w.v = b.v; return w; }
            }
            kernel { entry func Main() {
                let Wrapper a = 'x' as Wrapper;
                let Wrapper b = 5 as Wrapper;
                let Wrapper c = new Box() as Wrapper;
            } }
            """);
    }

    /// <summary>
    /// A cast to the source's own type is identity (the existing SameType short-circuit in
    /// CheckCast), so declaring 'public operator func as(Self s) -> Self' is legal even though it can
    /// never fire - it's dead code, not an error.
    /// </summary>
    [Fact]
    public void SelfConversionOperatorIsLegalButUnreachable()
    {
        AssertClean("""
            class Box {
                int v;
                public operator Box func as(Box b) { return b; }
            }
            kernel { entry func Main() {
                let Box a = new Box();
                let Box b = a as Box;
            } }
            """);
    }

    /// <summary>
    /// A generic class's 'as' operator is substituted per instantiation, same as any other
    /// operator or method - Box[int]'s 'as' converts FROM int, independently of what other
    /// instantiations of Box exist.
    /// </summary>
    [Fact]
    public void GenericClassAsOperatorMonomorphizesPerInstantiation()
    {
        AssertClean("""
            class Box[T] {
                T v;
                public operator Box[T] func as(T t) { let Box[T] b = new Box[T](); b.v = t; return b; }
            }
            kernel { entry func Main() {
                let Box[int] bi = 5 as Box[int];
            } }
            """);
    }

    /// <summary>
    /// A class-typed source uses a destination-declared 'as' just like a primitive source does -
    /// there's only one shape now, so there's nothing special about the source being a class.
    /// </summary>
    [Fact]
    public void ClassSourceUsesDestinationDeclaredConversion()
    {
        AssertClean("""
            class Box { public int v; }
            class Wrapper {
                int v;
                public operator Wrapper func as(Box b) { let Wrapper w = new Wrapper(); w.v = b.v; return w; }
            }
            kernel { entry func Main() {
                let Box b = new Box();
                let Wrapper w = b as Wrapper;
            } }
            """);
    }

    /// <summary>
    /// 'as' conversions are not implicitly invoked - a plain assignment across unrelated types
    /// still requires the explicit cast syntax, exactly like every other cast in the language.
    /// </summary>
    [Fact]
    public void ConversionNeverFiresImplicitly()
    {
        AssertError(Codes.TypeMismatch, """
            class Wrapper {
                int v;
                public operator Wrapper func as(int i) { let Wrapper w = new Wrapper(); return w; }
            }
            kernel { entry func Main() {
                let Wrapper w = 5;
            } }
            """);
    }

    #endregion

    #region Error programs

    /// <summary>
    /// 'as' always takes exactly one parameter - the source value being converted. There is no
    /// zero-parameter form; a class can never declare how it converts itself OUT to something
    /// else via 'as', only how another type converts IN to it.
    /// </summary>
    [Fact]
    public void AsOperatorWithZeroParametersIsRejected()
    {
        AssertError(Codes.WrongArgCount, """
            class Box {
                int v;
                public operator int func as() { return self.v; }
            }
            """);
    }

    /// <summary>
    /// 'as' takes exactly one parameter, never two or more.
    /// </summary>
    [Fact]
    public void AsOperatorWithTwoParametersIsRejected()
    {
        AssertError(Codes.WrongArgCount, """
            class Box {
                int v;
                public operator int func as(int a, int b) { return a; }
            }
            """);
    }

    /// <summary>
    /// Two 'as' operators converting from the same source (parameter) type on the same class
    /// collide.
    /// </summary>
    [Fact]
    public void DuplicateAsOverloadForSameSourceIsRejected()
    {
        AssertError(Codes.DuplicateName, """
            class Wrapper {
                int v;
                public operator Wrapper func as(int i) { let Wrapper w = new Wrapper(); return w; }
                public operator Wrapper func as(int i) { let Wrapper w = new Wrapper(); return w; }
            }
            """);
    }

    /// <summary>
    /// Declaring the same non-'as' operator twice on one class is now caught explicitly instead
    /// of silently colliding on the same C name at codegen time.
    /// </summary>
    [Fact]
    public void DuplicateRegularOperatorIsRejected()
    {
        AssertError(Codes.DuplicateName, """
            class Box {
                int v;
                public operator Box func +(Box other) { return self; }
                public operator Box func +(Box other) { return other; }
            }
            """);
    }

    /// <summary>
    /// An explicit return type on 'as' that isn't the owner class is rejected - it would
    /// otherwise let a cast's declared IR type disagree with what the C function actually
    /// returns, since dispatch finds this operator purely by the destination class's name.
    /// </summary>
    [Fact]
    public void WrongExplicitReturnTypeIsRejected()
    {
        AssertError(Codes.TypeMismatch, """
            class Wrapper {
                int v;
                public operator int func as(int i) { return i; }
            }
            """);
    }

    /// <summary>
    /// Without a matching 'as' operator, casting between unrelated class types is still rejected
    /// exactly as it was before this feature existed.
    /// </summary>
    [Fact]
    public void CastWithNoMatchingAsOperatorIsStillRejected()
    {
        AssertError(Codes.InvalidCast, """
            class Box { int v; }
            class Other { int v; }
            kernel { entry func Main() {
                let Box b = new Box();
                let Other o = b as Other;
            } }
            """);
    }

    /// <summary>
    /// A destination class's 'as' overload for the wrong source type does not apply - having an
    /// 'as' that doesn't match the cast's source is exactly as if it had none at all.
    /// </summary>
    [Fact]
    public void AsOnDestinationForAnUnrelatedSourceDoesNotApply()
    {
        AssertError(Codes.InvalidCast, """
            class Box { int v; }
            class Other { int v; }
            class Wrapper {
                int v;
                public operator Wrapper func as(Other o) { let Wrapper w = new Wrapper(); return w; }
            }
            kernel { entry func Main() {
                let Box b = new Box();
                let Wrapper w = b as Wrapper;
            } }
            """);
    }

    /// <summary>
    /// Without a matching 'as', casting a primitive to an unrelated class is still rejected
    /// exactly as it was before this feature existed.
    /// </summary>
    [Fact]
    public void PrimitiveSourceWithNoMatchingAsOperatorIsStillRejected()
    {
        AssertError(Codes.InvalidCast, """
            class Wrapper { int v; }
            kernel { entry func Main() {
                let Wrapper w = 5 as Wrapper;
            } }
            """);
    }

    /// <summary>
    /// A class converting itself to a primitive has no 'as' path at all - not "check the source,
    /// find nothing"; there's structurally nowhere on the primitive side an 'as' could ever be
    /// declared, so this fails identically whether or not the class declares anything.
    /// </summary>
    [Fact]
    public void ClassToPrimitiveConversionIsAlwaysRejected()
    {
        AssertError(Codes.InvalidCast, """
            class Box { int v; func _init(int v) { self.v = v; } }
            kernel { entry func Main() {
                let Box b = new Box(1);
                let int i = b as int;
            } }
            """);
    }

    /// <summary>
    /// The class -> primitive rejection carries a hint steering the author toward a named
    /// conversion method instead of leaving it looking like a missing/misspelled overload.
    /// </summary>
    [Fact]
    public void ClassToPrimitiveRejectionHasAGuidingHint()
    {
        var (diag, _) = SingleFileCompile.Check("""
            class Box { int v; func _init(int v) { self.v = v; } }
            kernel { entry func Main() {
                let Box b = new Box(1);
                let int i = b as int;
            } }
            """);
        var d = Assert.Single(diag.All, x => x.Code == Codes.InvalidCast);
        Assert.Contains(d.Hints, h => h.Contains("named") && h.Contains("method"));
    }

    #endregion

    #region Codegen

    /// <summary>
    /// Finds the entry function's body statements among the module's free functions - 'kernel {
    /// entry func Main() { ... } }' lowers to a free function with IsEntry set, not a class
    /// member, so this is where 'let' statements inside Main live in the IR.
    /// </summary>
    private static IReadOnlyList<IrStmt> EntryBody(IrModule module)
    {
        var entry = module.FreeFunctions.Single(f => f.IsEntry);
        return Assert.IsType<IrBlock>(entry.Body).Stmts;
    }

    /// <summary>
    /// Finds the initializer expression of the 'let <name> = ...' declaration with the given
    /// name inside the given statement list.
    /// </summary>
    private static IrExpr DeclInit(IReadOnlyList<IrStmt> stmts, string name)
    {
        var decl = stmts.OfType<IrDeclVar>().Single(d => d.Name == name);
        Assert.NotNull(decl.Init);
        return decl.Init!;
    }

    /// <summary>
    /// A user-defined 'as' cast resolves to a direct call to the operator's C function
    /// (IrStaticCall, the same IR shape every other operator overload already uses), not
    /// IrCast - the raw C-style reinterpret cast that built-in numeric/pointer casts still use.
    /// </summary>
    [Fact]
    public void AsCastResolvesToOperatorCallNotRawCast()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Wrapper {
                int v;
                public operator Wrapper func as(int i) { let Wrapper w = new Wrapper(); w.v = i; return w; }
            }
            kernel { entry func Main() {
                let Wrapper w = 5 as Wrapper;
            } }
            """);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);

        // IrStaticCall proves this went through operator dispatch; the alternative (IrCast)
        // is what a bare numeric/pointer cast produces and would mean the operator never fired.
        Assert.IsType<IrStaticCall>(DeclInit(EntryBody(module!), "w"));
    }

    /// <summary>
    /// Two 'as' overloads on the same class get distinct C names, keyed by their parameter type,
    /// since they'd otherwise collide under the single 'gata_Owner_op' name every other
    /// operator uses - and the cast at each use site calls the one matching its own source type.
    /// </summary>
    [Fact]
    public void OverloadedAsOperatorsGetDistinctCNames()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Wrapper {
                int v;
                public operator Wrapper func as(int i) { let Wrapper w = new Wrapper(); w.v = i; return w; }
                public operator Wrapper func as(bool flag) { let Wrapper w = new Wrapper(); w.v = flag as int; return w; }
            }
            kernel { entry func Main() {
                let Wrapper a = 5 as Wrapper;
                let Wrapper b = true as Wrapper;
            } }
            """);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);

        // The symbol table's CNames (pre-densification, what codegen would emit without a
        // Release-mode dense pass) are already distinct and parameter-type-suffixed.
        var overloads = module!.Symbols.OperatorOverloads("Wrapper", "as");
        Assert.Equal(2, overloads.Count);
        Assert.NotEqual(overloads[0].CName, overloads[1].CName);
        Assert.StartsWith("gata_Wrapper_op", overloads[0].CName);
        Assert.StartsWith("gata_Wrapper_op", overloads[1].CName);

        // The IR call sites (post-densification - Densifier rewrites IrStaticCall.CName but
        // never touches the symbol table) still resolve to two distinct functions, proving the
        // two overloads didn't collapse into one at the call site either.
        var body = EntryBody(module);
        var intCall = Assert.IsType<IrStaticCall>(DeclInit(body, "a"));
        var boolCall = Assert.IsType<IrStaticCall>(DeclInit(body, "b"));
        Assert.NotEqual(intCall.CName, boolCall.CName);
    }

    /// <summary>
    /// An 'as' operator's emitted C signature has no 'self' parameter - only its explicit
    /// parameter - since no instance exists yet at the point of conversion.
    /// </summary>
    [Fact]
    public void AsOperatorSignatureOmitsSelf()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Wrapper {
                int v;
                public operator Wrapper func as(int i) { let Wrapper w = new Wrapper(); w.v = i; return w; }
            }
            kernel { entry func Main() {
                let Wrapper w = 5 as Wrapper;
            } }
            """);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);

        var op = Assert.Single(module!.Symbols.OperatorOverloads("Wrapper", "as"));
        Assert.True(op.Sig!.Params.Count == 1);

        var cls = Assert.Single(module.Classes, c => c.Name == "Wrapper");
        var irOp = Assert.Single(cls.Operators, o => o.Op == "as");
        Assert.True(irOp.IsStatic);
        string sig = Emitter.OperatorSig(irOp);
        Assert.DoesNotContain("self", sig);
    }

    #endregion
}
