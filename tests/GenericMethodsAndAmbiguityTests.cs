namespace Appa.Tests;

using Appa;

/// <summary>
/// Regression coverage for generic methods on classes and modules, the AmbiguousCall diagnostic for
/// a generic free function shadowing an equally plausible sibling, and the file-basename qualifier
/// that disambiguates it.
/// </summary>
public class GenericMethodsAndAmbiguityTests
{
    private static void AssertError(string code, string src, string path = "<test>")
    {
        var (diag, _) = SingleFileCompile.Check(src, path);
        Assert.True(diag.HasErrors, $"expected {code} but no errors were produced");
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == code);
    }

    private static void AssertClean(string src, string path = "<test>")
    {
        var (diag, _) = SingleFileCompile.Check(src, path);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
    }

    /// <summary>
    /// Compiles each file's source with every other file mutually visible, mirroring how
    /// Pipeline.BuildModule is driven once real import resolution has already produced a visibility
    /// map - the exact mechanism SingleFileCompile.Check uses for one file.
    /// </summary>
    private static (DiagnosticBag Diag, IrModule? Module) CheckMulti(params (string Path, string Src)[] files)
    {
        var sources = new SourceSet();
        foreach (var (path, src) in files) sources.Add(path, src);
        var diag = new DiagnosticBag(sources);

        var programs = new List<(string path, Program prog)>();
        foreach (var (path, src) in files)
        {
            Program? prog = null;
            try { prog = SingleFileCompile.Parse(src); }
            catch (ParseException ex) { diag.Error(ex.Code, path, ex.Span, ex.Message, ex.Hints); }
            if (prog == null) return (diag, null);
            programs.Add((path, prog));
        }

        var allPaths = files.Select(f => f.Path).ToHashSet();
        var visible = files.ToDictionary(f => f.Path, _ => allPaths);
        var (module, _, _) = Pipeline.BuildModule(programs, visible, Mode.Debug, diag);
        return (diag, module);
    }

    #region Generic methods

    [Fact]
    public void GenericModuleCompilesPerInstance()
    {
        AssertClean("""
            module Foo {
                public T func Id[T](T x) { return x; }
            }
            realm kernel { entry func Main() {
                let int a = Foo.Id(5);
                let String b = Foo.Id("hi");
            } }
            """);
    }

    [Fact]
    public void GenericStaticClassMethodCompiles()
    {
        AssertClean("""
            class Foo {
                public static T func Id[T](T x) { return x; }
            }
            realm kernel { entry func Main() {
                let int a = Foo.Id(5);
            } }
            """);
    }

    /// <summary>
    /// A generic INSTANCE method has a real 'self', usable inside the instantiated body to call an
    /// ordinary sibling instance method - the full "self instantiation" fix, not just the
    /// static/module case.
    /// </summary>
    [Fact]
    public void GenericInstanceMethodCompiles()
    {
        var (diag, module) = SingleFileCompile.Check("""
            class Box {
                int tag;
                func _init(int t) { self.tag = t; }
                public int func Tag() { return self.tag; }
                public U func Combine[U](U seed) {
                    let int t = self.Tag();
                    return seed;
                }
            }
            realm kernel { entry func Main() {
                let Box b = new Box(7);
                let int r1 = b.Combine(5);
                let String r2 = b.Combine("hi");
            } }
            """);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} {d.Message}")));
        Assert.NotNull(module);
        // Two distinct instantiations (Combine[int], Combine[String]) should each be a
        // real class method with self, not free functions - confirming the drain path
        // attaches generic instance-method instantiations to the owning IrClass.
        var box = module!.Classes.Single(c => c.Name == "Box");
        Assert.Equal(2, box.Methods.Count(m => m.Name.StartsWith("Combine_")));
        Assert.All(box.Methods.Where(m => m.Name.StartsWith("Combine_")),
            m => Assert.False(m.IsStatic));
    }

    [Fact]
    public void GenericModuleCallsItsSibling()
    {
        AssertClean("""
            module Foo {
                public T func Id[T](T x) { return x; }
                public T func Wrap[T](T x) { return Id(x); }
            }
            realm kernel { entry func Main() {
                let int a = Foo.Wrap(5);
            } }
            """);
    }

    #endregion

    #region AmbiguousCall (G069)

    [Fact]
    public void FreeFuncVsSiblingIsAmbiguous()
    {
        AssertError(Codes.AmbiguousCall, """
            T func Min[T](T a, T b) { if (a < b) { return a; } return b; }
            module Foo {
                public double func Min(double a, double b) { return Min(a, b); }
            }
            realm kernel { entry func Main() { } }
            """);
    }

    [Fact]
    public void ModuleQualifierResolvesAmbiguity()
    {
        AssertClean("""
            T func Min[T](T a, T b) { if (a < b) { return a; } return b; }
            module Foo {
                public double func Min(double a, double b) { return Foo.Min(a, b); }
            }
            realm kernel { entry func Main() { } }
            """, path: "collide.g");
    }

    [Fact]
    public void FileNameQualifierResolvesAmbiguity()
    {
        AssertClean("""
            T func Min[T](T a, T b) { if (a < b) { return a; } return b; }
            module Foo {
                public double func Min(double a, double b) { return collide.Min(a, b); }
            }
            realm kernel { entry func Main() { } }
            """, path: "collide.g");
    }

    [Fact]
    public void GenericInTwoFilesIsAmbiguous()
    {
        var (diag, _) = CheckMulti(
            ("a.g", "T func Pick[T](T a, T b) { return a; }"),
            ("b.g", "T func Pick[T](T a, T b) { return b; }"),
            ("main.g", "realm kernel { entry func Main() { let int x = Pick(1, 2); } }"));
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == Codes.AmbiguousCall);
    }

    [Fact]
    public void FileQualifierResolvesCollision()
    {
        var (diag, _) = CheckMulti(
            ("a.g", "T func Pick[T](T a, T b) { return a; }"),
            ("b.g", "T func Pick[T](T a, T b) { return b; }"),
            ("main.g", "realm kernel { entry func Main() { let int x = a.Pick(1, 2); let int y = b.Pick(1, 2); } }"));
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} {d.Message}")));
    }

    #endregion

    #region Privacy/scope gating for generic templates

    [Fact]
    public void PrivateGenericsDoNotClobber()
    {
        var (diag, module) = CheckMulti(
            ("a.g", """
                private T func Pick[T](T x, T y) { return x; }
                int func UseA() { return Pick(1, 2); }
                """),
            ("b.g", """
                private T func Pick[T](T x, T y) { return y; }
                int func UseB() { return Pick(1, 2); }
                """),
            ("main.g", """
                realm kernel { entry func Main() { } }
                """));
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} {d.Message}")));
        Assert.NotNull(module);
    }

    #endregion
}
