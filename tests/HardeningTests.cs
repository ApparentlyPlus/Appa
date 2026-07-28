namespace Appa.Tests;

using Appa;

/// <summary>
/// Regression coverage for the hardening pass: entry signatures, throws return types and
/// initializers, realm structure, and the string-concat operator diagnostic. Each pins a case that
/// previously produced uncompilable or wrong C with no diagnostic.
/// </summary>
public class HardeningTests
{
    private static void AssertError(string code, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.True(diag.HasErrors, $"expected {code} but no errors were produced");
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == code);
    }

    private static void AssertClean(string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
    }

    #region Entry signatures

    [Fact]
    public void EntryFuncWithParamsIsRejected()
    {
        AssertError(Codes.BadEntrySignature,
            "kernel { entry func Main(int x) { } }");
    }

    [Fact]
    public void EntryFuncWithReturnTypeIsRejected()
    {
        AssertError(Codes.BadEntrySignature,
            "kernel { entry int func Main() { return 1; } }");
    }

    [Fact]
    public void EntryFuncWithThrowsIsRejected()
    {
        AssertError(Codes.BadEntrySignature,
            "kernel { entry throws func Main() { } }");
    }

    [Fact]
    public void ThreadEntryWithParamsIsRejected()
    {
        AssertError(Codes.BadEntrySignature, """
            kernel { entry func Main() { } }
            user { foreground process P { thread T { entry func Run(int x) { } } } }
            """);
    }

    [Fact]
    public void PlainEntrySignaturesAreClean()
    {
        AssertClean("""
            kernel { entry func Main() { } }
            user { foreground process P { thread T { entry func Run() { } } } }
            """);
    }

    #endregion

    #region Realm structure

    [Fact]
    public void UserEntryInGatOSIsRejected()
    {
        var prog = SingleFileCompile.Parse("""
            kernel { entry func Main() { } }
            user { entry func UMain() { } }
            """);
        var diag = new DiagnosticBag(new SourceSet());
        Pipeline.ValidateStructure([("t.g", prog)], Target.GatOS, diag);
        Assert.Contains(diag.All, d => d.Code == Codes.EntryOutsideKernel);
    }

    [Fact]
    public void UserEntryInHostedIsAccepted()
    {
        var prog = SingleFileCompile.Parse("user { entry func UMain() { } }");
        var diag = new DiagnosticBag(new SourceSet());
        Pipeline.ValidateStructure([("t.g", prog)], Target.Hosted, diag);
        Assert.False(diag.HasErrors);
    }

    #endregion

    #region throws return types

    /// <summary>
    /// A throws pointer/array return type has no legal Result_T typedef spelling (it used to emit
    /// 'typedef ... Result_int*;' - invalid C).
    /// </summary>
    [Theory]
    [InlineData("throws int* func F() { throw; } kernel { entry func Main() { try { unsafe { let int* p = F(); } } catch { } } }")]
    [InlineData("throws [4]int func F() { throw; } kernel { entry func Main() { try { let [4]int a = F(); } catch { } } }")]
    public void ThrowsPointerReturnIsRejected(string src)
    {
        AssertError(Codes.BadThrowsReturnType, src);
    }

    [Fact]
    public void ThrowsPointerMethodIsRejected()
    {
        AssertError(Codes.BadThrowsReturnType, """
            class Box { public throws int* func Get() { throw; } }
            kernel { entry func Main() { } }
            """);
    }

    [Fact]
    public void ThrowsEnumReturnEmitsItsTypedef()
    {
        var output = SingleFileCompile.Emit("""
            enum Color { Red, Green }
            throws Color func Pick(bool ok) { if (ok) { return Color.Red; } throw; }
            kernel { entry func Main() { try { let Color c = Pick(true); } catch { } } }
            """);
        Assert.NotEmpty(output);
        var shared = Assert.Single(output, f => f.Name == "shared.h").Content;
        Assert.Contains("typedef struct { gata_Color value; bool has_error; } Result_Color;", shared);
    }

    #endregion

    #region throws initializer type checking

    [Fact]
    public void ThrowsInitTypeMismatchIsRejected()
    {
        AssertError(Codes.TypeMismatch, """
            class Box { int v; }
            throws Box func Make() { throw; }
            kernel { entry func Main() { try { let int x = Make(); } catch { } } }
            """);
    }

    [Theory]
    [InlineData("throws int func F() { return 1; } kernel { entry func Main() { try { let int x = F(); } catch { } } }")]
    [InlineData("throws int func F() { return 1; } kernel { entry func Main() { try { let int64 x = F(); } catch { } } }")]
    public void ThrowsInitMatchingTypeIsClean(string src)
    {
        AssertClean(src);
    }

    #endregion

    #region Generic inference over multi-parameter generics

    /// <summary>
    /// Inferring a type argument from a multi-parameter generic class argument used to fail: the
    /// old inference split the mangled instance name (Pair_int_int) at the first underscore, so
    /// 'Pair[T, T]' never unified. Structural unification fixes it.
    /// </summary>
    [Fact]
    public void GenericFuncInfersFromMultiParam()
    {
        AssertClean("""
            class Pair[A, B] {
                A first;
                B second;
                public A func First() { return self.first; }
            }
            T func GetFirst[T](Pair[T, T] p) { return p.First(); }
            kernel { entry func Main() {
                let Pair[int, int] p = new Pair[int, int]();
                let int x = GetFirst(p);
            } }
            """);
    }

    [Fact]
    public void ConflictingBindingIsRejected()
    {
        AssertError(Codes.ArgTypeMismatch, """
            class Pair[A, B] {
                A first;
                B second;
                public A func First() { return self.first; }
            }
            T func GetFirst[T](Pair[T, T] p) { return p.First(); }
            kernel { entry func Main() {
                let Pair[int, bool] p = new Pair[int, bool]();
                let int x = GetFirst(p);
            } }
            """);
    }

    #endregion

    #region Dedicated diagnostic codes

    [Theory]
    [InlineData("kernel { entry func Main() { defer { return; } } }")]
    [InlineData("kernel { entry func Main() { while (true) { defer { break; } } } }")]
    [InlineData("kernel { entry func Main() { while (true) { defer { continue; } } } }")]
    [InlineData("kernel { entry func Main() { defer { defer { let x = 1; } } } }")]
    public void DeferControlTransferHasItsOwnCode(string src)
    {
        AssertError(Codes.DeferTransfer, src);
    }

    [Fact]
    public void ModuleFieldUsesModuleFieldCode()
    {
        AssertError(Codes.ModuleField,
            "module M { int x; } kernel { entry func Main() { } }");
    }

    [Theory]
    [InlineData("class C { public private func F() { } } kernel { entry func Main() { } }")]
    [InlineData("class C { public public func F() { } } kernel { entry func Main() { } }")]
    [InlineData("class C { static static func F() { } } kernel { entry func Main() { } }")]
    public void ConflictingModifiersAreRejected(string src)
    {
        AssertError(Codes.ConflictingModifiers, src);
    }

    [Theory]
    [InlineData("class C { throws func _init() { } } kernel { entry func Main() { } }")]
    [InlineData("class C { throws func _deinit() { } } kernel { entry func Main() { } }")]
    public void ThrowsOnLifecycleMethodIsRejected(string src)
    {
        AssertError(Codes.LifecycleThrows, src);
    }

    #endregion

    #region Single-source-of-truth consistency

    [Fact]
    public void EveryPrimitiveLexesAsAKeyword()
    {
        foreach (var name in SymbolTable.Primitives)
        {
            var kind = SingleFileCompile.Tokenize(name)[0].Kind;
            Assert.True(kind is TK.TBool or TK.TInt or TK.TChar or TK.TFloat or TK.TDouble
                        or TK.TShort or TK.TVoid or TK.TPrim,
                $"primitive '{name}' lexed as {kind}, not a primitive keyword");
        }
    }

    [Fact]
    public void OperatorSuffixesAreDistinct()
    {
        string[] ops = ["+", "-", "*", "/", "==", "!=", "<", ">", "<=", ">=",
                        "&", "|", "^", "<<", ">>", "[]", "[]=", "!", "~", "++", "--"];
        var seen = new HashSet<string>();
        foreach (var op in ops)
        {
            string suffix = Mangler.OpSuffix(op);
            Assert.NotEqual("op", suffix);
            Assert.True(seen.Add(suffix), $"operators share the mangling suffix '{suffix}'");
        }
    }

    #endregion

    #region String concatenation floor

    [Fact]
    public void StringConcatNeedsAnOperator()
    {
        AssertError(Codes.MissingIntrinsic,
            """kernel { entry func Main() { let s = "a" + "b"; } }""");
    }

    #endregion

    #region Speculative parsing leaves no residue

    /// <summary>
    /// An abandoned speculative parse gives back the recursion depth it used. 'a[0].x' is read both
    /// ways and the failing attempt unwinds past every ExitDepth, so the leak is cumulative: 195 of
    /// these hit MaxDepth. The count here is well past that.
    /// </summary>
    [Fact]
    public void SpeculativeParseRestoresDepth()
    {
        var body = new System.Text.StringBuilder();
        for (int i = 0; i < 600; i++) body.Append($"    total = total + a[0].x + b[i].y;\n");

        AssertClean($$"""
            class P { public int x; public int y; func _init() { } }
            kernel { entry func Main() {
                let [2]P a = default([2]P);
                let [2]P b = default([2]P);
                let int i = 0;
                let int total = 0;
            {{body}}    }
            }
            """);
    }

    #endregion
}
