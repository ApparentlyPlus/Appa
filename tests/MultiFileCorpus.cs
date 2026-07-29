namespace Appa.Tests;

/// <summary>
/// One multi-file torture case: Files written into a project directory, Expect saying whether the
/// build must be rejected or accepted, and Code the diagnostic required when it is. Entry and
/// environment are always src/main.g and env.g, the layout Pipeline discovers.
/// </summary>
public sealed record MultiFileCase(
    string Name,
    (string Path, string Content)[] Files,
    Expect Expect,
    string? Code = null)
{
    public override string ToString() => Name;
}

/// <summary>
/// The multi-file torture corpus. Everything else compiles a single source string, leaving import
/// resolution, per-file visibility, cross-file collisions, split realms and private-name mangling
/// untested - none of it reachable without files on disk.
/// </summary>
public static class MultiFileCorpus
{
    /// <summary>
    /// The environment every case shares unless it supplies its own. Declares a user realm so
    /// Layout emits a real translation unit, and provides the headers and ARC roles that a project
    /// with no libgata would otherwise lack.
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

    /// <summary>
    /// Every multi-file case.
    /// </summary>
    public static IReadOnlyList<MultiFileCase> All => _all ??= [.. Cases()];

    /// <summary>
    /// Shorthand for a file tuple, so the case list stays readable.
    /// </summary>
    private static (string, string) F(string path, string content) => (path, content);

    /// <summary>
    /// A minimal user realm with an entry point, for cases whose focus is elsewhere.
    /// </summary>
    private const string MainShell = "realm userspace {{ entry func Main() {{ {0} }} }}";

