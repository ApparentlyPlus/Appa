namespace Appa.Tests;

using Appa;

/// <summary>
/// Coverage for Pipeline.ValidateIntrinsics - the ARC runtime contract check.
///
/// This validation deliberately lives at the CLI front-end layer rather than inside
/// BuildModule, because BuildModule legitimately runs over stdlib-free input (every other
/// test file in this project does exactly that through SingleFileCompile). These tests
/// therefore drive it the same way RunBuild/RunCheck do: check a source, then run the
/// validator over the resulting module explicitly.
/// </summary>
public class IntrinsicValidationTests
{
    /// <summary>
    /// Checks a source with no libgata, then runs the intrinsic validator over the result.
    /// Asserts the source itself was otherwise clean so a typo in a fixture can't masquerade
    /// as the diagnostic under test.
    /// </summary>
    private static List<Diagnostic> ValidateOf(string src)
    {
        var (diag, module) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, "fixture should check cleanly before validation, but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
        Assert.NotNull(module);

        int before = diag.All.Count;
        Pipeline.ValidateIntrinsics(module, diag);
        return [.. diag.All.Skip(before)];
    }

    /// <summary>
    /// A reference-counted class with nothing binding the ARC roles is a broken build: the
    /// emitter reaches for all five, and the C would fail to link on undefined symbols.
    /// One diagnostic names the whole missing set rather than leaking them one at a time.
    /// </summary>
    [Fact]
    public void ManagedClassWithoutArcBindingsIsRejected()
    {
        var found = ValidateOf("""
            class Box {
                int v;
                func _init(int x) { self.v = x; }
            }
            kernel { entry func Main() { let Box b = new Box(5); } }
            """);

        var d = Assert.Single(found);
        Assert.Equal(Codes.MissingIntrinsic, d.Code);
        Assert.Equal(Severity.Error, d.Severity);
        foreach (var role in new[] { "alloc", "retain", "release", "obj_header", "obj_init" })
            Assert.Contains($"@intrinsic({role})", d.Message);
    }

    /// <summary>
    /// The diagnostic carries actionable hints. These are what tell the reader the standard
    /// library is at fault rather than their own program, so they are part of the contract.
    /// </summary>
    [Fact]
    public void MissingArcBindingsCarryHints()
    {
        var d = Assert.Single(ValidateOf("""
            class Box { int v; }
            kernel { entry func Main() { let Box b = new Box(); } }
            """));

        Assert.NotEmpty(d.Hints);
        Assert.Contains(d.Hints, h => h.Contains("whole set"));
        Assert.Contains(d.Hints, h => h.Contains("standard library is incomplete"));
    }

    /// <summary>
    /// A program that declares no reference-counted class never touches the runtime, so an
    /// unbound ARC role costs it nothing. Reporting here would fire on every libgata-free
    /// snippet in this test suite.
    /// </summary>
    [Fact]
    public void ProgramWithNoManagedClassIsClean()
    {
        Assert.Empty(ValidateOf("""
            kernel { entry func Main() {
                let int x = 0;
                for (let int i = 0; i < 3; i = i + 1) { x = x + i; }
            } }
            """));
    }

    /// <summary>
    /// A module is a stateless namespace, never instantiated and never reference-counted,
    /// so a module-only program does not pull in the ARC runtime either.
    /// </summary>
    [Fact]
    public void ModuleOnlyProgramIsClean()
    {
        Assert.Empty(ValidateOf("""
            module M { public static int func Twice(int x) { return x * 2; } }
            kernel { entry func Main() { let int y = M.Twice(21); } }
            """));
    }

    /// <summary>
    /// An enum is a plain integer value type with no object header, so it does not require
    /// the runtime any more than an int does.
    /// </summary>
    [Fact]
    public void EnumOnlyProgramIsClean()
    {
        Assert.Empty(ValidateOf("""
            enum Status { Pending, Active, Done }
            kernel { entry func Main() { let Status s = Status.Active; } }
            """));
    }

    /// <summary>
    /// A partially-bound standard library is the case this check exists for: binding some of
    /// the set is not partially working. Only the genuinely absent roles are named, so the
    /// message points at what to add rather than restating the whole contract as missing.
    /// </summary>
    [Fact]
    public void PartialArcBindingNamesOnlyTheMissingRoles()
    {
        var d = Assert.Single(ValidateOf("""
            @intrinsic(obj_header)
            native type obj { size_t __rc; }

            @intrinsic(alloc)
            void* func alloc(usize n) native { return 0; }

            @intrinsic(obj_init)
            void func obj_init(void* o, func(void*) -> void dtor) native { }

            class Box { int v; }
            kernel { entry func Main() { let Box b = new Box(); } }
            """));

        Assert.Equal(Codes.MissingIntrinsic, d.Code);
        Assert.Contains("@intrinsic(retain)", d.Message);
        Assert.Contains("@intrinsic(release)", d.Message);
        Assert.DoesNotContain("@intrinsic(alloc)", d.Message);
        Assert.DoesNotContain("@intrinsic(obj_init)", d.Message);
        Assert.DoesNotContain("@intrinsic(obj_header)", d.Message);
    }

    /// <summary>
    /// With the whole contract bound, nothing is reported - the positive control for the
    /// cases above.
    /// </summary>
    [Fact]
    public void FullyBoundArcContractIsClean()
    {
        Assert.Empty(ValidateOf("""
            @intrinsic(obj_header)
            native type obj { size_t __rc; }

            @intrinsic(alloc)
            void* func alloc(usize n) native { return 0; }

            @intrinsic(obj_init)
            void func obj_init(void* o, func(void*) -> void dtor) native { }

            @intrinsic(retain)
            void* func retain(void* p) native { return p; }

            @intrinsic(release)
            void func release(void* p) native { }

            class Box { int v; }
            kernel { entry func Main() { let Box b = new Box(); } }
            """));
    }
}
