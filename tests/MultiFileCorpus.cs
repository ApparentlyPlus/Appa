namespace Appa.Tests;

/// <summary>
/// One multi-file torture case: Files written into a project directory, Expect saying whether the
/// build must be rejected or accepted, and Code the diagnostic required when it is. Entry and
/// environment are always src/main.g and env.g, the layout Pipeline discovers.
/// </summary>
internal sealed record MultiFileCase(
    string Name,
    (string Path, string Content)[] Files,
    Expect Expect,
    string? Code = null,
    Target Target = Target.Hosted)
{
    public override string ToString() => Name;
}

/// <summary>
/// The multi-file torture corpus. Everything else compiles a single source string, leaving import
/// resolution, per-file visibility, cross-file collisions, split realms and private-name mangling
/// untested - none of it reachable without files on disk.
/// </summary>
internal static class MultiFileCorpus
{
    /// <summary>
    /// The environment every case shares unless it supplies its own. Declares a user realm so
    /// Layout emits a real translation unit, and provides the headers, ARC roles and topology
    /// floor that a project with no libgata would otherwise lack.
    /// </summary>
    public const string DefaultEnv = """
        @environment

        @preamble(user) native {
        #include <stdint.h>
        #include <stddef.h>
        #include <stdbool.h>
        #include <stdlib.h>
        #include "shared.h"
        int gata_argc = 0;
        char** gata_argv = 0;
        void* _env_proc_create(const char* name) { (void)name; return 0; }
        void _env_proc_hide(void* proc) { (void)proc; }
        void _env_thread_spawn(void* proc, const char* name, void (*entry)(void*), int is_user) {
            (void)proc; (void)name; (void)entry; (void)is_user;
        }
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

    /// <summary>
    /// The environment for cases built as GatOS rather than Hosted. Same roles as
    /// <see cref="DefaultEnv"/>, with a kernel preamble as well as a user one, since a GatOS build
    /// emits both translation units. Only a GatOS build allows more than one 'realm userspace { }'
    /// block, which is what makes a realm split across files reachable at all.
    /// </summary>
    public const string GatOSEnv = """
        @environment

        @preamble(kernel) native {
        #include <stdint.h>
        #include <stddef.h>
        #include <stdbool.h>
        #include <stdlib.h>
        #include "shared.h"
        void* _env_proc_create(const char* name) { (void)name; return 0; }
        void _env_proc_hide(void* proc) { (void)proc; }
        void _env_thread_spawn(void* proc, const char* name, void (*entry)(void*), int is_user) {
            (void)proc; (void)name; (void)entry; (void)is_user;
        }
        }

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

    /// <summary>
    /// Builds src/main.g for a GatOS case: the given declarations go into this file's half of the
    /// user realm, a thread calls 'Use()' so the emitter and DCE see them as live, and the kernel
    /// realm supplies the entry point a GatOS build requires. Every caller declares
    /// 'int func Use()'.
    /// </summary>
    private static string GatOS(string imports, string userItems) =>
        imports + "\nrealm userspace {\n" + userItems + "\n" +
        "background process P { thread T { entry func R() { let int q = Use(); } } }\n}\n" +
        "realm kernel { entry func Main() { } }\n";

    private static IEnumerable<MultiFileCase> Cases()
    {
        #region import resolution
        yield return new("import/basic",
        [
            F("src/lib.g", "int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib.g\";", "let int v = Helper();")),
        ], Expect.Accepted);

        yield return new("procvar/type-from-another-file",
        [
            F("src/lib.g", "class Cell { public int v; func _init() { self.v = 3; } }"),
            F("src/main.g",
                "import \"src/lib.g\";\n" +
                "realm userspace { entry func Main() { }\n" +
                "  background process P { let Cell c = new Cell();\n" +
                "    thread T { entry func R() { let int a = c.v; } } } }\n"),
        ], Expect.Accepted);

        yield return new("procvar/initialiser-calls-another-file",
        [
            F("src/lib.g", "int func Seed() { return 11; }"),
            F("src/main.g",
                "import \"src/lib.g\";\n" +
                "realm userspace { entry func Main() { }\n" +
                "  background process P { let int n = Seed() * 2;\n" +
                "    thread T { entry func R() { let int a = n; } } } }\n"),
        ], Expect.Accepted);

        yield return new("procvar/generic-over-another-files-class",
        [
            F("src/cell.g", "class Cell { public int v; func _init() { self.v = 4; } }"),
            F("src/box.g", "class Box[T] { public T v; func _init(T x) { self.v = x; } }"),
            F("src/main.g",
                "import \"src/cell.g\";\nimport \"src/box.g\";\n" +
                "realm userspace { entry func Main() { }\n" +
                "  background process P { let Box[Cell] b = new Box[Cell](new Cell());\n" +
                "    thread T { entry func R() { let int a = b.v.v; } } } }\n"),
        ], Expect.Accepted);

        yield return new("procvar/union-from-another-file",
        [
            F("src/lib.g", "union Shape { Dot, Line(int n) }"),
            F("src/main.g",
                "import \"src/lib.g\";\n" +
                "realm userspace { entry func Main() { }\n" +
                "  background process P { let Shape s = Shape.Line(5);\n" +
                "    thread T { entry func R() { match (s) { case Dot { } case Line(n) { } } } } } }\n"),
        ], Expect.Accepted);

