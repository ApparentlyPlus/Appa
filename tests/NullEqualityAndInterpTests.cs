namespace Appa.Tests;

using Appa;

/// <summary>
/// Two lowerings that pick a cheaper or safer shape than the source's surface form: null equality
/// always compiling to a pointer check, so a class's own '==' cannot recurse through its null
/// guards; and interpolation past two parts going through a StringBuilder.
/// </summary>
public class NullEqualityAndInterpTests
{
    /// <summary>
    /// IR walker that records every call CName plus every IrNew class name reachable from a
    /// statement, so tests can assert which lowering was chosen after the full pipeline (including
    /// Densifier renaming) has run.
    /// </summary>
    private sealed class CallCollector : IrWalker
    {
        public readonly List<string> Calls = [];
        public readonly List<string> News = [];

        public void Collect(IrStmt s) => WalkStmt(s);

        protected override void WalkExpr(IrExpr e)
        {
            switch (e)
            {
                case IrStaticCall sc: Calls.Add(sc.CName); break;
                case IrInstanceCall ic: Calls.Add(ic.CName); break;
                case IrNew n: News.Add(n.ClassName); break;
                case IrNewInit ni: News.Add(ni.ClassName); break;
            }
            base.WalkExpr(e);
        }
    }

    private static IrModule CheckClean(string src)
    {
        var (diag, module) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, "expected no errors but got: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error)
                                      .Select(d => $"{d.Code} {d.Message}")));
        Assert.NotNull(module);
        return module!;
    }

    private static CallCollector CollectEntry(IrModule module)
    {
        var entry = module.FreeFunctions.Single(f => f.IsEntry && f.Body != null);
        var c = new CallCollector();
        c.Collect(entry.Body!);
        return c;
    }

    /// <summary>
    /// The CNames of every '==' / '!=' operator in the module, post-renaming, so a test can assert
    /// their presence or absence among the entry function's calls.
    /// </summary>
    private static HashSet<string> EqOperatorCNames(IrModule module)
    {
        return [.. module.Classes
            .SelectMany(c => c.Operators)
            .Where(o => o.Op is "==" or "!=")
            .Select(o => o.CName)];
    }

    #region Null-literal equality

    private const string BoxWithEq = """
        class Box {
            int v;
            func _init(int x) { self.v = x; }
            public operator bool func ==(Box o) {
                if (o == null) { return false; }
                return self.v == o.v;
            }
        }
        """;

    [Fact]
    public void EqNullBypassesDeclaredOperator()
    {
        var module = CheckClean(BoxWithEq + """
            kernel { entry func Main() {
                let Box b = new Box(1);
                if (b == null) { return; }
            } }
            """);
        var eqOps = EqOperatorCNames(module);
        Assert.DoesNotContain(CollectEntry(module).Calls, c => eqOps.Contains(c));
    }

    [Fact]
    public void NeNullBypassesDeclaredOperator()
    {
        var module = CheckClean(BoxWithEq + """
            kernel { entry func Main() {
                let Box b = new Box(1);
                if (b != null) { return; }
            } }
            """);
        var eqOps = EqOperatorCNames(module);
        Assert.DoesNotContain(CollectEntry(module).Calls, c => eqOps.Contains(c));
    }

    [Fact]
    public void NullOnLeftIsIdentity()
    {
        var module = CheckClean(BoxWithEq + """
            kernel { entry func Main() {
                let Box b = new Box(1);
                if (null == b) { return; }
            } }
            """);
        var eqOps = EqOperatorCNames(module);
        Assert.DoesNotContain(CollectEntry(module).Calls, c => eqOps.Contains(c));
    }

    [Fact]
    public void ValueEqualityStillDispatches()
    {
        var module = CheckClean(BoxWithEq + """
            kernel { entry func Main() {
                let Box a = new Box(1);
                let Box b = new Box(1);
                if (a == b) { return; }
            } }
            """);
        var eqOps = EqOperatorCNames(module);
        Assert.Contains(CollectEntry(module).Calls, c => eqOps.Contains(c));
    }

    [Fact]
    public void IntAgainstNullIsRejected()
    {
        var (diag, _) = SingleFileCompile.Check(
            "kernel { entry func Main() { let int n = 5; if (n == null) { } } }");
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == Codes.TypeMismatch);
    }

    [Fact]
    public void PlainClassNullCheckStillClean()
    {
        CheckClean("""
            class Plain { int v; }
            kernel { entry func Main() {
                let Plain p = new Plain();
                if (p == null) { return; }
            } }
            """);
    }

    #endregion

    #region Interpolation lowering

    // The minimal stdlib surface interpolation needs: a @builtin(String) with '+' for the
    // concat fallback, the stringify_int role for {int} parts, and a @builtin(StringBuilder)
    // with the Put/ToString pair the builder lowering resolves.
    private const string InterpStubs = """
        @builtin(String)
        class String {
            int len;
            public operator String func +(String other) { return self; }
        }

        @intrinsic(stringify_int)
        String func IntToString(int n) { return new String(); }

        @builtin(StringBuilder)
        class StringBuilder {
            int n;
            public void func Append(String s) { self.n = self.n + 1; }
            public StringBuilder func Put(String s) { self.Append(s); return self; }
            public String func ToString() { return new String(); }
        }
        """;

    [Fact]
    public void ThreePartInterpUsesStringBuilder()
    {
        var module = CheckClean(InterpStubs + """
            kernel { entry func Main() {
                let int x = 1;
                let String s = $"a{x}b{x}c";
            } }
            """);
        var calls = CollectEntry(module);
        Assert.Contains("StringBuilder", calls.News);
    }

    [Fact]
    public void TwoPartInterpKeepsConcat()
    {
        var module = CheckClean(InterpStubs + """
            kernel { entry func Main() {
                let int x = 1;
                let String s = $"a{x}";
            } }
            """);
        var calls = CollectEntry(module);
        Assert.DoesNotContain("StringBuilder", calls.News);
    }

    [Fact]
    public void NoBuilderBindingFallsBackToConcat()
    {
        var module = CheckClean("""
            @builtin(String)
            class String {
                int len;
                public operator String func +(String other) { return self; }
            }

            @intrinsic(stringify_int)
            String func IntToString(int n) { return new String(); }

            kernel { entry func Main() {
                let int x = 1;
                let String s = $"a{x}b{x}c";
            } }
            """);
        var calls = CollectEntry(module);
        Assert.Empty(calls.News.Where(n => n == "StringBuilder"));
    }

    #endregion
}
