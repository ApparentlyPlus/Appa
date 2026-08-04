namespace Appa.Tests;

/// <summary>
/// One compilation must not depend on what was compiled before it.
/// </summary>
public class CompilationIndependenceTests
{
    private const string StubEnvironment = """
        @preamble(kernel) native {
        #include <stdint.h>
        #include <stddef.h>
        #include <stdbool.h>
        #include "shared.h"
        typedef struct gata_String gata_String;
        static void* gata_MISSING_retain(void* p) { return p; }
        static void gata_MISSING_release(void* p) { (void)p; }
        }

        """;

    /// <summary>
    /// Programs that each move some piece of the compiler's static bookkeeping: dense names,
    /// generic instance display names, scope display names, and the array/func-pointer registries.
    /// </summary>
    private static readonly (string Name, string Source)[] Programs =
    [
        ("dense-names",
            "int func A() { return 1; } int func B() { return A(); } int func C() { return B(); } " +
            "realm kernel { entry func Main() { let int v = C(); } }"),

        ("generics",
            "class Box[T] { public T v; func _init(T x) { self.v = x; } } " +
            "T func Echo[T](T x) { return x; } " +
            "realm kernel { entry func Main() { let Box[int] b = new Box[int](Echo(1)); " +
            "let Box[bool] c = new Box[bool](Echo(true)); } }"),

        ("scopes-and-shadows",
            "int func Step() { return 1; } " +
            "realm kernel { @shadows int func Step() { return 2; } " +
            "foreground process P { @shadows int func Step() { return 3; } " +
            "thread T { entry func R() { let int z = Step() + kernel.Step() + ::Step(); } } } " +
            "entry func Main() { } }"),

        ("arrays-and-func-pointers",
            "int func Add(int a, int b) { return a + b; } " +
            "realm kernel { entry func Main() { let [4]int xs = default([4]int); " +
            "let func(int, int) -> int f = Add; let int v = f(xs[0], xs[1]); } }"),

        ("registers-a-generic-display-name",
            "class Box[T] { public T v; } class Widget { public int n; } " +
            "realm kernel { entry func Main() { let Box[Widget] b = new Box[Widget](); let int q = b.v.n; } }"),

        ("unions-and-throws",
            "union U { None, One(int n) } throws int func T(int n) { if (n == 0) { throw; } return n; } " +
            "realm kernel { entry func Main() { let U u = U.One(1); " +
            "let int v = T(1) catch { assign 0; }; match (u) { case None { } case One(n) { } } } }"),
    ];

    /// <summary>
    /// Programs the compiler rejects or warns about, whose diagnostics name a declaration.
    /// </summary>
    private static readonly (string Name, string Source)[] Diagnosing =
    [
        ("undefined-type",
            "realm kernel { entry func Main() { let Widget w; } }"),
        ("wrong-argument",
            "class Widget { public int n; } int func Take(Widget w) { return w.n; } " +
            "realm kernel { entry func Main() { let int v = Take(1); } }"),
        ("generic-in-a-message",
            "class Box[T] { public T v; } class Widget { public int n; } " +
            "realm kernel { entry func Main() { let Box[Widget] b = new Box[Widget](); let int v = b.v; } }"),
        ("unknown-member",
            "class Widget { public int n; } " +
            "realm kernel { entry func Main() { let Widget w = new Widget(); let int v = w.missing; } }"),
        ("name-that-looks-mangled",
            "class Box_Widget { public int n; } " +
            "realm kernel { entry func Main() { let Box_Widget b = new Box_Widget(); let int v = b.missing; } }"),
    ];

    [Fact]
    public void DiagnosticsDoNotDependOnWhatWasCompiledBefore()
    {
        var mismatches = new List<string>();
        foreach (var (aName, aSrc) in Diagnosing)
        {
            var byPredecessor = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (bName, bSrc) in Programs.Concat(Diagnosing))
            {
                if (aName == bName) continue;
                Diagnose(bSrc);
                byPredecessor[bName] = Diagnose(aSrc);
            }
            var distinct = byPredecessor.Values.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count <= 1) continue;
            mismatches.Add($"'{aName}' diagnoses differently depending on what ran first:\n" +
                string.Join("\n", byPredecessor.Select(kv => $"    after '{kv.Key}': {kv.Value}")));
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} diagnostic(s) depend on what was compiled before them:\n" +
            string.Join("\n\n", mismatches.Take(3)));
    }

    /// <summary>
    /// The leak this class exists for, stated outright rather than as a difference: a message must
    /// name what the author wrote.
    /// </summary>
    [Fact]
    public void AnEarlierBuildsDisplayNamesDoNotReachThisBuildsMessages()
    {
        Emit("registers-a-generic-display-name", Find(Programs, "registers-a-generic-display-name"));
        string d = Diagnose(Find(Diagnosing, "name-that-looks-mangled"));

        Assert.Contains("Box_Widget", d, StringComparison.Ordinal);
        Assert.DoesNotContain("Box[Widget]", d, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the named program's source.
    /// </summary>
    private static string Find((string Name, string Source)[] set, string name) =>
        set.First(p => p.Name == name).Source;

    /// <summary>
    /// Runs the front end and returns every diagnostic as one string, tolerating rejection - these
    /// programs are meant to be rejected.
    /// </summary>
    private static string Diagnose(string source)
    {
        var (diag, _) = SingleFileCompile.Check(StubEnvironment + source);
        return string.Join(" | ", diag.All.Select(d => $"{d.Severity} {d.Code} {d.Message}"));
    }

    [Fact]
    public void CompilingSomethingElseFirstChangesNothing()
    {
        var mismatches = new List<string>();
        foreach (var (aName, aSrc) in Programs)
        {
            var byPredecessor = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (bName, bSrc) in Programs)
            {
                if (aName == bName) continue;
                Emit(bName, bSrc);
                byPredecessor[bName] = Emit(aName, aSrc);
            }
            var distinct = byPredecessor.Values.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count <= 1) continue;
            mismatches.Add($"'{aName}' is emitted differently depending on what ran first:\n" +
                           FirstDifference(distinct[0], distinct[1]));
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} program(s) depend on what was compiled before them:\n" +
            string.Join("\n\n", mismatches.Take(5)));
    }

    /// <summary>
    /// Runs the front end and returns the emitted files as one string. Fails the test rather than
    /// returning something empty if a program stops checking, since silence would make the
    /// comparison pass for the wrong reason.
    /// </summary>
    private static string Emit(string name, string source)
    {
        var (diag, module) = SingleFileCompile.Check(StubEnvironment + source);
        Assert.False(diag.HasErrors, $"'{name}' no longer checks: " +
            string.Join("; ", diag.All.Where(d => d.Severity == Severity.Error).Select(d => $"{d.Code} {d.Message}")));
        var files = Layout.Compose(new Emitter(module!, diag).Build(), module!.Symbols);
        return string.Join("\n", files.Select(f => $"===== {f.Name}\n{f.Content}"));
    }

    /// <summary>
    /// The first differing line of two emissions, with a little context, for the failure message.
    /// </summary>
    private static string FirstDifference(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            string x = i < la.Length ? la[i] : "<none>";
            string y = i < lb.Length ? lb[i] : "<none>";
            if (x != y) return $"  line {i + 1}:\n    first: {x}\n    again: {y}";
        }
        return "  (lengths differ with no differing line)";
    }
}
