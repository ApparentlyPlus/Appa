namespace Appa.Tests;

using Appa;

// G016/G039 need a String or union payload constructor that only resolves once
// libgata is really imported - both are covered by the torture fixture port
// (TortureTests) instead. G019/G020/G036/G042 need a real environment/release-mode
// setup neither this file nor the torture corpus currently reproduces.
/// <summary>
/// Inline string-literal semantic coverage: one minimal repro per diagnostic code
/// reachable without libgata, plus a set of error-free "good path" language-feature
/// programs. Repros are adapted from the torture corpus's import-free fixtures so
/// the expected code is already validated against the real compiler.
/// </summary>
public class PipelineTests
{
    /// <summary>
    /// Each source produces at least one error diagnostic carrying the expected code.
    /// </summary>
    [Theory]
    [InlineData("G044", "kernel { entry func Main() { let int x = 5 } }")]
    [InlineData("G003", "kernel { entry func Main() { } } int func f(int x, int x) { return x; }")]
    [InlineData("G003", "kernel { entry func Main() { let int s = 10; let String s = \"hi\"; } }")]
    [InlineData("G004", "kernel { entry func Main() { let x = true + 5; } }")]
    [InlineData("G004", "kernel { entry func Main() { let x = 0x1FFFFFFFFFFFFFFFFF; } }")]
    [InlineData("G004", "kernel { entry func Main() { let x = 999999999999999999999999; } }")]
    [InlineData("G004", "kernel { entry func Main() { let x = 3 && 4; } }")]
    [InlineData("G004", "kernel { entry func Main() { let int64 a = 5; let int b = a; } }")]
    [InlineData("G004", "class Box { int v; func _init(int x) { self.v = x; } } kernel { entry func Main() { let int x = true ? 1 : new Box(2); } }")]
    [InlineData("G004", "kernel { entry func Main() { defer defer { } } }")]
    [InlineData("G005", "kernel { entry func Main() { let int x = y + 1; } }")]
    [InlineData("G006", "kernel { entry func Main() { let int n = 5; n.foo(); } }")]
    [InlineData("G006", "class C { int x; func f() { let y = self.x(); } } kernel { entry func Main() { } }")]
    [InlineData("G007", "kernel { entry func Main(void x) { x -= 1; } }")]
    [InlineData("G007", "T func make[T]() { return default(T); } kernel { entry func Main() { let int z = make(); } }")]
    [InlineData("G007", "kernel { entry func Main() { let long a = 5; } }")]
    [InlineData("G007", "kernel { entry func Main() { let void v; } }")]
    [InlineData("G008", "int func add(int a, int b) { return a + b; } kernel { entry func Main() { let int x = add(1); } }")]
    [InlineData("G009", "T func same[T](T a, T b) { return a; } kernel { entry func Main() { let int64 z = same(3, (4 as int64)); } }")]
    [InlineData("G010", "kernel { entry func Main() { } int func f() { return; } }")]
    [InlineData("G010", "kernel { entry func Main() { } void func f() { return 5; } }")]
    [InlineData("G011", "module M { } kernel { entry func Main() { let M m = new M(); } }")]
    [InlineData("G012", "kernel { entry func Main() { let int n = 5; let x = n[0]; } }")]
    [InlineData("G013", "class C { public int func F() { return 1; } } kernel { entry func Main() { let int x = C.F(); } }")]
    [InlineData("G014", "class C { public static int func F() { return 1; } } kernel { entry func Main() { let C c = new C(); let int x = c.F(); } }")]
    [InlineData("G015", "int func F(int a, float b) { return a; } int func F(float a, int b) { return b; } kernel { entry func Main() { let int x = F(1, 1); } }")]
    [InlineData("G017", "@intrinsic(totally_bogus_role) int func Foo() { return 0; } kernel { entry func Main() { } }")]
    [InlineData("G018", "@intrinsic(alloc) void* func A(usize n) { return null; } @intrinsic(alloc) void* func B(usize n) { return null; } kernel { entry func Main() { } }")]
    [InlineData("G021", "kernel { entry func Main() { throw; } }")]
    [InlineData("G022", "kernel { entry func Main() { continue; } }")]
    [InlineData("G022", "throws int func risky(int x) { if (x < 0) { throw; } return x; } kernel { entry func Main() { try { let int x = risky(1); } catch { break; } } }")]
    [InlineData("G027", "kernel { entry func Main() { } int func f() { } }")]
    [InlineData("G028", "kernel { entry func Main() { let int x = 5; let y = x as void; } }")]
    [InlineData("G029", "kernel { entry func Main() { if (5) { } } }")]
    [InlineData("G030", "kernel { entry func Main() { Main(); } }")]
    [InlineData("G031", "kernel { entry func Main() {} } user { foreground process A { thread T { entry func Run() { panic \"nope\"; } } } }")]
    [InlineData("G032", "kernel { entry func Main() { for i in 5 { } } }")]
    [InlineData("G033", "kernel { entry func Main() { let int x = 5; let int* p = &x; } }")]
    [InlineData("G034", "kernel { entry func Main() { 5 = 3; } }")]
    [InlineData("G035", "class Box { private int v; func _init(int x) { self.v = x; } } kernel { entry func Main() { let Box b = new Box(1); let int z = b.v; } }")]
    [InlineData("G035", "module M { private int func helper() { return 1; } } kernel { entry func Main() { let int z = M.helper(); } }")]
    [InlineData("G037", "void func TakesInt(int n) { } kernel { entry func Main() { let int x = 1; TakesInt(ref x); } }")]
    [InlineData("G038", "class RO { int v; operator func [](int i) -> int { return self.v; } } kernel { entry func Main() { let RO r = new RO(); r[0] = 5; } }")]
    [InlineData("G040", "static int func helper() { return 1; } kernel { entry func Main() { let int x = helper(); } }")]
    [InlineData("G041", "@intrinsic(alloc) native { #kernel: int x; #user: int x; } kernel { entry func Main() { } }")]
    [InlineData("G041", "class C { @keep int func F() { return 1; } } kernel { entry func Main() { } }")]
    [InlineData("G041", "@preamble(user) int func Foo() { return 1; } kernel { entry func Main() { } }")]
    [InlineData("G043", "kernel { entry func Main() {} } user { foreground process App { background thread Worker { entry func Run() {} } } }")]
    public void ProducesExpectedErrorCode(string expectedCode, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.True(diag.HasErrors, $"expected {expectedCode} but source produced no errors");
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == expectedCode);
    }

    /// <summary>
    /// Each source produces at least one warning diagnostic carrying the expected
    /// code, with no errors (a warning alone never fails a build).
    /// </summary>
    [Theory]
    [InlineData("G023", "kernel { entry func Main() { let int x = 5; } }")]
    [InlineData("G024", "int func F() { return 1; let int x = 2; } kernel { entry func Main() { let int y = F(); } }")]
    [InlineData("G025", "kernel { entry func Main() { if (true) { } } }")]
    [InlineData("G026", "void func F() { return; } kernel { entry func Main() { F(); } }")]
    public void ProducesExpectedWarningCode(string expectedCode, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors);
        Assert.Contains(diag.All, d => d.Severity == Severity.Warning && d.Code == expectedCode);
    }

    /// <summary>
    /// Pipeline.ValidateStructure enforces exactly one kernel block with exactly
    /// one entry func - these codes only surface through that pass, not BuildModule.
    /// </summary>
    [Theory]
    [InlineData("G002", "module M { }")]
    [InlineData("G001", "kernel { entry func Main() { } } kernel { entry func Main2() { } }")]
    public void ValidateStructureProducesExpectedCode(string expectedCode, string src)
    {
        var prog = SingleFileCompile.Parse(src);
        var programs = new List<(string path, Appa.Program prog)> { ("<test>", prog) };
        var sources = new SourceSet();
        sources.Add("<test>", src);
        var diag = new DiagnosticBag(sources);
        Pipeline.ValidateStructure(programs, target: null, diag);
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == expectedCode);
    }

    /// <summary>
    /// Hosted-target realm validation: a kernel{} block is a hard error, and exactly one
    /// user{} block with exactly one entry func is required.
    /// </summary>
    [Theory]
    [InlineData("G055", "kernel { entry func Main() { } } user { entry func Main() { } }")]
    [InlineData("G056", "class M { }")]
    [InlineData("G057", "user { entry func A() { } } user { entry func B() { } }")]
    [InlineData("G058", "user { func f() { } }")]
    [InlineData("G059", "user { entry func A() { } entry func B() { } }")]
    public void ValidateStructureProducesExpectedHostedCode(string expectedCode, string src)
    {
        var prog = SingleFileCompile.Parse(src);
        var programs = new List<(string path, Appa.Program prog)> { ("<test>", prog) };
        var sources = new SourceSet();
        sources.Add("<test>", src);
        var diag = new DiagnosticBag(sources);
        Pipeline.ValidateStructure(programs, Target.Hosted, diag);
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == expectedCode);
    }

    /// <summary>
    /// A well-formed Hosted program (one user{} block, one entry func, no kernel{})
    /// passes validation cleanly.
    /// </summary>
    [Fact]
    public void ValidateStructureAcceptsWellFormedHosted()
    {
        const string src = "user { entry func Main() { } }";
        var prog = SingleFileCompile.Parse(src);
        var programs = new List<(string path, Appa.Program prog)> { ("<test>", prog) };
        var sources = new SourceSet();
        sources.Add("<test>", src);
        var diag = new DiagnosticBag(sources);
        Pipeline.ValidateStructure(programs, Target.Hosted, diag);
        Assert.False(diag.HasErrors);
    }

    /// <summary>
    /// Error-free programs exercising core language features: generics with
    /// inference, enums with switch, ternary/widening/narrowing, compound
    /// assignment, width-explicit primitives, and unsafe pointer locals.
    /// </summary>
    [Theory]
    [InlineData("""
        T func max[T](T a, T b) { if (a > b) { return a; } return b; }
        kernel { entry func Main() {
          let int a = max(3, 7);
          let int64 b = max((10 as int64), (4 as int64));
          if (a == 7) { } else { }
        } }
        """)]
    [InlineData("""
        enum Dir { North, East, South, West }
        int func turns(Dir d) {
          switch (d) {
            case Dir.North { return 0; }
            case Dir.East, Dir.West { return 1; }
            default { return 2; }
          }
        }
        kernel { entry func Main() { let int t = turns(Dir.West); if (t == 1) { } else { } } }
        """)]
    [InlineData("""
        kernel { entry func Main() {
          let int a = 3;
          let int b = 7;
          let int m = a > b ? a : b;
          let int64 w = a > 0 ? (m as int64) : b;
          if (m == 7) { } else { }
        } }
        """)]
    [InlineData("""
        kernel { entry func Main() {
          let int f = 0;
          f |= 8; f &= 12; f ^= 4; f <<= 2; f >>= 1;
          let uint u = 1u;
          u |= 2u; u <<= 3;
        } }
        """)]
    [InlineData("""
        throws int64 func parse(int x) {
          if (x < 0) { throw; }
          return (x as int64);
        }
        kernel { entry func Main() {
          let int64 sum = 0;
          try {
            let int64 parsed = parse(5);
            sum = sum + parsed;
          } catch { }
          unsafe {
            let byte b = 7;
            let byte* p = &b;
            let byte first = *p;
          }
          if (sum >= 0) { } else { }
        } }
        """)]
    public void GoodPathProgramsHaveNoErrors(string src)
    {
        var (diag, module) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);
    }

    /// <summary>
    /// A user-defined operator overload dispatches on the declared operator and
    /// type-checks the operand types against its signature.
    /// </summary>
    [Fact]
    public void OperatorOverloadDispatchesOnDeclaredOperator()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Vec {
              int x;
              int y;
              func _init(int a, int b) { self.x = a; self.y = b; }
              operator func +(Vec other) -> Vec { return new Vec(self.x + other.x, self.y + other.y); }
              public int func Sum() { return self.x + self.y; }
            }
            kernel { entry func Main() {
              let Vec a = new Vec(1, 2);
              let Vec b = new Vec(3, 4);
              let Vec c = a + b;
              if (c.Sum() == 10) { } else { }
            } }
            """);
        Assert.False(diag.HasErrors);
        Assert.NotNull(module);
    }
}
