namespace Appa.Tests;

/// <summary>
/// One multi-file torture case: a set of files written into a project directory, the file the
/// build starts from, and what the compiler is expected to do with it.
///
/// The entry is always "src/main.g" and the environment always "env.g", matching the layout
/// Pipeline discovers, so a case only has to spell out the files it actually cares about.
/// </summary>
/// <param name="Name">Identifier used in failure messages.</param>
/// <param name="Files">Project-relative path to file contents.</param>
/// <param name="Expect">Whether the build must be rejected, accepted, or is unconstrained.</param>
/// <param name="Code">Optional exact diagnostic code required when Expect is Rejected.</param>
public sealed record MultiFileCase(
    string Name,
    (string Path, string Content)[] Files,
    Expect Expect,
    string? Code = null)
{
    public override string ToString() => Name;
}

/// <summary>
/// The multi-file torture corpus.
///
/// Everything else in the suite compiles a single source string, which leaves a whole tier of
/// the compiler untested: import resolution and cycles, per-file visibility, cross-file name
/// collisions, realm blocks split across files, and the mangling that has to keep two files'
/// same-named private functions apart. None of that is reachable without real files on disk,
/// because Pipeline.Transpile follows imports by reading them.
/// </summary>
public static class MultiFileCorpus
{
    /// <summary>
    /// The environment every case shares unless it supplies its own. Declares a user realm so
    /// Layout emits a real translation unit, and provides the headers and ARC roles that a
    /// project with no libgata would otherwise lack.
    /// </summary>
    public const string DefaultEnv = """
        @environment

        @preamble(user) native {
        #include <stdint.h>
        #include <stddef.h>
        #include <stdbool.h>
        #include <stdlib.h>
        #include "shared.h"
        }

        @intrinsic(obj_header)
        native type obj { gata_Fn_void__void_p __dtor; size_t __rc; }

        @intrinsic(alloc)
        void* func gmalloc(usize n) native { return calloc(1, (size_t)n); }

        @intrinsic(retain)
        void* func gretain(void* p) native { if (p) ((gata_obj*)p)->__rc++; return p; }

        @intrinsic(release)
        void func grelease(void* p) native {
            if (!p) return;
            gata_obj* o = (gata_obj*)p;
            if (--o->__rc == 0) { if (o->__dtor) o->__dtor(p); free(p); }
        }

        @intrinsic(obj_init)
        void func gobjinit(void* o, func(void*) -> void dtor) native {
            gata_obj* x = (gata_obj*)o; x->__rc = 1; x->__dtor = dtor;
        }
        """;

    private static IReadOnlyList<MultiFileCase>? _all;

    /// <summary>Every multi-file case.</summary>
    public static IReadOnlyList<MultiFileCase> All => _all ??= [.. Cases()];

    /// <summary>Shorthand for a file tuple, so the case list stays readable.</summary>
    private static (string, string) F(string path, string content) => (path, content);

    /// <summary>A minimal user realm with an entry point, for cases whose focus is elsewhere.</summary>
    private const string MainShell = "user {{ entry func Main() {{ {0} }} }}";

    /// <summary>Builds src/main.g with the given imports and body.</summary>
    private static string Main(string imports, string body) =>
        imports + "\n" + string.Format(MainShell, body) + "\n";

    private static IEnumerable<MultiFileCase> Cases()
    {
        #region import resolution
        yield return new("import/basic",
        [
            F("src/lib.g", "public int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib.g\";", "let int v = Helper();")),
        ], Expect.Accepted);

        yield return new("import/missing-file",
        [
            F("src/main.g", Main("import \"src/nope.g\";", "")),
        ], Expect.Rejected, Codes.File);

        yield return new("import/missing-library",
        [
            F("src/main.g", Main("import NoSuchModule;", "")),
        ], Expect.Rejected, Codes.File);

        yield return new("import/self",
        [
            F("src/main.g", Main("import \"src/main.g\";", "")),
        ], Expect.Any);

        yield return new("import/cycle-two",
        [
            F("src/a.g", "import \"src/b.g\";\npublic int func A() { return B(); }"),
            F("src/b.g", "import \"src/a.g\";\npublic int func B() { return 1; }"),
            F("src/main.g", Main("import \"src/a.g\";", "let int v = A();")),
        ], Expect.Any);