        yield return new("procvar/funcptr-across-files",
        [
            F("src/lib.g", "int func Twice(int x) { return x * 2; }"),
            F("src/main.g",
                "import \"src/lib.g\";\n" +
                "realm userspace { entry func Main() { }\n" +
                "  background process P { let func(int) -> int f = Twice;\n" +
                "    thread T { entry func R() { let int a = f(3); } } } }\n"),
        ], Expect.Accepted);

        yield return new("procvar/initialiser-reads-later-one-across-files",
        [
            F("src/lib.g", "class Cell { public int v; func _init() { self.v = 3; } }"),
            F("src/main.g",
                "import \"src/lib.g\";\n" +
                "realm userspace { entry func Main() { }\n" +
                "  background process P { let int n = c.v; let Cell c = new Cell();\n" +
                "    thread T { entry func R() { let int a = n; } } } }\n"),
        ], Expect.Rejected, Codes.UseBeforeAssignment);

        yield return new("gen/free-function-over-callers-class",
        [
            F("src/lib.g", "T func Echo[T](T x) { return x; }"),
            F("src/main.g", "import \"src/lib.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { let Widget w = Echo(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/free-function-body-names-the-parameter",
        [
            F("src/lib.g", "T func Echo[T](T x) { let T y = x; return y; }"),
            F("src/main.g", "import \"src/lib.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { let Widget w = Echo(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/method-over-callers-class",
        [
            F("src/lib.g", "class Util { public T func Pick[T](T a) { return a; } }"),
            F("src/main.g", "import \"src/lib.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { let Util u = new Util(); " +
                            "let Widget w = u.Pick(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/free-function-over-a-third-files-class",
        [
            F("src/lib.g", "T func Echo[T](T x) { return x; }"),
            F("src/w.g", "class Widget { public int n; }"),
            F("src/main.g", "import \"src/lib.g\";\nimport \"src/w.g\";\n" +
                            "realm userspace { entry func Main() { let Widget w = Echo(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/free-function-over-callers-union",
        [
            F("src/lib.g", "T func Echo[T](T x) { return x; }"),
            F("src/main.g", "import \"src/lib.g\";\nunion Shape { Dot, Line(int n) }\n" +
                            "realm userspace { entry func Main() { let Shape s = Echo(Shape.Dot()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/free-function-over-callers-enum",
        [
            F("src/lib.g", "T func Echo[T](T x) { return x; }"),
            F("src/main.g", "import \"src/lib.g\";\nenum Col { Red, Blue }\n" +
                            "realm userspace { entry func Main() { let Col c = Echo(Col.Red); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/transitive-instantiation-two-libraries",
        [
            F("src/lib2.g", "T func Inner[T](T x) { return x; }"),
            F("src/lib.g", "import \"src/lib2.g\";\nT func Outer[T](T x) { return Inner(x); }"),
            F("src/main.g", "import \"src/lib.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { let Widget w = Outer(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/transitive-instantiation-three-deep",
        [
            F("src/l3.g", "T func L3[T](T x) { return x; }"),
            F("src/l2.g", "import \"src/l3.g\";\nT func L2[T](T x) { return L3(x); }"),
            F("src/l1.g", "import \"src/l2.g\";\nT func L1[T](T x) { return L2(x); }"),
            F("src/main.g", "import \"src/l1.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { let Widget w = L1(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/generic-class-body-calls-generic-function",
        [
            F("src/lib2.g", "T func Inner[T](T x) { return x; }"),
            F("src/box.g", "import \"src/lib2.g\";\n" +
                           "class Box[T] { public T v; func _init(T x) { self.v = Inner(x); } }"),
            F("src/main.g", "import \"src/box.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { " +
                            "let Box[Widget] b = new Box[Widget](new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/function-body-instantiates-a-generic-type-across-files",
        [
            F("src/box.g", "class Box[T] { public T v; func _init(T x) { self.v = x; } }"),
            F("src/lib.g", "import \"src/box.g\";\nT func Wrap[T](T x) { let Box[T] b = new Box[T](x); return b.v; }"),
            F("src/main.g", "import \"src/lib.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { let Widget w = Wrap(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/function-body-instantiation-two-rounds",
        [
            F("src/box.g", "class Box[T] { public T v; func _init(T x) { self.v = x; } }"),
            F("src/lib.g", "import \"src/box.g\";\n" +
                           "T func Inner[T](T x) { let Box[T] b = new Box[T](x); return b.v; }\n" +
                           "T func Outer[T](T x) { let Box[T] b = new Box[T](Inner(x)); return b.v; }"),
            F("src/main.g", "import \"src/lib.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { let Widget w = Outer(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/function-body-instantiates-a-nested-generic",
        [
            F("src/box.g", "class Box[T] { public T v; func _init(T x) { self.v = x; } }"),
            F("src/lib.g", "import \"src/box.g\";\n" +
                           "T func Wrap[T](T x) { let Box[Box[T]] b = new Box[Box[T]](new Box[T](x)); return b.v.v; }"),
            F("src/main.g", "import \"src/lib.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { let Widget w = Wrap(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/function-body-instantiation-seeded-concretely",
        [
            F("src/box.g", "class Box[T] { public T v; func _init(T x) { self.v = x; } }"),
            F("src/lib.g", "import \"src/box.g\";\nT func Wrap[T](T x) { let Box[T] b = new Box[T](x); return b.v; }"),
            F("src/main.g", "import \"src/lib.g\";\nimport \"src/box.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { " +
                            "let Box[Widget] seed = new Box[Widget](new Widget()); " +
                            "let Widget w = Wrap(new Widget()); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/generic-union-managed-payload-across-files",
        [
            F("src/w.g", "class Widget { public int n; }"),
            F("src/u.g", "union Wrap[T] { None, Some(T t) }"),
            F("src/main.g", "import \"src/w.g\";\nimport \"src/u.g\";\n" +
                            "realm userspace { entry func Main() { " +
                            "let Wrap[Widget] w = Wrap[Widget].Some(new Widget()); " +
                            "match (w) { case None { } case Some(t) { } } } }\n"),
        ], Expect.Accepted);

        yield return new("gen/operator-reached-only-through-a-generic",
        [
            F("src/v.g", "class Vec { public int n; func _init(int a) { self.n = a; } " +
                         "public operator Vec func +(Vec o) { return new Vec(self.n + o.n); } }"),
            F("src/lib.g", "T func Add[T](T a, T b) { return a + b; }"),
            F("src/main.g", "import \"src/v.g\";\nimport \"src/lib.g\";\n" +
                            "realm userspace { entry func Main() { " +
                            "let Vec s = Add(new Vec(1), new Vec(2)); } }\n"),
        ], Expect.Accepted);

        yield return new("gen/instantiated-only-from-a-process-variable",
        [
            F("src/lib.g", "T func Echo[T](T x) { return x; }"),
            F("src/main.g", "import \"src/lib.g\";\nclass Widget { public int n; }\n" +
                            "realm userspace { entry func Main() { }\n" +
                            "  background process P { let Widget w = Echo(new Widget());\n" +
                            "    thread T { entry func R() { let int q = w.n; } } } }\n"),
        ], Expect.Accepted);

        yield return new("gen/widening-does-not-leak-unrelated-types",
        [
            F("src/lib.g", "T func Echo[T](T x) { return x; }\nint func Peek(Hidden h) { return h.n; }"),
            F("src/hidden.g", "class Hidden { public int n; }"),
            F("src/main.g", "import \"src/lib.g\";\nimport \"src/hidden.g\";\n" +
                            "realm userspace { entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.UndefinedType);

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
            F("src/a.g", "import \"src/b.g\";\nint func A() { return B(); }"),
            F("src/b.g", "import \"src/a.g\";\nint func B() { return 1; }"),
            F("src/main.g", Main("import \"src/a.g\";", "let int v = A();")),
        ], Expect.Any);

        yield return new("import/cycle-three",
        [
            F("src/a.g", "import \"src/b.g\";\nint func A() { return 1; }"),
            F("src/b.g", "import \"src/c.g\";\nint func B() { return 2; }"),
            F("src/c.g", "import \"src/a.g\";\nint func C() { return 3; }"),
            F("src/main.g", Main("import \"src/a.g\";", "let int v = A();")),
        ], Expect.Any);

        yield return new("import/duplicate",
        [
            F("src/lib.g", "int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib.g\";\nimport \"src/lib.g\";", "let int v = Helper();")),
        ], Expect.Any);

        yield return new("import/diamond",
        [
            F("src/base.g", "int func Base() { return 1; }"),
            F("src/left.g", "import \"src/base.g\";\nint func Left() { return Base(); }"),
            F("src/right.g", "import \"src/base.g\";\nint func Right() { return Base(); }"),
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
            F("src/deep.g", "int func Deep() { return 1; }"),
            F("src/mid.g", "import \"src/deep.g\";\nint func Mid() { return Deep(); }"),
            F("src/main.g", Main("import \"src/mid.g\";", "let int v = Deep();")),
        ], Expect.Accepted);

        yield return new("visible/not-imported",
        [
            F("src/lib.g", "int func Helper() { return 7; }"),
            F("src/other.g", "int func Other() { return 1; }"),
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
            F("src/a.g", "private int func Shared() { return 1; }\nint func A() { return Shared(); }"),
            F("src/b.g", "private int func Shared() { return 2; }\nint func B() { return Shared(); }"),
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
            F("src/a.g", "int func Helper() { return 1; }"),
            F("src/b.g", "int func Helper() { return 2; }"),
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
            F("src/b.g", "int func Thing() { return 1; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Any);

        yield return new("dup/unimported-same-name-is-fine",
        [
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

        yield return new("realm/shadows-imported-class",
        [
            F("src/lib.g", "class Widget { public int n; }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { @shadows class Widget { public int m; } " +
                            "entry func Main() { let Widget w = new Widget(); let int v = w.m; } }\n"),
        ], Expect.Accepted);

        yield return new("realm/qualifier-reaches-an-import",
        [
            F("src/lib.g", "class Widget { public int n; }\nint func Helper() { return 1; }"),
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
            F("src/lib.g", "int func Helper() { return 1; }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { @shadows int func Helper() { return 2; } " +
                            "entry func Main() { let int v = Helper(); } }\n"),
        ], Expect.Accepted);

        yield return new("realm/shadows-imported-func-unmarked",
        [
            F("src/lib.g", "int func Helper() { return 1; }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { int func Helper() { return 2; } " +
                            "entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("realm/shadows-unimported-name",
        [
            F("src/lib.g", "class Widget { public int n; }"),
            F("src/main.g", "realm userspace { @shadows class Widget { public int m; } entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("realm/process-shadows-realm-from-another-file",
        [
            F("src/lib.g", "realm userspace { class Frame { public int outer; } }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { foreground process App { " +
                            "class Frame { public int inner; } thread T { entry func R() { } } } " +
                            "entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("realm/no-entry-anywhere",
        [
            F("src/lib.g", "int func Helper() { return 1; }"),
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
            F("src/a.g", "import \"src/box.g\";\nint func A() { let Box[int] b = new Box[int](); return b.v; }"),
            F("src/b.g", "import \"src/box.g\";\nint func B() { let Box[int] b = new Box[int](); return b.v; }"),
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

        yield return new("cross/managed-union-declared-elsewhere",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/u.g", "import \"src/p.g\";\nunion Msg { Text(Payload p), Code(int n) }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/u.g\";",
                                 "let Msg m = Msg.Text(new Payload()); match (m) { case Text(p) { } case Code(n) { } }")),
        ], Expect.Accepted);

        yield return new("cross/managed-union-three-files",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/u.g", "import \"src/p.g\";\nunion Msg { Text(Payload p), Code(int n) }"),
            F("src/mk.g", "import \"src/p.g\";\nimport \"src/u.g\";\nMsg func Make() { return Msg.Text(new Payload()); }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/u.g\";\nimport \"src/mk.g\";",
                                 "let Msg m = Make(); match (m) { case Text(p) { } case Code(n) { } }")),
        ], Expect.Accepted);

        yield return new("cross/nested-managed-union",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/i.g", "import \"src/p.g\";\nunion Inner { A(Payload p), B }"),
            F("src/o.g", "import \"src/i.g\";\nunion Outer { W(Inner i), P(int n) }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/i.g\";\nimport \"src/o.g\";",
                                 "let Outer o = Outer.W(Inner.A(new Payload()));")),
        ], Expect.Accepted);

        yield return new("cross/managed-union-as-class-field",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/u.g", "import \"src/p.g\";\nunion Msg { Text(Payload p), Code(int n) }"),
            F("src/c.g", "import \"src/u.g\";\nclass Box { Msg m; public func _init(Msg m) { self.m = m; } }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/u.g\";\nimport \"src/c.g\";",
                                 "let Box b = new Box(Msg.Text(new Payload()));")),
        ], Expect.Accepted);

        yield return new("cross/union-compared-in-another-file",
        [
            F("src/u.g", "union Msg { Text(int n), Code(int c) }"),
            F("src/main.g", Main("import \"src/u.g\";",
                                 "let bool b = Msg.Text(1) == Msg.Code(1);")),
        ], Expect.Accepted);

        yield return new("cross/union-compared-in-two-files",
        [
            F("src/u.g", "union Msg { Text(int n), Code(int c) }"),
            F("src/a.g", "import \"src/u.g\";\nbool func SameA(Msg x, Msg y) { return x == y; }"),
            F("src/b.g", "import \"src/u.g\";\nbool func SameB(Msg x, Msg y) { return x != y; }"),
            F("src/main.g", Main("import \"src/u.g\";\nimport \"src/a.g\";\nimport \"src/b.g\";",
                                 "let bool b = SameA(Msg.Text(1), Msg.Code(1)) || SameB(Msg.Text(1), Msg.Code(1));")),
        ], Expect.Accepted);

        yield return new("cross/union-payload-equality-operator-elsewhere",
        [
            F("src/v.g", "class Valued { public int n; public operator bool func ==(Valued o) { return self.n == o.n; } }"),
            F("src/u.g", "import \"src/v.g\";\nunion Msg { V(Valued v), Code(int c) }"),
            F("src/main.g", Main("import \"src/v.g\";\nimport \"src/u.g\";",
                                 "let bool b = Msg.Code(1) == Msg.Code(2);")),
        ], Expect.Accepted);

        yield return new("cross/nested-union-compared",
        [
            F("src/p.g", "class Payload { public int n; }"),
            F("src/i.g", "import \"src/p.g\";\nunion Inner { A(Payload p), B(int n) }"),
            F("src/o.g", "import \"src/i.g\";\nunion Outer { W(Inner i), K(int n) }"),
            F("src/main.g", Main("import \"src/p.g\";\nimport \"src/i.g\";\nimport \"src/o.g\";",
                                 "let bool b = Outer.K(1) == Outer.W(Inner.B(2));")),
        ], Expect.Accepted);

        yield return new("cross/generic-reaching-for-generic",
        [
            F("src/g.g", "class Inner[T] { public T v; }\n" +
                         "class Outer[T] { public T item; " +
                         "public Inner[T] func Wrap() { let Inner[T] i = new Inner[T](); i.v = self.item; return i; } }"),
            F("src/main.g", Main("import \"src/g.g\";\nimport \"src/r.g\";",
                                 "let Outer[Res] o = new Outer[Res](); o.item = new Res(); let Res r = o.Wrap().v;")),
            F("src/r.g", "class Res { public int id; }"),
        ], Expect.Accepted);

        yield return new("cross/generic-chain-three-deep",
        [
            F("src/g.g", "class A[T] { public T v; }\n" +
                         "class B[T] { public A[T] a; }\n" +
                         "class C[T] { public B[T] b; }"),
            F("src/main.g", Main("import \"src/g.g\";\nimport \"src/r.g\";", "let C[Res] c = new C[Res]();")),
            F("src/r.g", "class Res { public int id; }"),
        ], Expect.Accepted);

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

        yield return new("cross/generic-union-two-requesters",
        [
            F("src/m.g", "union Maybe[V] { Found(V v), Missing }"),
            F("src/a.g", "import \"src/m.g\";\nMaybe[int] func MakeInt() { return Maybe.Found(1); }"),
            F("src/b.g", "import \"src/m.g\";\nMaybe[bool] func MakeBool() { return Maybe.Found(true); }"),
            F("src/main.g", Main("import \"src/m.g\";\nimport \"src/a.g\";\nimport \"src/b.g\";",
                                 "let Maybe[int] x = MakeInt(); let Maybe[bool] y = MakeBool();")),
        ], Expect.Accepted);

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
            F("src/r.g", "throws int func Risky() { throw; }"),
            F("src/main.g", Main("import \"src/r.g\";",
                                 "let int v = Risky() catch { assign 0; };")),
        ], Expect.Accepted);

        yield return new("cross/fixed-array-type",
        [
            F("src/a.g", "[4]int func Make() { return [1, 2, 3, 4]; }"),
            F("src/main.g", Main("import \"src/a.g\";", "let [4]int a = Make();")),
        ], Expect.Accepted);

        yield return new("cross/func-pointer-type",
        [
            F("src/a.g", "int func Twice(int n) { return n * 2; }"),
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
            F("src/l1.g", "import \"src/l2.g\";\nint func L1() { return L2(); }"),
            F("src/l2.g", "import \"src/l3.g\";\nint func L2() { return L3(); }"),
            F("src/l3.g", "import \"src/l4.g\";\nint func L3() { return L4(); }"),
            F("src/l4.g", "int func L4() { return 4; }"),
            F("src/main.g", Main("import \"src/l1.g\";", "let int v = L1();")),
        ], Expect.Accepted);

        yield return new("file/same-file-two-paths",
        [
            F("src/lib.g", "int func Helper() { return 7; }"),
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

        yield return new("kind/enum-vs-free-func",
        [
            F("src/a.g", "enum Color { Red }"),
            F("src/b.g", "int func Color() { return 1; }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "")),
        ], Expect.Any);

        #endregion

        #region private free-function mangling across files
        yield return new("private/three-files-same-name",
        [
            F("src/a.g", "private int func Shared() { return 1; }\nint func A() { return Shared(); }"),
            F("src/b.g", "private int func Shared() { return 2; }\nint func B() { return Shared(); }"),
            F("src/c.g", "private int func Shared() { return 3; }\nint func C() { return Shared(); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";\nimport \"src/c.g\";",
                                 "let int v = A() + B() + C();")),
        ], Expect.Accepted);

        yield return new("private/shadows-public-in-other-file",
        [
            F("src/a.g", "int func Helper() { return 1; }"),
            F("src/b.g", "private int func Helper() { return 2; }\nint func B() { return Helper(); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = B();")),
        ], Expect.Any);

        yield return new("private/overloads-in-two-files",
        [
            F("src/a.g", "private int func P(int n) { return n; }\nprivate int func P(bool b) { return 1; }\nint func A() { return P(1) + P(true); }"),
            F("src/b.g", "private int func P(int n) { return n * 2; }\nint func B() { return P(2); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = A() + B();")),
        ], Expect.Accepted);

        yield return new("private/class-in-two-files",
        [
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

        yield return new("proc/gatos-two-processes-in-two-files",
        [
            F("src/a.g", "realm userspace { background process Alpha { " +
                         "thread T { entry func Run() { } } } }"),
            F("src/b.g", "realm userspace { background process Beta { " +
                         "thread T { entry func Run() { } } } }"),
            F("src/main.g", "import \"src/a.g\";\nimport \"src/b.g\";\nrealm kernel { entry func Main() { } }\n"),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("proc/gatos-same-process-name-two-files",
        [
            F("src/a.g", "realm userspace { background process Worker { thread T { entry func Run() { } } } }"),
            F("src/b.g", "realm userspace { background process Worker { thread U { entry func Run() { } } } }"),
            F("src/main.g", "import \"src/a.g\";\nimport \"src/b.g\";\nrealm kernel { entry func Main() { } }\n"),
        ], Expect.Rejected, Codes.DuplicateName, Target.GatOS);

        yield return new("proc/gatos-same-thread-name-two-files",
        [
            F("src/a.g", "realm userspace { background process Alpha { thread T { entry func Run() { } } } }"),
            F("src/b.g", "realm userspace { background process Beta { thread T { entry func Run() { } } } }"),
            F("src/main.g", "import \"src/a.g\";\nimport \"src/b.g\";\nrealm kernel { entry func Main() { } }\n"),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("proc/gatos-same-name-in-both-realms-two-files",
        [
            F("src/a.g", "realm userspace { background process App { thread T { entry func Run() { } } } }"),
            F("src/main.g", "import \"src/a.g\";\nrealm kernel { " +
                            "background process App { thread T { entry func Run() { } } } " +
                            "entry func Main() { } }\n"),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("proc/gatos-same-state-name-two-files",
        [
            F("src/a.g", "realm userspace { background process Alpha { let int seed = 1; " +
                         "thread T { entry func Run() { let int a = seed; } } } }"),
            F("src/b.g", "realm userspace { background process Beta { let int seed = 2; " +
                         "thread T { entry func Run() { let int a = seed; } } } }"),
            F("src/main.g", "import \"src/a.g\";\nimport \"src/b.g\";\nrealm kernel { entry func Main() { } }\n"),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("proc/gatos-managed-state-over-another-files-class",
        [
            F("src/w.g", "class Widget { public int n; func _init(int a) { self.n = a; } }"),
            F("src/a.g", "import \"src/w.g\";\nrealm userspace { background process Alpha { " +
                         "let Widget held = new Widget(4); " +
                         "thread T { entry func Run() { let int a = held.n; } } } }"),
            F("src/main.g", "import \"src/a.g\";\nrealm kernel { entry func Main() { } }\n"),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("proc/gatos-thread-reaches-three-files-out",
        [
            F("src/g.g", "T func Echo[T](T x) { return x; }"),
            F("src/lib.g", "import \"src/g.g\";\nrealm userspace { int func Step() { return Echo(6); } }"),
            F("src/a.g", "import \"src/lib.g\";\nrealm userspace { background process Alpha { " +
                         "thread T { entry func Run() { let int a = Step() + userspace.Step(); } } } }"),
            F("src/main.g", "import \"src/a.g\";\nrealm kernel { entry func Main() { } }\n"),
        ], Expect.Accepted, null, Target.GatOS);

        #endregion

        #region generics instantiated from several files
        yield return new("gen/three-files-same-instantiation",
        [
            F("src/box.g", "class Box[T] { public T v; }"),
            F("src/a.g", "import \"src/box.g\";\nint func A() { let Box[int] b = new Box[int](); return b.v; }"),
            F("src/b.g", "import \"src/box.g\";\nint func B() { let Box[int] b = new Box[int](); return b.v; }"),
            F("src/c.g", "import \"src/box.g\";\nint func C() { let Box[int] b = new Box[int](); return b.v; }"),
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
            F("src/id.g", "T func Id[T](T v) { return v; }"),
            F("src/a.g", "import \"src/id.g\";\nint func A() { return Id(1); }"),
            F("src/b.g", "import \"src/id.g\";\nint func B() { return Id(2); }"),
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
            F("src/a.g", "class Holder[T] { public T v; }\nint func A() { let Holder[int] h = new Holder[int](); return h.v; }"),
            F("src/b.g", "class Holder[T] { public T v; }\nint func B() { let Holder[int] h = new Holder[int](); return h.v; }"),
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
            F("src/lib.g", "int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"./src/lib.g\";", "let int v = Helper();")),
        ], Expect.Any);

        yield return new("path/redundant-segments",
        [
            F("src/lib.g", "int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/../src/lib.g\";", "let int v = Helper();")),
        ], Expect.Any);

        yield return new("path/wrong-case",
        [
            F("src/lib.g", "int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib.g\";\nimport \"src/LIB.g\";", "let int v = Helper();")),
        ], Expect.Any);

        yield return new("path/no-extension",
        [
            F("src/lib.g", "int func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib\";", "")),
        ], Expect.Rejected);

        yield return new("path/absolute-looking",
        [
            F("src/main.g", Main("import \"/nonexistent/lib.g\";", "")),
        ], Expect.Rejected);

        yield return new("path/back-edge-to-entry",
        [
            F("src/lib.g", "import \"src/main.g\";\nint func Helper() { return 7; }"),
            F("src/main.g", Main("import \"src/lib.g\";", "let int v = Helper();")),
        ], Expect.Any);

        #endregion

        #region fan-out and depth
        yield return new("scale/wide-fan-out",
        [
            F("src/m0.g", "int func M0() { return 0; }"),
            F("src/m1.g", "int func M1() { return 1; }"),
            F("src/m2.g", "int func M2() { return 2; }"),
            F("src/m3.g", "int func M3() { return 3; }"),
            F("src/m4.g", "int func M4() { return 4; }"),
            F("src/m5.g", "int func M5() { return 5; }"),
            F("src/m6.g", "int func M6() { return 6; }"),
            F("src/m7.g", "int func M7() { return 7; }"),
            F("src/main.g", Main(
                string.Join("\n", Enumerable.Range(0, 8).Select(i => $"import \"src/m{i}.g\";")),
                "let int v = " + string.Join(" + ", Enumerable.Range(0, 8).Select(i => $"M{i}()")) + ";")),
        ], Expect.Accepted);

        yield return new("scale/mutual-pair-with-shared-dep",
        [
            F("src/dep.g", "int func Dep() { return 1; }"),
            F("src/a.g", "import \"src/b.g\";\nimport \"src/dep.g\";\nint func A() { return Dep(); }"),
            F("src/b.g", "import \"src/a.g\";\nimport \"src/dep.g\";\nint func B() { return Dep(); }"),
            F("src/main.g", Main("import \"src/a.g\";\nimport \"src/b.g\";", "let int v = A() + B();")),
        ], Expect.Accepted);

        #endregion

        #region realm qualifiers reaching another file

        yield return new("qualify/function-declared-in-another-file",
        [
            F("src/lib.g", "realm userspace { int func Step() { return 4; } }"),
            F("src/main.g", GatOS("import \"src/lib.g\";",
                                  "int func Use() { return userspace.Step(); }")),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("qualify/type-declared-in-another-file",
        [
            F("src/lib.g", "realm userspace { class Frame { public int n; func _init() { self.n = 3; } } }"),
            F("src/main.g", GatOS("import \"src/lib.g\";",
                                  "int func Use() { let userspace.Frame f = new userspace.Frame(); return f.n; }")),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("qualify/reaches-the-realm-past-a-root-name",
        [
            F("src/root.g", "int func Step() { return 1; }"),
            F("src/lib.g", "import \"src/root.g\";\nrealm userspace { @shadows int func Step() { return 2; } }"),
            F("src/main.g", GatOS("import \"src/root.g\";\nimport \"src/lib.g\";",
                                  "int func Use() { return userspace.Step() + ::Step(); }")),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("qualify/module-declared-in-another-file",
        [
            F("src/lib.g", "realm userspace { module M { public static int func F() { return 6; } } }"),
            F("src/main.g", GatOS("import \"src/lib.g\";",
                                  "int func Use() { return userspace.M.F(); }")),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("qualify/generic-function-declared-in-another-file",
        [
            F("src/w.g", "class Widget { public int n; func _init() { self.n = 8; } }"),
            F("src/lib.g", "realm userspace { T func Echo[T](T x) { return x; } }"),
            F("src/main.g", GatOS("import \"src/w.g\";\nimport \"src/lib.g\";",
                                  "int func Use() { let Widget w = userspace.Echo(new Widget()); return w.n; }")),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("qualify/generic-type-declared-in-another-file",
        [
            F("src/w.g", "class Widget { public int n; func _init() { self.n = 9; } }"),
            F("src/lib.g", "realm userspace { class Hold[T] { public T v; func _init(T x) { self.v = x; } } }"),
            F("src/main.g", GatOS("import \"src/w.g\";\nimport \"src/lib.g\";",
                                  "int func Use() { let userspace.Hold[Widget] h = new userspace.Hold[Widget](new Widget()); return h.v.n; }")),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("qualify/generic-over-a-third-files-class",
        [
            F("src/w.g", "class Widget { public int n; func _init() { self.n = 5; } }"),
            F("src/lib.g", "realm userspace { T func Echo[T](T x) { return x; } " +
                           "class Hold[T] { public T v; func _init(T x) { self.v = x; } } }"),
            F("src/main.g", GatOS("import \"src/w.g\";\nimport \"src/lib.g\";",
                                  "int func Use() { let userspace.Hold[Widget] h = " +
                                  "new userspace.Hold[Widget](userspace.Echo(new Widget())); return h.v.n; }")),
        ], Expect.Accepted, null, Target.GatOS);

        yield return new("qualify/thread-reaches-the-realms-other-file",
        [
            F("src/lib.g", "realm userspace { int func Step() { return 4; } " +
                           "class Frame { public int n; func _init() { self.n = 3; } } }"),
            F("src/main.g", "import \"src/lib.g\";\nrealm userspace { background process P { " +
                            "thread T { entry func R() { let userspace.Frame f = new userspace.Frame(); " +
                            "let int v = Step() + userspace.Step() + f.n; } } } }\n" +
                            "realm kernel { entry func Main() { } }\n"),
        ], Expect.Accepted, Target: Target.GatOS);

        yield return new("qualify/process-type-is-not-reachable-from-another-file",
        [
            F("src/lib.g", "realm userspace { background process Worker { " +
                           "class Frame { public int n; func _init() { self.n = 2; } } " +
                           "thread T { entry func R() { let Frame f = new Frame(); } } } }"),
            F("src/main.g", GatOS("import \"src/lib.g\";",
                                  "int func Use() { let userspace.Worker.Frame f = new userspace.Worker.Frame(); return f.n; }")),
        ], Expect.Rejected, Codes.ScopeNotEnclosing, Target.GatOS);

        yield return new("qualify/process-variable-is-not-reachable-from-another-file",
        [
            F("src/lib.g", "realm userspace { background process Worker { " +
                           "let int seed = 5; " +
                           "thread T { entry func R() { let int a = seed; } } } }"),
            F("src/main.g", GatOS("import \"src/lib.g\";",
                                  "int func Use() { return userspace.Worker.seed; }")),
        ], Expect.Rejected, null, Target.GatOS);

        yield return new("qualify/process-variable-initialised-across-files",
        [
            F("src/w.g", "class Widget { public int n; func _init(int a) { self.n = a; } }"),
            F("src/lib.g", "T func Echo[T](T x) { return x; }\nrealm userspace { int func Seed() { return 7; } }"),
            F("src/main.g", "import \"src/w.g\";\nimport \"src/lib.g\";\nrealm userspace { " +
                            "background process Worker { " +
                            "let Widget shared = Echo(new Widget(userspace.Seed())); " +
                            "thread T { entry func R() { let int a = shared.n; } } } }\n" +
                            "realm kernel { entry func Main() { } }\n"),
        ], Expect.Accepted, Target: Target.GatOS);

        yield return new("qualify/other-realm-from-a-third-file",
        [
            F("src/k.g", "realm kernel { class Cfg { public int a; } entry func KMain() { } }"),
            F("src/lib.g", "realm userspace { int func Reach() { let kernel.Cfg c; return 1; } }"),
            F("src/main.g", "import \"src/k.g\";\nimport \"src/lib.g\";\nrealm userspace { " +
                            "background process P { thread T { entry func R() { let int v = Reach(); } } } }\n"),
        ], Expect.Rejected, Codes.ScopeNotEnclosing, Target.GatOS);

        yield return new("qualify/same-name-in-both-halves-of-a-realm",
        [
            F("src/lib.g", "realm userspace { int func Step() { return 1; } }"),
            F("src/main.g", GatOS("import \"src/lib.g\";",
                                  "int func Step() { return 2; } int func Use() { return Step(); }")),
        ], Expect.Rejected, null, Target.GatOS);

        #endregion


        #region externs and entry-name sharing across files

        yield return new("extern/definition-and-declaration-in-different-files",
        [
            F("src/impl.g", "native { int probe_add(int a, int b) { return a + b; } }"),
            F("src/decl.g", "@extern int func probe_add(int a, int b);"),
            F("src/main.g", Main("import \"src/impl.g\";\nimport \"src/decl.g\";",
                                 "let int v = probe_add(2, 3);")),
        ], Expect.Accepted);

        yield return new("extern/static-inline-definition-in-another-file",
        [
            F("src/impl.g", "native { static inline int probe_twice(int a) { return a * 2; } }"),
            F("src/decl.g", "@extern int func probe_twice(int a);"),
            F("src/main.g", Main("import \"src/impl.g\";\nimport \"src/decl.g\";",
                                 "let int v = probe_twice(21);")),
        ], Expect.Accepted);

        yield return new("extern/declared-in-two-files",
        [
            F("src/impl.g", "native { int probe_one(void) { return 1; } }"),
            F("src/a.g", "@extern int func probe_one();\nint func ViaA() { return probe_one(); }"),
            F("src/b.g", "@extern int func probe_one();\nint func ViaB() { return probe_one(); }"),
            F("src/main.g", Main("import \"src/impl.g\";\nimport \"src/a.g\";\nimport \"src/b.g\";",
                                 "let int v = ViaA() + ViaB();")),
        ], Expect.Accepted);

        yield return new("extern/conflicting-declarations-in-two-files",
        [
            F("src/impl.g", "native { int probe_two(int a) { return a; } }"),
            F("src/a.g", "@extern int func probe_two(int a);"),
            F("src/b.g", "@extern int func probe_two(bool a);"),
            F("src/main.g", Main("import \"src/impl.g\";\nimport \"src/a.g\";\nimport \"src/b.g\";",
                                 "let int v = probe_two(1);")),
        ], Expect.Rejected, Codes.DuplicateName);

        yield return new("entry/free-function-in-another-file-shares-the-entry-name",
        [
            F("src/lib.g", "int func Main(int n) { return n + 1; }"),
            F("src/main.g", Main("import \"src/lib.g\";", "let int v = Main(41);")),
        ], Expect.Accepted);

        yield return new("entry/free-overloads-in-another-file-share-the-entry-name",
        [
            F("src/lib.g", "int func Main(int n) { return n + 1; } int func Main(bool b) { return 1; }"),
            F("src/main.g", Main("import \"src/lib.g\";", "let int v = Main(41) + Main(true);")),
        ], Expect.Accepted);
        
        yield return new("entry/method-in-another-file-shares-the-entry-name",
        [
            F("src/lib.g", "class Runner { public int n; func _init() { self.n = 5; } " +
                           "public int func Main() { return self.n; } }"),
            F("src/main.g", Main("import \"src/lib.g\";",
                                 "let Runner r = new Runner(); let int v = r.Main();")),
        ], Expect.Accepted);

        yield return new("entry/private-free-function-shares-the-entry-name",
        [
            F("src/lib.g", "private int func Main() { return 3; } int func Reach() { return Main(); }"),
            F("src/main.g", Main("import \"src/lib.g\";", "let int v = Reach();")),
        ], Expect.Accepted);

        #endregion
    }
}
