namespace Appa.Tests;

using Appa;

/// <summary>
/// Regression coverage for the hardening pass: entry-signature validation, throws
/// return-type restrictions, throws-initializer type checking, realm-structure rules,
/// and the string-concat missing-operator diagnostic. Each test pins a case that
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

    /// <summary>
    /// An entry func is invoked through a fixed void(void) ABI; parameters used to be
    /// silently dropped by the emitter, producing C that referenced unbound names.
    /// </summary>
    [Fact]
    public void EntryFuncWithParamsIsRejected()
    {
        AssertError(Codes.BadEntrySignature,
            "kernel { entry func Main(int x) { } }");
    }

    /// <summary>
    /// An entry func has no caller to receive a value; a declared return type is an error.
    /// </summary>
    [Fact]
    public void EntryFuncWithReturnTypeIsRejected()
    {
        AssertError(Codes.BadEntrySignature,
            "kernel { entry int func Main() { return 1; } }");
    }

    /// <summary>
    /// An entry func cannot be throws - the Result would go nowhere.
    /// </summary>
    [Fact]
    public void EntryFuncWithThrowsIsRejected()
    {
        AssertError(Codes.BadEntrySignature,
            "kernel { entry throws func Main() { } }");
    }

    /// <summary>
    /// A thread entry's emitted signature is the fixed void(void*) thread ABI; declared
    /// parameters were scoped by the resolver but never bound in the emitted C.
    /// </summary>
    [Fact]
    public void ThreadEntryWithParamsIsRejected()
    {
        AssertError(Codes.BadEntrySignature, """
            kernel { entry func Main() { } }
            user { foreground process P { thread T { entry func Run(int x) { } } } }
            """);
    }

    /// <summary>
    /// The plain forms stay accepted.
    /// </summary>
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

    /// <summary>
    /// Every 'entry func' mangles to the single kernel entry symbol, so one inside
    /// 'user { }' in a GatOS build would link-collide with the kernel's.
    /// </summary>
    [Fact]
    public void UserEntryFuncInGatOSBuildIsRejected()
    {
        var prog = SingleFileCompile.Parse("""
            kernel { entry func Main() { } }
            user { entry func UMain() { } }
            """);
        var diag = new DiagnosticBag(new SourceSet());
        Pipeline.ValidateStructure([("t.g", prog)], Target.GatOS, diag);
        Assert.Contains(diag.All, d => d.Code == Codes.EntryOutsideKernel);
    }

    /// <summary>
    /// Hosted builds keep their user-entry rule: a user-block entry func is required there.
    /// </summary>
    [Fact]
    public void UserEntryFuncInHostedBuildIsAccepted()
    {
        var prog = SingleFileCompile.Parse("user { entry func UMain() { } }");
        var diag = new DiagnosticBag(new SourceSet());
        Pipeline.ValidateStructure([("t.g", prog)], Target.Hosted, diag);
        Assert.False(diag.HasErrors);
    }

    #endregion

    #region throws return types

    /// <summary>
    /// A throws pointer/array return type has no legal Result_T typedef spelling
    /// (it used to emit 'typedef ... Result_int*;' - invalid C).
    /// </summary>
    [Theory]
    [InlineData("throws int* func F() { throw; } kernel { entry func Main() { try { unsafe { let int* p = F(); } } catch { } } }")]
    [InlineData("throws [4]int func F() { throw; } kernel { entry func Main() { try { let [4]int a = F(); } catch { } } }")]
    public void ThrowsPointerOrArrayReturnIsRejected(string src)
    {
        AssertError(Codes.BadThrowsReturnType, src);
    }

    /// <summary>
    /// A throws method on a class is validated the same way as a free function.
    /// </summary>
    [Fact]
    public void ThrowsPointerReturnOnMethodIsRejected()
    {
        AssertError(Codes.BadThrowsReturnType, """
            class Box { public throws int* func Get() { throw; } }
            kernel { entry func Main() { } }
            """);
    }

    /// <summary>
    /// A throws enum return now emits a matching Result typedef (previously the typedef
    /// field was mis-typed as a class pointer).
    /// </summary>
    [Fact]
    public void ThrowsEnumReturnIsCleanAndEmitsMatchingTypedef()
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

    /// <summary>
    /// 'let T x = throwsCall();' used to skip type checking entirely because both sides
    /// of CheckAssign bailed on Result types.
    /// </summary>
    [Fact]
    public void ThrowsInitializerInnerTypeMismatchIsRejected()
    {
        AssertError(Codes.TypeMismatch, """
            class Box { int v; }
            throws Box func Make() { throw; }
            kernel { entry func Main() { try { let int x = Make(); } catch { } } }
            """);
    }

    /// <summary>
    /// A matching declared type (and a widening one) stays accepted.
    /// </summary>
    [Theory]
    [InlineData("throws int func F() { return 1; } kernel { entry func Main() { try { let int x = F(); } catch { } } }")]
    [InlineData("throws int func F() { return 1; } kernel { entry func Main() { try { let int64 x = F(); } catch { } } }")]
    public void ThrowsInitializerMatchingTypeIsClean(string src)
    {
        AssertClean(src);
    }

    #endregion

    #region Generic inference over multi-parameter generics

    /// <summary>
    /// Inferring a type argument from a multi-parameter generic class argument used to fail:
    /// the old inference split the mangled instance name (Pair_int_int) at the first
    /// underscore, so 'Pair[T, T]' never unified. Structural unification fixes it.
    /// </summary>
    [Fact]
    public void GenericFuncInfersFromMultiParamGenericClassArg()
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

    /// <summary>
    /// Conflicting bindings across the two slots of the same type parameter are diagnosed.
    /// </summary>
    [Fact]
    public void GenericFuncConflictingBindingIsRejected()
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

    /// <summary>
    /// Control transfer out of a defer body gets its own code, not a repurposed G004.
    /// </summary>
    [Theory]
    [InlineData("kernel { entry func Main() { defer { return; } } }")]
    [InlineData("kernel { entry func Main() { while (true) { defer { break; } } } }")]
    [InlineData("kernel { entry func Main() { while (true) { defer { continue; } } } }")]
    [InlineData("kernel { entry func Main() { defer { defer { let x = 1; } } } }")]
    public void DeferControlTransferUsesDeferTransferCode(string src)
    {
        AssertError(Codes.DeferTransfer, src);
    }

    /// <summary>
    /// A field on a module is a category error with its own code.
    /// </summary>
    [Fact]
    public void ModuleFieldUsesModuleFieldCode()
    {
        AssertError(Codes.ModuleField,
            "module M { int x; } kernel { entry func Main() { } }");
    }

    /// <summary>
    /// 'public private' and repeated modifiers are conflicts, not silent no-ops.
    /// </summary>
    [Theory]
    [InlineData("class C { public private func F() { } } kernel { entry func Main() { } }")]
    [InlineData("class C { public public func F() { } } kernel { entry func Main() { } }")]
    [InlineData("class C { static static func F() { } } kernel { entry func Main() { } }")]
    public void ConflictingModifiersAreRejected(string src)
    {
        AssertError(Codes.ConflictingModifiers, src);
    }

    /// <summary>
    /// _init/_deinit are called from generated allocator/destructor code that cannot
    /// receive a Result; throws on them is a hard error.
    /// </summary>
    [Theory]
    [InlineData("class C { throws func _init() { } } kernel { entry func Main() { } }")]
    [InlineData("class C { throws func _deinit() { } } kernel { entry func Main() { } }")]
    public void ThrowsOnLifecycleMethodIsRejected(string src)
    {
        AssertError(Codes.LifecycleThrows, src);
    }

    #endregion

    #region Single-source-of-truth consistency

    /// <summary>
    /// Every primitive spelling in the shared table lexes as a primitive keyword, so the
    /// lexer's keyword list cannot silently drift from PrimTypes.
    /// </summary>
    [Fact]
    public void EveryPrimitiveSpellingLexesAsAPrimitiveKeyword()
    {
        foreach (var name in SymbolTable.Primitives)
        {
            var kind = SingleFileCompile.Tokenize(name)[0].Kind;
            Assert.True(kind is TK.TBool or TK.TInt or TK.TChar or TK.TFloat or TK.TDouble
                        or TK.TShort or TK.TVoid or TK.TPrim,
                $"primitive '{name}' lexed as {kind}, not a primitive keyword");
        }
    }

    /// <summary>
    /// Every overloadable operator symbol (except 'as', which is deliberately generic)
    /// has a distinct mangling suffix, so two operators can never share a C name.
    /// </summary>
    [Fact]
    public void OperatorManglingSuffixesAreDistinct()
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

    /// <summary>
    /// '+' on Strings with no String '+' operator in the build is a diagnostic, not a
    /// silently fabricated symbol that fails at link time.
    /// </summary>
    [Fact]
    public void StringConcatWithoutOperatorIsDiagnosed()
    {
        AssertError(Codes.MissingIntrinsic,
            """kernel { entry func Main() { let s = "a" + "b"; } }""");
    }

    #endregion
}