        yield return new("import/cycle-three",
        [
            F("src/a.g", "import \"src/b.g\";\npublic int func A() { return 1; }"),
            F("src/b.g", "import \"src/c.g\";\npublic int func B() { return 2; }"),
            F("src/c.g", "import \"src/a.g\";\npublic int func C() { return 3; }"),
            F("src/main.g", Main("import \"src/a.g\";", "let int v = A();")),
        ], Expect.Any);

        yield return new("import/duplicate",
        [
            F("src/lib.g", "public int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib.g\";\nimport \"src/lib.g\";", "let int v = Helper();")),
        ], Expect.Any);

        yield return new("import/diamond",
        [
            F("src/base.g", "public int func Base() { return 1; }"),
            F("src/left.g", "import \"src/base.g\";\npublic int func Left() { return Base(); }"),
            F("src/right.g", "import \"src/base.g\";\npublic int func Right() { return Base(); }"),
            F("src/main.g", Main("import \"src/left.g\";\nimport \"src/right.g\";",
                                 "let int v = Left() + Right();")),
        ], Expect.Accepted);

        yield return new("import/parent-escape",
        [
            F("src/main.g", Main("import \"../outside.g\";", "")),
        ], Expect.Rejected);

        yield return new("import/directory-not-file",
        [
            F("src/main.g", Main("import \"src\";", "")),
        ], Expect.Rejected);

        #endregion

        #region visibility across files
        yield return new("visible/transitive",
        [
            F("src/deep.g", "public int func Deep() { return 1; }"),
            F("src/mid.g", "import \"src/deep.g\";\npublic int func Mid() { return Deep(); }"),
            F("src/main.g", Main("import \"src/mid.g\";", "let int v = Deep();")),
        ], Expect.Accepted);

        yield return new("visible/not-imported",
        [
            F("src/lib.g", "public int func Helper() { return 7; }"),
            F("src/other.g", "public int func Other() { return 1; }"),
            F("src/main.g", Main("import \"src/lib.g\";", "let int v = Other();")),
        ], Expect.Any);

        yield return new("visible/class-not-imported",
        [
            F("src/lib.g", "class Widget { public int n; }"),
            F("src/main.g", Main("", "let Widget w = new Widget();")),
        ], Expect.Rejected);

        yield return new("visible/private-not-visible",
        [
            F("src/lib.g", "private int func Secret() { return 1; }"),
            F("src/main.g", Main("import \"src/lib.g\";", "let int v = Secret();")),
        ], Expect.Rejected);

        yield return new("visible/private-same-name-two-files",
        [
            F("src/a.g", "private int func Shared() { return 1; }\npublic int func A() { return Shared(); }"),
            F("src/b.g", "private int func Shared() { return 2; }\npublic int func B() { return Shared(); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = A() + B();")),
        ], Expect.Accepted);

        yield return new("visible/private-class-member-cross-file",
        [
            F("src/lib.g", "class Widget { int hidden; }"),
            F("src/main.g", Main("import \"src/lib.g\";",
                                 "let Widget w = new Widget(); let int v = w.hidden;")),
        ], Expect.Rejected, Codes.PrivateMember);

        #endregion

        #region duplicate top-level names across files
        yield return new("dup/class-two-files",
        [
            F("src/a.g", "class Widget { public int n; }"),
            F("src/b.g", "class Widget { public int n; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let Widget w = new Widget();")),
        ], Expect.Rejected, Codes.DuplicateName);

        yield return new("dup/public-func-two-files",
        [
            F("src/a.g", "public int func Helper() { return 1; }"),
            F("src/b.g", "public int func Helper() { return 2; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = Helper();")),
        ], Expect.Rejected, Codes.DuplicateName);

        yield return new("dup/enum-two-files",
        [
            F("src/a.g", "enum Color { Red }"),
            F("src/b.g", "enum Color { Blue }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected);

        yield return new("dup/union-two-files",
        [
            F("src/a.g", "union Shape { Circle }"),
            F("src/b.g", "union Shape { Square }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected);

        yield return new("dup/module-two-files",
        [
            F("src/a.g", "module Util { public static int func F() { return 1; } }"),
            F("src/b.g", "module Util { public static int func G() { return 2; } }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected);

        yield return new("dup/enum-member-across-enums",
        [
            F("src/a.g", "enum Color { Red }"),
            F("src/b.g", "enum Mood { Red }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let Color c = Color.Red;")),
        ], Expect.Any);

        yield return new("dup/class-and-func-same-name",
        [
            F("src/a.g", "class Thing { public int n; }"),
            F("src/b.g", "public int func Thing() { return 1; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Any);

        yield return new("dup/unimported-same-name-is-fine",
        [
            // Neither file imports the other, and main imports only one, so the two
            // declarations never share a scope.
            F("src/a.g", "class Widget { public int n; }"),
            F("src/b.g", "class Widget { public int n; }"),
            F("src/main.g", Main("import \"src/a.g\";", "let Widget w = new Widget();")),
        ], Expect.Any);

        #endregion

        #region realm blocks split across files
        yield return new("realm/entry-in-imported-file",
        [
            F("src/lib.g", "user { entry func Main() { } }"),
            F("src/main.g", "import \"src/lib.g\";\n"),
        ], Expect.Any);

        yield return new("realm/two-user-blocks-two-files",
        [
            F("src/lib.g", "user { void func Helper() { } }"),
            F("src/main.g", Main("import \"src/lib.g\";", "")),
        ], Expect.Any);

        yield return new("realm/two-entries-two-files",
        [
            F("src/lib.g", "user { entry func Second() { } }"),
            F("src/main.g", Main("import \"src/lib.g\";", "")),
        ], Expect.Rejected);

        yield return new("realm/kernel-and-user-split",
        [
            F("src/k.g", "kernel { entry func KMain() { } }"),
            F("src/main.g", Main("import \"src/k.g\";", "")),
        ], Expect.Any);

        yield return new("realm/no-entry-anywhere",
        [
            F("src/lib.g", "public int func Helper() { return 1; }"),
            F("src/main.g", "import \"src/lib.g\";\nuser { void func NotEntry() { } }\n"),
        ], Expect.Rejected);

        #endregion

        #region environment declarations
        yield return new("env/second-environment-file",
        [
            F("src/extra.g", "@environment\n"),
            F("src/main.g", Main("import \"src/extra.g\";", "")),
        ], Expect.Rejected);

        yield return new("env/none",
        [
            F("env.g", "@preamble(user) native { }\n"),
            F("src/main.g", Main("", "")),
        ], Expect.Rejected);

        #endregion

        #region cross-file type and generic use
        yield return new("cross/generic-class",
        [
            F("src/box.g", "class Box[T] { public T v; }"),
            F("src/main.g", Main("import \"src/box.g\";",
                                 "let Box[int] b = new Box[int](); b.v = 1;")),
        ], Expect.Accepted);

        yield return new("cross/generic-instantiated-in-two-files",
        [
            F("src/box.g", "class Box[T] { public T v; }"),
            F("src/a.g", "import \"src/box.g\";\npublic int func A() { let Box[int] b = new Box[int](); return b.v; }"),
            F("src/b.g", "import \"src/box.g\";\npublic int func B() { let Box[int] b = new Box[int](); return b.v; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = A() + B();")),
        ], Expect.Accepted);

        yield return new("cross/enum-used-in-other-file",
        [
            F("src/e.g", "enum Color { Red, Green }"),
            F("src/main.g", Main("import \"src/e.g\";",
                                 "let Color c = Color.Green; switch (c) { case Color.Red { } default { } }")),
        ], Expect.Any);

        yield return new("cross/union-matched-in-other-file",
        [
            F("src/u.g", "union Shape { Circle(int r), Square }"),
            F("src/main.g", Main("import \"src/u.g\";",
                                 "let Shape s = Shape.Circle(2); match (s) { case Circle(r) { } case Square { } }")),
        ], Expect.Accepted);

        yield return new("cross/operator-overload",
        [
            F("src/v.g", "class Vec { public int n; public operator Vec func +(Vec o) { return o; } }"),
            F("src/main.g", Main("import \"src/v.g\";",
                                 "let Vec a = new Vec(); let Vec b = a + a;")),
        ], Expect.Accepted);

        yield return new("cross/throws-function",
        [
            F("src/r.g", "public throws int func Risky() { throw; }"),
            F("src/main.g", Main("import \"src/r.g\";",
                                 "let int v = Risky() catch { assign 0; };")),
        ], Expect.Accepted);

        yield return new("cross/fixed-array-type",
        [
            F("src/a.g", "public [4]int func Make() { return [1, 2, 3, 4]; }"),
            F("src/main.g", Main("import \"src/a.g\";", "let [4]int a = Make();")),
        ], Expect.Accepted);

        yield return new("cross/func-pointer-type",
        [
            F("src/a.g", "public int func Twice(int n) { return n * 2; }"),
            F("src/main.g", Main("import \"src/a.g\";",
                                 "let func(int) -> int f = Twice; let int v = f(2);")),
        ], Expect.Accepted);

        #endregion

        #region native and annotation placement across files
        yield return new("native/block-in-imported-file",
        [
            F("src/n.g", "native { static int shared_counter = 0; }"),
            F("src/main.g", Main("import \"src/n.g\";", "")),
        ], Expect.Any);

        yield return new("native/duplicate-intrinsic-role",
        [
            F("src/dup.g", "@intrinsic(alloc)\nvoid* func other(usize n) native { return 0; }"),
            F("src/main.g", Main("import \"src/dup.g\";", "")),
        ], Expect.Rejected, Codes.DuplicateIntrinsic);

        yield return new("native/extern-in-imported-file",
        [
            F("src/e.g", "@extern void func puts(char* s);"),
            F("src/main.g", Main("import \"src/e.g\";", "")),
        ], Expect.Any);

        #endregion

        #region file-level edge cases
        yield return new("file/empty-imported",
        [
            F("src/empty.g", ""),
            F("src/main.g", Main("import \"src/empty.g\";", "")),
        ], Expect.Any);

        yield return new("file/imported-has-syntax-error",
        [
            F("src/bad.g", "class { }"),
            F("src/main.g", Main("import \"src/bad.g\";", "")),
        ], Expect.Rejected);

        yield return new("file/deep-chain",
        [
            F("src/l1.g", "import \"src/l2.g\";\npublic int func L1() { return L2(); }"),
            F("src/l2.g", "import \"src/l3.g\";\npublic int func L2() { return L3(); }"),
            F("src/l3.g", "import \"src/l4.g\";\npublic int func L3() { return L4(); }"),
            F("src/l4.g", "public int func L4() { return 4; }"),
            F("src/main.g", Main("import \"src/l1.g\";", "let int v = L1();")),
        ], Expect.Accepted);

        yield return new("file/same-file-two-paths",
        [
            // "src/lib.g" and "src/./lib.g" name one file; Transpile canonicalises before
            // deduplicating, so it must be parsed once, not twice into duplicate symbols.
            F("src/lib.g", "public int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib.g\";\nimport \"src/./lib.g\";", "let int v = Helper();")),
        ], Expect.Any);

        foreach (var c in MoreCases()) yield return c;
    }

    /// <summary>
    /// A second batch, covering what the first pass did not reach: collisions between the
    /// different kinds of type declaration, private-function mangling across many files,
    /// processes and threads declared away from the entry, generics instantiated from several
    /// files at once, and the shapes an import path can take.
    /// </summary>
    private static IEnumerable<MultiFileCase> MoreCases()
    {

        #endregion

        #region collisions between different kinds of declaration
        yield return new("kind/class-vs-enum",
        [
            F("src/a.g", "class Color { public int n; }"),
            F("src/b.g", "enum Color { Red }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected, Codes.DuplicateName);

        yield return new("kind/class-vs-union",
        [
            F("src/a.g", "class Shape { public int n; }"),
            F("src/b.g", "union Shape { Circle }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected, Codes.DuplicateName);

        yield return new("kind/enum-vs-union",
        [
            F("src/a.g", "enum Tag { One }"),
            F("src/b.g", "union Tag { Two }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected, Codes.DuplicateName);

        yield return new("kind/class-vs-module",
        [
            F("src/a.g", "class Util { public int n; }"),
            F("src/b.g", "module Util { public static int func F() { return 1; } }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected, Codes.DuplicateName);

        yield return new("kind/enum-vs-native-type",
        [
            F("src/a.g", "enum Handle { None }"),
            F("src/b.g", "native type Handle { int raw; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected, Codes.DuplicateName);

        // Types and free functions are separate namespaces in Gata, and the emitter keeps
        // them apart in C too, so this is legal rather than a collision.
        yield return new("kind/enum-vs-free-func",
        [
            F("src/a.g", "enum Color { Red }"),
            F("src/b.g", "public int func Color() { return 1; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Any);

        #endregion

        #region private free-function mangling across files
        yield return new("private/three-files-same-name",
        [
            F("src/a.g", "private int func Shared() { return 1; }\npublic int func A() { return Shared(); }"),
            F("src/b.g", "private int func Shared() { return 2; }\npublic int func B() { return Shared(); }"),
            F("src/c.g", "private int func Shared() { return 3; }\npublic int func C() { return Shared(); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";\nimport \"src/c.g\";",
                                 "let int v = A() + B() + C();")),
        ], Expect.Accepted);

        yield return new("private/shadows-public-in-other-file",
        [
            F("src/a.g", "public int func Helper() { return 1; }"),
            F("src/b.g", "private int func Helper() { return 2; }\npublic int func B() { return Helper(); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = B();")),
        ], Expect.Any);

        yield return new("private/overloads-in-two-files",
        [
            F("src/a.g", "private int func P(int n) { return n; }\nprivate int func P(bool b) { return 1; }\npublic int func A() { return P(1) + P(true); }"),
            F("src/b.g", "private int func P(int n) { return n * 2; }\npublic int func B() { return P(2); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = A() + B();")),
        ], Expect.Accepted);

        yield return new("private/class-in-two-files",
        [
            // Classes are not file-scoped the way private free functions are, so two files
            // declaring the same class name collide however they are marked.
            F("src/a.g", "class Widget { public int n; }"),
            F("src/b.g", "class Widget { public int m; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected, Codes.DuplicateName);

        #endregion

        #region processes and threads declared away from the entry
        yield return new("proc/in-imported-file",
        [
            F("src/p.g", "kernel { foreground process Worker { thread T { entry func Run() { } } } }"),
            F("src/main.g", "import \"src/p.g\";\nkernel { entry func Main() { } }\n"),
        ], Expect.Any);

        yield return new("proc/same-name-two-files",
        [
            F("src/a.g", "kernel { foreground process Worker { thread T { entry func Run() { } } } }"),
            F("src/b.g", "kernel { foreground process Worker { thread U { entry func Run() { } } } }"),
            F("src/main.g", "import \"src/a.g\";\nimport \"src/b.g\";\nkernel { entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.DuplicateName);

        #endregion

        #region generics instantiated from several files
        yield return new("gen/three-files-same-instantiation",
        [
            F("src/box.g", "class Box[T] { public T v; }"),
            F("src/a.g", "import \"src/box.g\";\npublic int func A() { let Box[int] b = new Box[int](); return b.v; }"),
            F("src/b.g", "import \"src/box.g\";\npublic int func B() { let Box[int] b = new Box[int](); return b.v; }"),
            F("src/c.g", "import \"src/box.g\";\npublic int func C() { let Box[int] b = new Box[int](); return b.v; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";\nimport \"src/c.g\";",
                                 "let int v = A() + B() + C();")),
        ], Expect.Accepted);

        yield return new("gen/instantiated-with-type-from-third-file",
        [
            F("src/box.g", "class Box[T] { public T v; }"),
            F("src/w.g", "class Widget { public int n; }"),
            F("src/main.g", Main("import \"src/box.g\";\nimport \"src/w.g\";",
                                 "let Box[Widget] b = new Box[Widget]();")),
        ], Expect.Accepted);

        yield return new("gen/generic-func-called-from-two-files",
        [
            F("src/id.g", "public T func Id[T](T v) { return v; }"),
            F("src/a.g", "import \"src/id.g\";\npublic int func A() { return Id(1); }"),
            F("src/b.g", "import \"src/id.g\";\npublic int func B() { return Id(2); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = A() + B();")),
        ], Expect.Accepted);

        yield return new("gen/same-name-generic-two-files",
        [
            F("src/a.g", "class Holder[T] { public T v; }"),
            F("src/b.g", "class Holder[T] { public T v; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Rejected, Codes.DuplicateName);

        yield return new("gen/same-name-generic-both-instantiated",
        [
            F("src/a.g", "class Holder[T] { public T v; }\npublic int func A() { let Holder[int] h = new Holder[int](); return h.v; }"),
            F("src/b.g", "class Holder[T] { public T v; }\npublic int func B() { let Holder[int] h = new Holder[int](); return h.v; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = A() + B();")),
        ], Expect.Rejected, Codes.DuplicateName);

        #endregion

        #region intrinsics and builtins across files
        yield return new("intrinsic/two-builtin-string",
        [
            F("src/s.g", "@builtin(String)\nclass MyString { public int n; }"),
            F("src/main.g", Main("import \"src/s.g\";", "")),
        ], Expect.Any);

        yield return new("intrinsic/role-bound-in-imported-file",
        [
            F("src/r.g", "@intrinsic(env_time)\nint64 func now() native { return 0; }"),
            F("src/main.g", Main("import \"src/r.g\";", "")),
        ], Expect.Any);

        #endregion

        #region import path shapes
        yield return new("path/dot-slash",
        [
            F("src/lib.g", "public int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"./src/lib.g\";", "let int v = Helper();")),
        ], Expect.Any);

        yield return new("path/redundant-segments",
        [
            F("src/lib.g", "public int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/../src/lib.g\";", "let int v = Helper();")),
        ], Expect.Any);

        yield return new("path/wrong-case",
        [
            // On a case-sensitive filesystem this names a file that does not exist; on a
            // case-insensitive one it names src/lib.g. Either outcome is fine, but it must
            // not be silently treated as a *different* module from the one already loaded.
            F("src/lib.g", "public int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib.g\";\nimport \"src/LIB.g\";", "let int v = Helper();")),
        ], Expect.Any);

        yield return new("path/no-extension",
        [
            F("src/lib.g", "public int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib\";", "")),
        ], Expect.Rejected);

        yield return new("path/absolute-looking",
        [
            F("src/main.g", Main("import \"/nonexistent/lib.g\";", "")),
        ], Expect.Rejected);

        yield return new("path/back-edge-to-entry",
        [
            F("src/lib.g", "import \"src/main.g\";\npublic int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib.g\";", "let int v = Helper();")),
        ], Expect.Any);

        #endregion

        #region fan-out and depth
        yield return new("scale/wide-fan-out",
        [
            F("src/m0.g", "public int func M0() { return 0; }"),
            F("src/m1.g", "public int func M1() { return 1; }"),
            F("src/m2.g", "public int func M2() { return 2; }"),
            F("src/m3.g", "public int func M3() { return 3; }"),
            F("src/m4.g", "public int func M4() { return 4; }"),
            F("src/m5.g", "public int func M5() { return 5; }"),
            F("src/m6.g", "public int func M6() { return 6; }"),
            F("src/m7.g", "public int func M7() { return 7; }"),
            F("src/main.g", Main(
                string.Join("\n", Enumerable.Range(0, 8).Select(i => $"import \"src/m{i}.g\";")),
                "let int v = " + string.Join(" + ", Enumerable.Range(0, 8).Select(i => $"M{i}()")) + ";")),
        ], Expect.Accepted);

        yield return new("scale/mutual-pair-with-shared-dep",
        [
            F("src/dep.g", "public int func Dep() { return 1; }"),
            F("src/a.g", "import \"src/b.g\";\nimport \"src/dep.g\";\npublic int func A() { return Dep(); }"),
            F("src/b.g", "import \"src/a.g\";\nimport \"src/dep.g\";\npublic int func B() { return Dep(); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = A() + B();")),
        ], Expect.Accepted);

        #endregion
    }
}