    /// <summary>
    /// Builds src/main.g with the given imports and body.
    /// </summary>
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
            F("src/lib.g", "realm userspace { entry func Main() { } }"),
            F("src/main.g", "import \"src/lib.g\";\n"),
        ], Expect.Any);

        yield return new("realm/two-user-blocks-two-files",
        [
            F("src/lib.g", "realm userspace { void func Helper() { } }"),
            F("src/main.g", Main("import \"src/lib.g\";", "")),
        ], Expect.Any);

        yield return new("realm/two-entries-two-files",
        [
            F("src/lib.g", "realm userspace { entry func Second() { } }"),
            F("src/main.g", Main("import \"src/lib.g\";", "")),
        ], Expect.Rejected);

        yield return new("realm/kernel-and-user-split",
        [
            F("src/k.g", "realm kernel { entry func KMain() { } }"),
            F("src/main.g", Main("import \"src/k.g\";", "")),
        ], Expect.Any);

        // A realm may take a name an import already gave the file, but only by saying so. Marked,
        // the inner one wins and the build is clean; unmarked, it is a hard error.
        yield return new("realm/shadows-imported-class",
        [
            F("src/lib.g", "class Widget { public int n; }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { @shadows class Widget { public int m; } " +
                            "entry func Main() { let Widget w = new Widget(); let int v = w.m; } }\n"),
        ], Expect.Accepted);

        // '::' reaches the imported name a realm displaced, across the file boundary that made the
        // displacement invisible in the first place.
        yield return new("realm/qualifier-reaches-an-import",
        [
            F("src/lib.g", "class Widget { public int n; }\npublic int func Helper() { return 1; }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { @shadows class Widget { public int m; } " +
                            "@shadows int func Helper() { return 2; } " +
                            "entry func Main() { let ::Widget w = new ::Widget(); " +
                            "let int v = w.n + ::Helper() + Helper(); } }\n"),
        ], Expect.Accepted);

        yield return new("realm/qualifier-into-a-sibling-file-realm",
        [
            F("src/lib.g", "realm kernel { class Cfg { public int a; } }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { " +
                            "void func F() { let kernel.Cfg c; } entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.ScopeNotEnclosing);

        yield return new("realm/shadows-imported-class-unmarked",
        [
            F("src/lib.g", "class Widget { public int n; }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { class Widget { public int m; } " +
                            "entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("realm/shadows-imported-func",
        [
            F("src/lib.g", "public int func Helper() { return 1; }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { @shadows int func Helper() { return 2; } " +
                            "entry func Main() { let int v = Helper(); } }\n"),
        ], Expect.Accepted);

        yield return new("realm/shadows-imported-func-unmarked",
        [
            F("src/lib.g", "public int func Helper() { return 1; }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { int func Helper() { return 2; } " +
                            "entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.UnmarkedShadow);

        // A name in a file nothing imports is not a name that already means something here, so
        // marking it is the mistake rather than omitting the mark.
        yield return new("realm/shadows-unimported-name",
        [
            F("src/lib.g", "class Widget { public int n; }"),
            F("src/main.g", "realm userspace { @shadows class Widget { public int m; } entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.UnmarkedShadow);

        // A realm split across two files is one namespace, so a process shadows what the other file
        // declared into the same realm - which only holds because declaring runs to completion first.
        yield return new("realm/process-shadows-realm-from-another-file",
        [
            F("src/lib.g", "realm userspace { class Frame { public int outer; } }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { foreground process App { " +
                            "class Frame { public int inner; } thread T { entry func R() { } } } " +
                            "entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("realm/no-entry-anywhere",
        [
            F("src/lib.g", "public int func Helper() { return 1; }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { void func NotEntry() { } }\n"),
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

        // A managed union declared in one file and used from another. Its retain/release pair
        // lives in the shared header, so every unit must agree on one definition - and the suite
        // links all units, so a per-file copy shows up as a duplicate symbol.
        yield return new("cross/managed-union-declared-elsewhere",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/u.g", "import \"src/p.g\";\nunion Msg { Text(Payload p), Code(int n) }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/u.g\";",
                                 "let Msg m = Msg.Text(new Payload()); match (m) { case Text(p) { } case Code(n) { } }")),
        ], Expect.Accepted);

        // Constructed in one file, consumed in a third: the union's ARC pair has to be reachable
        // from every unit that owns one, not only from the one that declared the type.
        yield return new("cross/managed-union-three-files",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/u.g", "import \"src/p.g\";\nunion Msg { Text(Payload p), Code(int n) }"),
            F("src/mk.g", "import \"src/p.g\";\nimport \"src/u.g\";\npublic Msg func Make() { return Msg.Text(new Payload()); }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/u.g\";\nimport \"src/mk.g\";",
                                 "let Msg m = Make(); match (m) { case Text(p) { } case Code(n) { } }")),
        ], Expect.Accepted);

        // A managed union nested inside another, across files: release has to recurse into the
        // inner union's pair, which is only emitted if the outer file's dependency is seen.
        yield return new("cross/nested-managed-union",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/i.g", "import \"src/p.g\";\nunion Inner { A(Payload p), B }"),
            F("src/o.g", "import \"src/i.g\";\nunion Outer { W(Inner i), P(int n) }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/i.g\";\nimport \"src/o.g\";",
                                 "let Outer o = Outer.W(Inner.A(new Payload()));")),
        ], Expect.Accepted);

        // A managed union stored in a class field declared in another file: the class destructor
        // is generated in the class's own file and must call the union's release.
        yield return new("cross/managed-union-as-class-field",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/u.g", "import \"src/p.g\";\nunion Msg { Text(Payload p), Code(int n) }"),
            F("src/c.g", "import \"src/u.g\";\nclass Box { Msg m; public func _init(Msg m) { self.m = m; } }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/u.g\";\nimport \"src/c.g\";",
                                 "let Box b = new Box(Msg.Text(new Payload()));")),
        ], Expect.Accepted);

        // Union equality is generated into every translation unit. These pin that its definition
        // and uses stay consistent across files: the suite links all units, so a disagreement is
        // a duplicate or missing symbol rather than a quiet pass.
        yield return new("cross/union-compared-in-another-file",
        [
            F("src/u.g", "union Msg { Text(int n), Code(int c) }"),
            F("src/main.g", Main("import \"src/u.g\";",
                                 "let bool b = Msg.Text(1) == Msg.Code(1);")),
        ], Expect.Accepted);

        // Compared from two different files: each unit defines the equality it uses.
        yield return new("cross/union-compared-in-two-files",
        [
            F("src/u.g", "union Msg { Text(int n), Code(int c) }"),
            F("src/a.g", "import \"src/u.g\";\npublic bool func SameA(Msg x, Msg y) { return x == y; }"),
            F("src/b.g", "import \"src/u.g\";\npublic bool func SameB(Msg x, Msg y) { return x != y; }"),
            F("src/main.g", Main("import \"src/u.g\";\nimport \"src/a.g\";\nimport \"src/b.g\";",
                                 "let bool b = SameA(Msg.Text(1), Msg.Code(1)) || SameB(Msg.Text(1), Msg.Code(1));")),
        ], Expect.Accepted);

        // The payload's '==' operator lives in a third file. The generated equality has to call
        // it, which means that operator must be declared before the equality body in every unit
        // that emits one.
        yield return new("cross/union-payload-equality-operator-elsewhere",
        [
            F("src/v.g", "class Valued { public int n; public operator bool func ==(Valued o) { return self.n == o.n; } }"),
            F("src/u.g", "import \"src/v.g\";\nunion Msg { V(Valued v), Code(int c) }"),
            F("src/main.g", Main("import \"src/v.g\";\nimport \"src/u.g\";",
                                 "let bool b = Msg.Code(1) == Msg.Code(2);")),
        ], Expect.Accepted);

        // A nested managed union compared across files: equality recurses into the inner union's
        // generated function, and ARC recurses into its release.
        yield return new("cross/nested-union-compared",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/i.g", "import \"src/p.g\";\nunion Inner { A(Payload p), B(int n) }"),
            F("src/o.g", "import \"src/i.g\";\nunion Outer { W(Inner i), K(int n) }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/i.g\";\nimport \"src/o.g\";",
                                 "let bool b = Outer.K(1) == Outer.W(Inner.B(2));")),
        ], Expect.Accepted);

        // One generic reaching for another, over a type from a third file. Outer[Res] is asked
        // for in main.g but the Inner[T] use sits in Outer's file, so Inner[Res] was attributed
        // there - which never heard of Res. Map.Values() hit this, breaking Map[int, UserClass].
        yield return new("cross/generic-reaching-for-generic",
        [
            F("src/g.g", "class Inner[T] { public T v; }\n" +
                         "class Outer[T] { public T item; " +
                         "public Inner[T] func Wrap() { let Inner[T] i = new Inner[T](); i.v = self.item; return i; } }"),
            F("src/main.g", Main("import \"src/g.g\";\nimport \"src/r.g\";",
                                 "let Outer[Res] o = new Outer[Res](); o.item = new Res(); let Res r = o.Wrap().v;")),
            F("src/r.g", "class Res { public int id; }"),
        ], Expect.Accepted);

        // Three levels deep: the requester has to propagate along the whole chain, not one hop.
        yield return new("cross/generic-chain-three-deep",
        [
            F("src/g.g", "class A[T] { public T v; }\n" +
                         "class B[T] { public A[T] a; }\n" +
                         "class C[T] { public B[T] b; }"),
            F("src/main.g", Main("import \"src/g.g\";\nimport \"src/r.g\";", "let C[Res] c = new C[Res]();")),
            F("src/r.g", "class Res { public int id; }"),
        ], Expect.Accepted);

        // A generic union template declared in one file and instantiated from others. Stamped
        // instances are spliced into the template's file, so each must resolve its arguments
        // under the file that named them, and different arguments must give separate unions.
        yield return new("cross/generic-union-instantiated-elsewhere",
        [
            F("src/m.g", "union Maybe[V] { Found(V v), Missing }"),
            F("src/main.g", Main("import \"src/m.g\";",
                                 "let Maybe[int] a = Maybe.Found(1); let Maybe[bool] b = Maybe.Found(true);")),
        ], Expect.Accepted);

        yield return new("cross/generic-union-over-a-class-from-another-file",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/m.g", "union Maybe[V] { Found(V v), Missing }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/m.g\";",
                                 "let Maybe[Payload] a = Maybe.Found(new Payload());")),
        ], Expect.Accepted);

        // Instantiated from two different files over two different arguments.
        yield return new("cross/generic-union-two-requesters",
        [
            F("src/m.g", "union Maybe[V] { Found(V v), Missing }"),
            F("src/a.g", "import \"src/m.g\";\npublic Maybe[int] func MakeInt() { return Maybe.Found(1); }"),
            F("src/b.g", "import \"src/m.g\";\npublic Maybe[bool] func MakeBool() { return Maybe.Found(true); }"),
            F("src/main.g", Main("import \"src/m.g\";\nimport \"src/a.g\";\nimport \"src/b.g\";",
                                 "let Maybe[int] x = MakeInt(); let Maybe[bool] y = MakeBool();")),
        ], Expect.Accepted);

        // A generic union reaching for a generic class through its own parameter, across files.
        yield return new("cross/generic-union-holding-a-generic-class",
        [
            F("src/c.g", "class Bag[T] { public T item; }"),
            F("src/m.g", "import \"src/c.g\";\nunion Holder[V] { Some(Bag[V] b), None }"),
            F("src/main.g", Main("import \"src/c.g\";\nimport \"src/m.g\";",
                                 "let Holder[int] h = Holder.None();")),
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
    /// A second batch covering what the first did not reach: collisions between the kinds of type
    /// declaration, private-function mangling across many files, processes declared away from the
    /// entry, generics instantiated from several files, and import-path shapes.
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
            F("src/p.g", "realm kernel { foreground process Worker { thread T { entry func Run() { } } } }"),
            F("src/main.g", "import \"src/p.g\";\nrealm kernel { entry func Main() { } }\n"),
        ], Expect.Any);

        yield return new("proc/same-name-two-files",
        [
            F("src/a.g", "realm kernel { foreground process Worker { thread T { entry func Run() { } } } }"),
            F("src/b.g", "realm kernel { foreground process Worker { thread U { entry func Run() { } } } }"),
            F("src/main.g", "import \"src/a.g\";\nimport \"src/b.g\";\nrealm kernel { entry func Main() { } }\n"),
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
