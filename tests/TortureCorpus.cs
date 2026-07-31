namespace Appa.Tests;

/// <summary>
/// What a torture case is expected to do. The corpus is full of syntactically plausible nonsense,
/// so most cases are <see cref="Rejected"/> - the point being that the compiler says *why* rather
/// than crashing, silently accepting, or emitting broken C.
/// </summary>
public enum Expect
{
    /// <summary>
    /// The compiler must produce at least one error diagnostic.
    /// </summary>
    Rejected,

    /// <summary>
    /// The program is legal and must check without any error.
    /// </summary>
    Accepted,

    /// <summary>
    /// Either outcome is defensible; only the no-crash / well-formed-output properties are
    /// asserted. Used for machine-generated combinations whose legality depends on context.
    /// </summary>
    Any
}

/// <summary>
/// One torture case: a name for failure messages, a source program, and its expectation.
/// </summary>
public sealed record TortureCase(string Name, string Source, Expect Expect, string? Code = null)
{
    public override string ToString() => Name;
}

/// <summary>
/// Builds the torture corpus from <see cref="Curated"/> cases pinning one rule each and the <see
/// cref="StatementMatrix"/>/<see cref="ExpressionMatrix"/> crossings, which catch the "only
/// rejected where its author thought about it" hole across positions that differ.
/// </summary>
public static class TortureCorpus
{
    // Lazy rather than a static initializer: the matrices read the position/probe
    // arrays declared further down, which a field initializer here would run before.
    private static IReadOnlyList<TortureCase>? _all;

    /// <summary>
    /// The full corpus: curated cases plus every generated matrix.
    /// </summary>
    public static IReadOnlyList<TortureCase> All =>
        _all ??= [.. Curated(), .. Curated2(), .. StatementMatrix(), .. ExpressionMatrix(),
                  .. DeclarationMatrix(), .. TypeMatrix(), .. BinaryOperatorMatrix(),
                  .. AssignmentMatrix(), .. MemberAccessMatrix(), .. IdentifierMatrix()];

    #region Statement matrix
    /// <summary>
    /// Every syntactic position that can hold a statement. "%S%" is the hole. Each template is
    /// otherwise a complete, valid program, so any diagnostic produced is attributable to the
    /// injected statement.
    /// </summary>
    private static readonly (string Name, string Template)[] StmtPositions =
    [
        ("free-func",     "void func H() { %S% } realm kernel { entry func Main() { H(); } }"),
        ("entry",         "realm kernel { entry func Main() { %S% } }"),
        ("class-method",  "class C { public void func M() { %S% } } realm kernel { entry func Main() { let C c = new C(); c.M(); } }"),
        ("module-method", "module M { public static void func F() { %S% } } realm kernel { entry func Main() { M.F(); } }"),
        ("ctor",          "class C { func _init() { %S% } } realm kernel { entry func Main() { let C c = new C(); } }"),
        ("operator",      "class C { int n; public operator C func +(C o) { %S% return self; } } realm kernel { entry func Main() { let C a = new C(); let C b = a + a; } }"),
        ("try",           "throws void func T() { throw; } realm kernel { entry func Main() { try { T(); %S% } catch { } } }"),
        ("catch",         "throws void func T() { throw; } realm kernel { entry func Main() { try { T(); } catch { %S% } } }"),
        ("catch-handler", "throws int func T() { throw; } realm kernel { entry func Main() { let int v = T() catch { %S% assign 0; }; } }"),
        ("defer",         "realm kernel { entry func Main() { defer %S% } }"),
        ("while-body",    "realm kernel { entry func Main() { while (false) { %S% } } }"),
        ("for-body",      "realm kernel { entry func Main() { for (let int i = 0; i < 1; i++) { %S% } } }"),
        ("forin-body",    "realm kernel { entry func Main() { let a = [1, 2]; for x in a { %S% } } }"),
        ("if-then",       "realm kernel { entry func Main() { if (true) { %S% } } }"),
        ("else",          "realm kernel { entry func Main() { if (true) { } else { %S% } } }"),
        ("switch-case",   "realm kernel { entry func Main() { switch (1) { case 1 { %S% } } } }"),
        ("switch-def",    "realm kernel { entry func Main() { switch (1) { case 1 { } default { %S% } } } }"),
        ("match-case",    "union U { A, B } realm kernel { entry func Main() { let U u = U.A(); match (u) { case A { %S% } case B { } } } }"),
        ("match-def",     "union U { A, B } realm kernel { entry func Main() { let U u = U.A(); match (u) { case A { } default { %S% } } } }"),
        ("unsafe",        "realm kernel { entry func Main() { unsafe { %S% } } }"),
        ("nested-block",  "realm kernel { entry func Main() { { { %S% } } } }"),
        ("thread-entry",  "realm kernel { foreground process P { thread T { entry func Run() { %S% } } } entry func Main() { } }"),
    ];

    /// <summary>
    /// Statements that are legal somewhere and nonsense elsewhere. Each must be either accepted or
    /// rejected with a diagnostic in every position above -- never crash, never fall through into
    /// the emitter.
    /// </summary>
    private static readonly (string Name, string Stmt)[] StmtProbes =
    [
        ("assign-value",   "assign 1;"),
        ("assign-str",     "assign \"s\";"),
        ("break",          "break;"),
        ("continue",       "continue;"),
        ("return-void",    "return;"),
        ("return-int",     "return 1;"),
        ("throw",          "throw;"),
        ("panic",          "panic \"boom\";"),
        ("debug",          "debug \"msg\";"),
        ("defer-break",    "defer break;"),
        ("defer-return",   "defer return;"),
        ("defer-assign",   "defer assign 1;"),
        ("defer-throw",    "defer throw;"),
        ("defer-block",    "defer { }"),
        ("empty-block",    "{ }"),
        ("let-shadow",     "let int q = 1; let int q = 2;"),
        ("bare-literal",   "1;"),
        ("undef-call",     "Nope();"),
        ("undef-assign",   "zzz = 1;"),
        ("self-use",       "self.q = 1;"),
        ("nested-unsafe",  "unsafe { }"),
        ("nested-try",     "try { } catch { }"),
        ("deref",          "let int n = 1; let int* p = &n; *p = 2;"),
        ("call-entry",     "Main();"),
        ("new-void",       "let void v = default(void);"),
    ];

    /// <summary>
    /// Crosses every statement probe with every statement position. Legality varies by position, so
    /// the expectation is <see cref="Expect.Any"/> and the assertions that matter are no-crash and
    /// well-formed output.
    /// </summary>
    private static IEnumerable<TortureCase> StatementMatrix()
    {
        foreach (var (pn, tpl) in StmtPositions)
            foreach (var (sn, stmt) in StmtProbes)
                yield return new TortureCase($"stmt/{pn}/{sn}", tpl.Replace("%S%", stmt), Expect.Any);
    }

    #endregion

    #region Expression matrix
    /// <summary>
    /// Every syntactic position that can hold an expression. "%E%" is the hole.
    /// </summary>
    private static readonly (string Name, string Template)[] ExprPositions =
    [
        ("let-init",      "realm kernel { entry func Main() { let v = %E%; } }"),
        ("let-typed",     "realm kernel { entry func Main() { let int v = %E%; } }"),
        ("expr-stmt",     "realm kernel { entry func Main() { %E%; } }"),
        ("if-cond",       "realm kernel { entry func Main() { if (%E%) { } } }"),
        ("while-cond",    "realm kernel { entry func Main() { while (%E%) { } } }"),
        ("for-cond",      "realm kernel { entry func Main() { for (let int i = 0; %E%; i++) { } } }"),
        ("for-init",      "realm kernel { entry func Main() { for (%E%; false; ) { } } }"),
        ("for-step",      "realm kernel { entry func Main() { for (; false; %E%) { } } }"),
        ("forin-subject", "realm kernel { entry func Main() { for x in %E% { } } }"),
        ("switch-scrut",  "realm kernel { entry func Main() { switch (%E%) { case 1 { } } } }"),
        ("switch-label",  "realm kernel { entry func Main() { switch (1) { case %E% { } } } }"),
        ("match-scrut",   "union U { A } realm kernel { entry func Main() { match (%E%) { case A { } } } }"),
        ("return-val",    "int func H() { return %E%; } realm kernel { entry func Main() { H(); } }"),
        ("call-arg",      "void func H(int n) { } realm kernel { entry func Main() { H(%E%); } }"),
        ("ref-arg",       "void func H(ref int n) { } realm kernel { entry func Main() { H(ref %E%); } }"),
        ("index",         "realm kernel { entry func Main() { let a = [1, 2]; let int v = a[%E%]; } }"),
        ("array-elem",    "realm kernel { entry func Main() { let a = [%E%, 1]; } }"),
        ("ternary-then",  "realm kernel { entry func Main() { let v = true ? %E% : 0; } }"),
        ("ternary-cond",  "realm kernel { entry func Main() { let v = %E% ? 1 : 0; } }"),
        ("interp",        "realm kernel { entry func Main() { let s = $\"v={%E%}\"; } }"),
        ("field-init",    "class C { int n = %E%; } realm kernel { entry func Main() { let C c = new C(); } }"),
        ("enum-value",    "enum E { A = %E% } realm kernel { entry func Main() { let E e = E.A; } }"),
        ("new-arg",       "class C { func _init(int n) { } } realm kernel { entry func Main() { let C c = new C(%E%); } }"),
        ("coll-init",     "realm kernel { entry func Main() { let a = new [2]int { %E%, 0 }; } }"),
        ("assign-rhs",    "realm kernel { entry func Main() { let int v = 0; v = %E%; } }"),
        ("assign-lhs",    "realm kernel { entry func Main() { %E% = 1; } }"),
        ("binop-left",    "realm kernel { entry func Main() { let v = %E% + 1; } }"),
        ("unary-not",     "realm kernel { entry func Main() { let v = !%E%; } }"),
        ("cast-operand",  "realm kernel { entry func Main() { let v = %E% as bool; } }"),
        ("member-target", "realm kernel { entry func Main() { let v = (%E%).Length; } }"),
    ];

    /// <summary>
    /// Expressions ranging from legal to structurally invalid in most positions.
    /// </summary>
    private static readonly (string Name, string Expr)[] ExprProbes =
    [
        ("int",            "1"),
        ("bool",           "true"),
        ("str",            "\"s\""),
        ("null",           "null"),
        ("void-call",      "VoidH()"),
        ("undef-ident",    "zzz"),
        ("undef-call",     "Nope()"),
        ("div-zero",       "1 / 0"),
        ("mod-zero",       "1 % 0"),
        ("shift-huge",     "1 << 99"),
        ("self-cmp",       "1 == 1"),
        ("sizeof",         "sizeof(int)"),
        ("default-void",   "default(void)"),
        ("addr-of",        "&zzz"),
        ("deref",          "*zzz"),
        ("inc",            "zzz++"),
        ("inc-literal",    "1++"),
        ("array-lit",      "[1, 2]"),
        ("empty-array",    "[]"),
        ("new-undef",      "new Nope()"),
        ("new-prim",       "new int()"),
        ("cast-bool",      "1 as bool"),
        ("cast-void",      "1 as void"),
        ("catch-call",     "ThrowsH() catch { assign 0; }"),
        ("nested-assign",  "(zzz = 1)"),
        ("ternary-mixed",  "true ? 1 : \"s\""),
        ("interp-nested",  "$\"{1}\""),
        ("member-on-int",  "1.Length"),
        ("index-on-int",   "1[0]"),
        ("call-on-int",    "1()"),
    ];

    /// <summary>
    /// Crosses every expression probe with every expression position. Two helpers (a void function
    /// and a throwing function) are prepended so the probes that reference them resolve rather than
    /// failing for an unrelated reason.
    /// </summary>
    private static IEnumerable<TortureCase> ExpressionMatrix()
    {
        const string prelude = "void func VoidH() { } throws int func ThrowsH() { throw; }\n";
        foreach (var (pn, tpl) in ExprPositions)
            foreach (var (en, expr) in ExprProbes)
                yield return new TortureCase($"expr/{pn}/{en}", prelude + tpl.Replace("%E%", expr), Expect.Any);
    }

    #endregion

    #region Declaration matrix
    /// <summary>
    /// Every container a declaration can appear in, with "%D%" as the hole. Which forms each
    /// accepts is spread across ParseTopLevel, ParseContextItem, ParseClassMember and the
    /// process/thread parsers, so a form rejected in one is easy to forget in another.
    /// </summary>
    private static readonly (string Name, string Template)[] DeclPositions =
    [
        ("top-level",  "%D% realm kernel { entry func Main() { } }"),
        ("realm-kernel",    "realm kernel { %D% entry func Main() { } }"),
        ("realm-userspace", "realm kernel { entry func Main() { } } realm userspace { %D% }"),
        ("class",      "class Holder { %D% } realm kernel { entry func Main() { } }"),
        ("module",     "module Holder { %D% } realm kernel { entry func Main() { } }"),
        // The two process positions are deliberately a matched pair: a process body means the same
        // thing in either realm, so every probe must get the same verdict in both. RealmSymmetry
        // in TortureTests asserts exactly that.
        ("process-kernel", "realm kernel { foreground process P { %D% thread T { entry func R() { } } } entry func Main() { } }"),
        ("process-user",   "realm kernel { entry func Main() { } } realm userspace { foreground process P { %D% thread T { entry func R() { } } } }"),
        ("thread",     "realm kernel { foreground process P { thread T { %D% entry func R() { } } } entry func Main() { } }"),
        ("func-body",  "realm kernel { entry func Main() { %D% } }"),
    ];

    /// <summary>
    /// Declaration forms, each legal in some containers and not others.
    /// </summary>
    private static readonly (string Name, string Decl)[] DeclProbes =
    [
        ("free-func",     "void func Helper() { }"),
        ("entry-func",    "entry func Second() { }"),
        ("throws-func",   "throws void func Risk() { throw; }"),
        ("static-func",   "static void func Helper() { }"),
        ("public-func",   "public void func Helper() { }"),
        ("private-func",  "private void func Helper() { }"),
        ("class",         "class Inner { int n; }"),
        ("module",        "module Inner { }"),
        ("enum",          "enum Inner { A }"),
        ("union",         "union Inner { A }"),
        ("realm-kernel-block",    "realm kernel { }"),
        ("realm-userspace-block", "realm userspace { }"),
        ("bad-realm",     "realm potato { }"),
        ("bare-kernel",   "kernel { }"),
        // 'user', 'process' and 'thread' stopped being reserved words when 'realm' landed. These
        // probes are the regression net: in a function body or class they must parse clean.
        ("let-user",      "let int user = 5;"),
        ("let-process",   "let int process = 5;"),
        ("let-thread",    "let int thread = 5;"),
        ("field-user",    "int user;"),
        ("process",       "foreground process Q { thread U { entry func R() { } } }"),
        ("thread",        "thread U { entry func R() { } }"),
        ("field",         "int n;"),
        ("field-init",    "int n = 1;"),
        ("static-field",  "static int n;"),
        ("operator",      "public operator int func +(int o) { return o; }"),
        ("import",        "import gata;"),
        ("extern",        "@extern void func Ext();"),
        ("environment",   "@environment"),
        ("generic-class", "class Gen[T] { T v; }"),
        ("generic-func",  "T func Gen[T](T v) { return v; }"),
        // Crossed with every container, so the annotation is probed at root, in each realm, in a
        // process, in a thread, in a class and in a function body without a case each.
        ("shadows-class", "@shadows class Inner { int n; }"),
        ("shadows-func",  "@shadows void func Helper() { }"),
        ("shadows-enum",  "@shadows enum Inner { A }"),
        // Process variables. Legal in a process body and nowhere else, so crossing them with every
        // container is what checks the "nowhere else" half rather than only the one that works.
        ("process-var",        "let int shared = 1;"),
        ("process-var-noinit", "let int spare;"),
        ("process-var-expr",   "let int shared = 1 + 2 * 3;"),
    ];

    /// <summary>
    /// Crosses every declaration form with every container that could hold one.
    /// </summary>
    private static IEnumerable<TortureCase> DeclarationMatrix()
    {
        foreach (var (pn, tpl) in DeclPositions)
            foreach (var (dn, decl) in DeclProbes)
                yield return new TortureCase($"decl/{pn}/{dn}", tpl.Replace("%D%", decl), Expect.Any);
    }

    #endregion

    #region Type matrix
    /// <summary>
    /// Every position that takes a type specifier. "%T%" is the hole. Types reach the checker
    /// through several different paths (ResolveType, CheckType, the parser's SkipTypeSpec
    /// lookahead), and a type that is nonsense in one has to be caught in all of them.
    /// </summary>
    private static readonly (string Name, string Template)[] TypePositions =
    [
        ("let",          "realm kernel { entry func Main() { let %T% v = default(%T%); } }"),
        ("param",        "void func H(%T% p) { } realm kernel { entry func Main() { } }"),
        ("ref-param",    "void func H(ref %T% p) { } realm kernel { entry func Main() { } }"),
        ("return",       "%T% func H() { return default(%T%); } realm kernel { entry func Main() { } }"),
        ("field",        "class C { %T% n; } realm kernel { entry func Main() { } }"),
        ("cast",         "realm kernel { entry func Main() { let v = 1 as %T%; } }"),
        ("sizeof",       "realm kernel { entry func Main() { let v = sizeof(%T%); } }"),
        ("default",      "realm kernel { entry func Main() { let v = default(%T%); } }"),
        ("new",          "realm kernel { entry func Main() { let v = new %T%(); } }"),
        ("generic-arg",  "class Box[T] { T v; } realm kernel { entry func Main() { let Box[%T%] b = new Box[%T%](); } }"),
        ("array-elem",   "realm kernel { entry func Main() { let [2]%T% a = new [2]%T%; } }"),
        ("ptr",          "realm kernel { entry func Main() { unsafe { let %T%* p = null; } } }"),
        ("funcptr-ret",  "realm kernel { entry func Main() { let func(int) -> %T% f = null; } }"),
        ("funcptr-arg",  "realm kernel { entry func Main() { let func(%T%) -> int f = null; } }"),
        ("union-field",  "union U { A(%T% x) } realm kernel { entry func Main() { } }"),
    ];

    /// <summary>
    /// Type specifiers ranging from ordinary to structurally invalid.
    /// </summary>
    private static readonly (string Name, string Type)[] TypeProbes =
    [
        ("int",         "int"),
        ("bool",        "bool"),
        ("char",        "char"),
        ("double",      "double"),
        ("void",        "void"),
        ("string",      "String"),
        ("undefined",   "NoSuchType"),
        ("lowercase",   "nosuchtype"),
        ("ptr",         "int*"),
        ("ptr-ptr",     "int**"),
        ("void-ptr",    "void*"),
        ("fixed-array", "[4]int"),
        ("array-void",  "[4]void"),
        ("array-zero",  "[0]int"),
        ("funcptr",     "func(int) -> int"),
        ("funcptr-void","func() -> void"),
        ("generic-use", "Box[int]"),
        ("generic-bad", "Box[NoSuchType]"),
        ("generic-arity", "Box[int, int]"),
        ("nested-array","[2][2]int"),
    ];

    /// <summary>
    /// Crosses every type probe with every type position. A generic Box is prepended so the generic
    /// probes name a real template rather than failing as an unknown type.
    /// </summary>
    private static IEnumerable<TortureCase> TypeMatrix()
    {
        const string prelude = "class Box[T] { T v; }\n";
        foreach (var (pn, tpl) in TypePositions)
            foreach (var (tn, type) in TypeProbes)
            {
                // The generic-arg position would nest Box inside Box's own argument list
                // and re-declare the prelude's Box; skip that self-referential pairing.
                if (pn == "generic-arg" && type.StartsWith("Box")) continue;
                yield return new TortureCase($"type/{pn}/{tn}",
                    prelude + tpl.Replace("%T%", type), Expect.Any);
            }
    }

    #endregion

    #region Binary operator matrix
    /// <summary>
    /// Every binary operator crossed with operand pairs covering each type family. Most
    /// combinations are type errors; the invariant is that each is either rejected or emits valid
    /// C, never silently lowered to a C operator meaning something else.
    /// </summary>
    private static IEnumerable<TortureCase> BinaryOperatorMatrix()
    {
        string[] ops = ["+", "-", "*", "/", "%", "&", "|", "^", "<<", ">>",
                        "==", "!=", "<", ">", "<=", ">=", "&&", "||"];
        (string Name, string Expr)[] operands =
        [
            ("int", "1"), ("double", "1.5"), ("bool", "true"), ("char", "'c'"),
            ("str", "\"s\""), ("null", "null"), ("enum", "Color.Red"),
            ("class", "obj"), ("array", "arr"),
        ];

        const string prelude = "enum Color { Red } class Thing { }\n";
        foreach (var op in ops)
            foreach (var (ln, le) in operands)
                foreach (var (rn, re) in operands)
                {
                    string body = $"let Thing obj = new Thing(); let arr = [1, 2]; let v = {le} {op} {re};";
                    yield return new TortureCase($"binop/{op}/{ln}-{rn}",
                        prelude + "realm kernel { entry func Main() { " + body + " } }", Expect.Any);
                }
    }

    #endregion

    #region Assignment matrix
    /// <summary>
    /// Every assignment operator crossed with every kind of target. Plain '=' and the compound
    /// forms take different resolver paths, as do indexed and field targets, so a target illegal
    /// for one form is easy to leave legal for another.
    /// </summary>
    private static IEnumerable<TortureCase> AssignmentMatrix()
    {
        string[] ops = ["=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>="];
        (string Name, string Target)[] targets =
        [
            ("local",       "n"),
            ("field",       "obj.n"),
            ("array-elem",  "arr[0]"),
            ("array-bad",   "arr[true]"),
            ("indexer",     "box[0]"),
            ("literal",     "1"),
            ("call",        "Two()"),
            ("expr",        "(n + 1)"),
            ("undefined",   "zzz"),
            ("self",        "self"),
            ("class",       "obj"),
            ("bool-local",  "flag"),
            ("string",      "text"),
        ];

        const string prelude = """
            class Thing { public int n; func _init() { self.n = 0; } }
            class Boxy { public operator int func [](int k) { return k; }
                         public operator func []=(int k, int v) { } }
            int func Two() { return 2; }

            """;
        foreach (var op in ops)
            foreach (var (tn, target) in targets)
            {
                string body = "let int n = 0; let bool flag = true; let text = \"s\"; " +
                              "let Thing obj = new Thing(); let arr = [1, 2]; let Boxy box = new Boxy(); " +
                              $"{target} {op} 1;";
                yield return new TortureCase($"assign/{op}/{tn}",
                    prelude + "realm kernel { entry func Main() { " + body + " } }", Expect.Any);
            }
    }

    #endregion

    #region Member access matrix
    /// <summary>
    /// Field reads, method calls and static-vs-instance access crossed with every kind of receiver.
    /// Resolution has one path per receiver shape and the emitter prints '->' for all of them, so a
    /// receiver nobody checked dereferences a non-pointer in C.
    /// </summary>
    private static IEnumerable<TortureCase> MemberAccessMatrix()
    {
        (string Name, string Recv)[] receivers =
        [
            ("instance",  "obj"), ("class-name", "Thing"), ("module-name", "Util"),
            ("enum-name", "Color"), ("int", "1"), ("bool", "true"), ("char", "'c'"),
            ("null",      "null"), ("array", "arr"), ("array-elem", "arr[0]"),
            ("call",      "Two()"), ("paren", "(1 + 2)"), ("undefined", "zzz"),
            ("self",      "self"), ("enum-const", "Color.Red"),
        ];
        (string Name, string Suffix)[] accesses =
        [
            ("field",       ".n"),
            ("unknown",     ".nope"),
            ("method",      ".Get()"),
            ("static",      ".Twice(1)"),
            ("unknown-call", ".Nope()"),
        ];

        const string prelude = """
            enum Color { Red }
            class Thing { public int n; func _init() { self.n = 0; } public int func Get() { return self.n; } }
            module Util { public static int func Twice(int v) { return v * 2; } }
            int func Two() { return 2; }

            """;
        foreach (var (rn, recv) in receivers)
            foreach (var (an, suffix) in accesses)
            {
                string body = $"let Thing obj = new Thing(); let arr = [1, 2]; let v = {recv}{suffix};";
                yield return new TortureCase($"member/{rn}/{an}",
                    prelude + "realm kernel { entry func Main() { " + body + " } }", Expect.Any);
            }
    }

    #endregion

    #region Identifier matrix
    /// <summary>
    /// Awkward identifiers in every kind of declaration. Locals and parameters are the only names
    /// printed as written, so the only ones that can collide; everything else is safe by
    /// construction, which is what stops being true when someone changes the mangler.
    /// </summary>
    private static IEnumerable<TortureCase> IdentifierMatrix()
    {
        string[] names =
        [
            // C keywords that are ordinary Gata identifiers
            "struct", "union", "typedef", "register", "signed", "unsigned", "long", "goto",
            "auto", "extern", "volatile", "restrict", "inline", "const", "do", "float",
            // C library macros that behave like keywords
            "NULL", "bool", "true", "false", "alignas", "static_assert",
            // names the compiler generates for its own temporaries
            "_g0", "_has_error", "_res_v", "_sw0", "_o", "_ixi", "_catch_0",
            // ordinary awkward-but-legal names
            "_", "__", "_unused", "x_", "Main", "self_",
        ];
        (string Name, string Template)[] shapes =
        [
            ("local",  "void func Sink(int v) { } realm kernel { entry func Main() { let int NAME = 1; Sink(NAME); } }"),
            ("param",  "void func H(int NAME) { } realm kernel { entry func Main() { H(1); } }"),
            ("func",   "void func NAME() { } realm kernel { entry func Main() { NAME(); } }"),
            ("class",  "class NAME { public int n; } realm kernel { entry func Main() { let NAME c = new NAME(); } }"),
            ("field",  "class C { public int NAME; } realm kernel { entry func Main() { let C c = new C(); c.NAME = 1; } }"),
            ("enum",   "enum E { NAME } realm kernel { entry func Main() { let E e = E.NAME; } }"),
            ("forin",  "realm kernel { entry func Main() { for NAME in [1, 2] { } } }"),
            ("bind",   "union U { A(int x) } realm kernel { entry func Main() { let U u = U.A(1); match (u) { case A(NAME) { } } } }"),
        ];

        foreach (var n in names)
            foreach (var (sn, tpl) in shapes)
                yield return new TortureCase($"ident/{sn}/{n}", tpl.Replace("NAME", n), Expect.Any);
    }

    #endregion

    #region Curated cases
    /// <summary>
    /// Hand-written cases, each pinning exactly one rule. Unlike the matrices these carry a real
    /// expectation, and where the diagnostic identity matters, the code.
    /// </summary>
    private static IEnumerable<TortureCase> Curated()
    {
        #region 'assign' outside a catch handler
        yield return new("assign/bare-entry",
            "realm kernel { entry func Main() { assign 1; } }", Expect.Rejected, Codes.AssignOutsideCatch);
        yield return new("assign/in-try-block",
            "throws void func T() { throw; } realm kernel { entry func Main() { try { T(); assign 1; } catch { } } }",
            Expect.Rejected, Codes.AssignOutsideCatch);
        yield return new("assign/in-catch-block",
            "throws void func T() { throw; } realm kernel { entry func Main() { try { T(); } catch { assign 1; } } }",
            Expect.Rejected, Codes.AssignOutsideCatch);
        yield return new("assign/in-loop-in-handler",
            "throws int func T() { throw; } realm kernel { entry func Main() { let int v = T() catch { while (true) { assign 0; } }; } }",
            Expect.Any);
        yield return new("assign/nested-func-in-handler",
            "throws int func T() { throw; } void func H() { assign 1; } realm kernel { entry func Main() { H(); } }",
            Expect.Rejected, Codes.AssignOutsideCatch);

        #endregion

        #region catch handlers
        yield return new("catch/no-assign",
            "throws int func T() { throw; } realm kernel { entry func Main() { let int v = T() catch { }; } }",
            Expect.Rejected, Codes.CatchHandlerNoAssign);
        yield return new("catch/partial-assign",
            "throws int func T() { throw; } realm kernel { entry func Main() { let int v = T() catch { if (true) { assign 0; } }; } }",
            Expect.Rejected, Codes.CatchHandlerNoAssign);
        yield return new("catch/both-branches-assign",
            "throws int func T() { throw; } realm kernel { entry func Main() { let int v = T() catch { if (true) { assign 0; } else { assign 1; } }; } }",
            Expect.Accepted);
        yield return new("catch/assign-wrong-type",
            "throws int func T() { throw; } realm kernel { entry func Main() { let int v = T() catch { assign \"s\"; }; } }",
            Expect.Rejected);
        yield return new("catch/on-non-throws-call",
            "int func H() { return 1; } realm kernel { entry func Main() { let int v = H() catch { assign 0; }; } }",
            Expect.Rejected);
        yield return new("catch/on-non-call",
            "realm kernel { entry func Main() { let int v = 1 catch { assign 0; }; } }", Expect.Rejected);
        yield return new("catch/nested-in-handler",
            "throws int func T() { throw; } realm kernel { entry func Main() { let int v = T() catch { let int w = T() catch { assign 1; }; assign w; }; } }",
            Expect.Any);
        yield return new("catch/subexpression",
            "throws int func T() { throw; } void func H(int n) { } realm kernel { entry func Main() { H(T() catch { assign 0; }); } }",
            Expect.Any);
        yield return new("catch/on-void-throws",
            "throws void func T() { throw; } realm kernel { entry func Main() { T() catch { assign 1; }; } }", Expect.Any);
        yield return new("catch/handler-falls-through-to-break",
            "throws int func T() { throw; } realm kernel { entry func Main() { while (true) { let int v = T() catch { break; }; } } }",
            Expect.Any);

        #endregion

        #region try/catch
        yield return new("try/catch-missing",
            "throws void func T() { throw; } realm kernel { entry func Main() { try { T(); } } }", Expect.Rejected);
        yield return new("try/empty-both",
            "realm kernel { entry func Main() { try { } catch { } } }", Expect.Any);
        yield return new("try/return-in-catch",
            "throws void func T() { throw; } int func H() { try { T(); return 1; } catch { return 0; } } realm kernel { entry func Main() { H(); } }",
            Expect.Accepted);
        yield return new("try/throw-in-catch-non-throws",
            "throws void func T() { throw; } void func H() { try { T(); } catch { throw; } } realm kernel { entry func Main() { H(); } }",
            Expect.Rejected);
        yield return new("throws/unhandled",
            "throws void func T() { throw; } realm kernel { entry func Main() { T(); } }", Expect.Rejected, Codes.ThrowsOutsideTry);
        yield return new("throws/void-return-type",
            "throws void func T() { throw; } realm kernel { entry func Main() { try { T(); } catch { } } }", Expect.Accepted);
        // Process variables. The accepted cases matter as much as the rejected ones: they are what
        // the emitted-C suite hands to gcc, and a static plus its gate is code nothing else writes.
        yield return new("procvar/basic",
            "realm kernel { background process P { let int n = 1; thread T { entry func R() { let int a = n; } } } entry func Main() { } }",
            Expect.Accepted);
        yield return new("procvar/written-from-thread",
            "realm kernel { background process P { let int n = 1; thread T { entry func R() { n = n + 1; } } } entry func Main() { } }",
            Expect.Accepted);
        yield return new("procvar/two-threads-share",
            "realm kernel { background process P { let int n = 0; thread A { entry func R() { n = 1; } } thread B { entry func R() { n = 2; } } } entry func Main() { } }",
            Expect.Accepted);
        yield return new("procvar/read-by-process-func",
            "realm kernel { background process P { let int n = 1; int func Get() { return n; } thread T { entry func R() { let int a = Get(); } } } entry func Main() { } }",
            Expect.Accepted);
        yield return new("procvar/same-name-two-processes",
            "realm kernel { background process P { let int n = 1; thread T { entry func R() { let int a = n; } } } background process Q { let int n = 2; thread T { entry func R() { let int a = n; } } } entry func Main() { } }",
            Expect.Accepted);
        yield return new("procvar/union-enum-array",
            "union Shape { Dot, Line(int n) } enum Col { Red, Blue } " +
            "realm kernel { background process P { let Shape s = Shape.Line(7); let Col c = Col.Blue; " +
            "let [4]int a = default([4]int); " +
            "thread T { entry func R() { a[0] = 1; if (c == Col.Blue) { a[1] = 2; } " +
            "match (s) { case Dot { } case Line(v) { a[2] = v; } } } } } entry func Main() { } }",
            Expect.Accepted);
        yield return new("procvar/compound-assign",
            "realm kernel { background process P { let int n = 1; " +
            "thread T { entry func R() { n += 2; n++; n = n * 2; } } } entry func Main() { } }",
            Expect.Accepted);
        yield return new("procvar/initialiser-reads-earlier-one",
            "realm kernel { background process P { let int a = 2; let int b = a * 3; " +
            "thread T { entry func R() { let int x = b; } } } entry func Main() { } }",
            Expect.Accepted);
        // Ordering, not scope: 'a' is a real variable of this process, it just has no value yet
        // when the store above it runs.
        yield return new("procvar/initialiser-reads-later-one",
            "realm kernel { background process P { let int b = a * 3; let int a = 2; " +
            "thread T { entry func R() { let int x = b; } } } entry func Main() { } }",
            Expect.Rejected, Codes.UseBeforeAssignment);
        yield return new("procvar/initialiser-reads-itself",
            "realm kernel { background process P { let int a = a + 1; " +
            "thread T { entry func R() { let int x = a; } } } entry func Main() { } }",
            Expect.Rejected, Codes.UseBeforeAssignment);
        yield return new("procvar/managed-initialiser-reads-itself",
            "class Cell { public int v; func _init() { self.v = 1; } } " +
            "realm kernel { background process P { let Cell c = c; " +
            "thread T { entry func R() { let int x = c.v; } } } entry func Main() { } }",
            Expect.Rejected, Codes.UseBeforeAssignment);
        yield return new("procvar/catch-handler-without-assign",
            "throws int func B(int x) { if (x < 0) { throw; } return x; } " +
            "realm kernel { background process P { let int a = B(1) catch { }; " +
            "thread T { entry func R() { let int x = a; } } } entry func Main() { } }",
            Expect.Rejected, Codes.CatchHandlerNoAssign);
        yield return new("procvar/catch-handler-with-assign",
            "throws int func B(int x) { if (x < 0) { throw; } return x; } " +
            "realm kernel { background process P { let int a = B(1) catch { assign 7; }; " +
            "thread T { entry func R() { let int x = a; } } } entry func Main() { } }",
            Expect.Accepted);
        yield return new("procvar/catch-handler-returns",
            "throws int func B(int x) { if (x < 0) { throw; } return x; } " +
            "realm kernel { background process P { let int a = B(1) catch { return; }; let int b = 2; " +
            "thread T { entry func R() { let int x = a + b; } } } entry func Main() { } }",
            Expect.Rejected, Codes.UninitialisedProcessVar);
        yield return new("procvar/funcptr-called-before-initialised",
            "int func Twice(int x) { return x * 2; } " +
            "realm kernel { background process P { let int a = f(2); let func(int) -> int f = Twice; " +
            "thread T { entry func R() { let int x = a; } } } entry func Main() { } }",
            Expect.Rejected, Codes.UseBeforeAssignment);
        yield return new("procvar/funcptr-called-directly",
            "int func Twice(int x) { return x * 2; } " +
            "realm kernel { background process P { let func(int) -> int f = Twice; " +
            "thread T { entry func R() { let int x = f(3); } } } entry func Main() { } }",
            Expect.Accepted);
        yield return new("procvar/no-initialiser",
            "realm kernel { background process P { let int n; thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.UninitialisedProcessVar);
        yield return new("procvar/not-visible-outside",
            "realm kernel { background process P { let int n = 1; thread T { entry func R() { } } } entry func Main() { let int a = n; } }",
            Expect.Rejected);
        yield return new("procvar/duplicate",
            "realm kernel { background process P { let int n = 1; let int n = 2; thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);
        yield return new("procvar/type-mismatch",
            "realm kernel { background process P { let int n = default([2]int); thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.TypeMismatch);
        yield return new("procvar/in-realm",
            "realm kernel { let int n = 1; entry func Main() { } }", Expect.Rejected, Codes.Syntax);
        yield return new("throws/on-entry",
            "realm kernel { entry throws func Main() { throw; } }", Expect.Rejected);
        yield return new("throw/outside-throws-func",
            "void func H() { throw; } realm kernel { entry func Main() { H(); } }", Expect.Rejected);

        #endregion

        #region defer
        yield return new("defer/return", "void func H() { defer return; } realm kernel { entry func Main() { H(); } }",
            Expect.Rejected, Codes.DeferTransfer);
        yield return new("defer/break", "realm kernel { entry func Main() { while (true) { defer break; } } }",
            Expect.Rejected, Codes.DeferTransfer);
        yield return new("defer/continue", "realm kernel { entry func Main() { while (true) { defer continue; } } }",
            Expect.Rejected, Codes.DeferTransfer);
        yield return new("defer/defer", "realm kernel { entry func Main() { defer defer VoidH(); } } void func VoidH() { }",
            Expect.Rejected);
        yield return new("defer/let", "realm kernel { entry func Main() { defer let int x = 1; } }", Expect.Any);
        yield return new("defer/throw-in-throws",
            "throws void func T() { defer throw; } realm kernel { entry func Main() { try { T(); } catch { } } }", Expect.Any);
        yield return new("defer/nested-defer-in-block",
            "void func H() { } realm kernel { entry func Main() { defer { defer H(); } } }", Expect.Any);

        #endregion

        #region switch
        yield return new("switch/dup-label",
            "realm kernel { entry func Main() { switch (1) { case 1 { } case 1 { } } } }", Expect.Rejected);
        yield return new("switch/no-cases",
            "realm kernel { entry func Main() { switch (1) { } } }", Expect.Any);
        yield return new("switch/only-default",
            "realm kernel { entry func Main() { switch (1) { default { } } } }", Expect.Any);
        // Gata's switch desugars to an if/else-if equality chain, not a C switch, so a
        // case label is an ordinary expression and need not be a compile-time constant.
        yield return new("switch/non-constant-label",
            "realm kernel { entry func Main() { let int n = 1; switch (n) { case n { } } } }", Expect.Any);
        yield return new("switch/string-scrutinee",
            "realm kernel { entry func Main() { switch (\"a\") { case \"a\" { } } } }", Expect.Any);
        yield return new("switch/bool-scrutinee",
            "realm kernel { entry func Main() { switch (true) { case true { } } } }", Expect.Any);
        yield return new("switch/break-in-case",
            "realm kernel { entry func Main() { switch (1) { case 1 { break; } } } }", Expect.Any);

        #endregion

        #region match
        yield return new("match/non-union",
            "realm kernel { entry func Main() { match (1) { case A { } } } }", Expect.Rejected);
        yield return new("match/unknown-variant",
            "union U { A } realm kernel { entry func Main() { let U u = U.A(); match (u) { case Zzz { } } } }", Expect.Rejected);
        yield return new("match/non-exhaustive",
            "union U { A, B } realm kernel { entry func Main() { let U u = U.A(); match (u) { case A { } } } }",
            Expect.Rejected, Codes.NonExhaustiveMatch);
        yield return new("match/dup-variant",
            "union U { A, B } realm kernel { entry func Main() { let U u = U.A(); match (u) { case A { } case A { } case B { } } } }",
            Expect.Rejected);
        yield return new("match/too-many-binds",
            "union U { A(int n), B } realm kernel { entry func Main() { let U u = U.A(1); match (u) { case A(x, y) { } case B { } } } }",
            Expect.Rejected);
        yield return new("match/binds-on-payloadless",
            "union U { A, B } realm kernel { entry func Main() { let U u = U.A(); match (u) { case A(x) { } case B { } } } }",
            Expect.Rejected);
        yield return new("match/no-cases",
            "union U { A } realm kernel { entry func Main() { let U u = U.A(); match (u) { } } }", Expect.Any);

        #endregion

        #region enum / union declarations
        yield return new("enum/empty", "enum E { } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("enum/dup-member", "enum E { A, A } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("enum/non-constant-value",
            "int func H() { return 1; } enum E { A = H() } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("enum/string-value", "enum E { A = \"s\" } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("union/empty", "union U { } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("union/dup-variant", "union U { A, A } realm kernel { entry func Main() { } }", Expect.Rejected);
        // Managed payloads are legal now, so these are Accepted. They pin the declaration only:
        // this corpus builds without libgata, so no reference counting is generated.
        // ManagedUnionTests covers that half by running programs and watching destructors.
        yield return new("union/managed-payload",
            "union U { A(String s) } realm kernel { entry func Main() { } }", Expect.Accepted);
        // A user-class payload is exercised in ManagedUnionTests instead: declaring a class here
        // makes the module ARC-managed, and this corpus builds without libgata, so the run would
        // fail on missing @intrinsic bindings rather than on anything union-related.
        yield return new("union/multi-managed-payload",
            "union U { A(String s, String t) } realm kernel { entry func Main() { } }", Expect.Accepted);
        yield return new("union/nested-managed-union",
            "union Inner { A(String s) } union Outer { W(Inner i), P(int n) } realm kernel { entry func Main() { } }",
            Expect.Accepted);
        yield return new("union/managed-payload-in-class-field",
            "union U { A(String s) } class C { U u; } realm kernel { entry func Main() { } }", Expect.Accepted);
        // A union cannot hold itself by value: the two would have no size. Rejected, not Any -
        // allowing managed payloads did not weaken this, and a regression here is a compiler
        // that recurses forever or emits an incomplete type.
        yield return new("union/self-payload", "union U { A(U u) } realm kernel { entry func Main() { } }",
            Expect.Rejected);
        yield return new("union/mutual-payload",
            "union A { X(B b) } union B { Y(A a) } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("union/dup-field", "union U { A(int x, int x) } realm kernel { entry func Main() { } }", Expect.Rejected);

        // Equality. Two unions of the same type compare structurally through a generated
        // function; anything else keeps the error it always had, so that making unions
        // comparable did not quietly make them comparable to everything.
        yield return new("union/eq-same-type",
            "union U { A(int n), B } realm kernel { entry func Main() { let bool b = U.B() == U.A(1); } }",
            Expect.Accepted);
        yield return new("union/ne-same-type",
            "union U { A(int n), B } realm kernel { entry func Main() { let bool b = U.B() != U.A(1); } }",
            Expect.Accepted);
        yield return new("union/eq-two-different-unions",
            "union U { A } union V { B } realm kernel { entry func Main() { let bool b = U.A() == V.B(); } }",
            Expect.Rejected, Codes.TypeMismatch);
        yield return new("union/eq-union-and-int",
            "union U { A } realm kernel { entry func Main() { let bool b = U.A() == 1; } }",
            Expect.Rejected, Codes.TypeMismatch);
        yield return new("union/relational-still-rejected",
            "union U { A } realm kernel { entry func Main() { let bool b = U.A() < U.A(); } }",
            Expect.Rejected, Codes.TypeMismatch);
        yield return new("union/eq-nested-union",
            "union I { A(int n), B } union O { W(I i), K(int n) } " +
            "realm kernel { entry func Main() { let bool b = O.K(1) == O.W(I.B()); } }", Expect.Accepted);
        yield return new("union/eq-array-payload",
            "union U { A([2]int a), B } realm kernel { entry func Main() { let bool b = U.A([1,2]) == U.B(); } }",
            Expect.Accepted);
        yield return new("union/eq-funcptr-payload",
            "int func Id(int x) { return x; } union U { A(func(int) -> int f), B } " +
            "realm kernel { entry func Main() { let bool b = U.A(Id) == U.B(); } }", Expect.Accepted);
        yield return new("union/eq-enum-payload",
            "enum E { X, Y } union U { A(E e), B } " +
            "realm kernel { entry func Main() { let bool b = U.A(E.X) == U.B(); } }", Expect.Accepted);
        yield return new("union/eq-in-condition",
            "union U { A(int n), B } realm kernel { entry func Main() { if (U.B() == U.A(1)) { } } }",
            Expect.Accepted);
        // Generic unions. The template is replaced by one stamped union per instantiation, so
        // these pin both that the stamping happens and that the rules the non-generic form is
        // held to still apply to the result.
        yield return new("union/generic-basic",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let M[int] m = M.S(1); } }",
            Expect.Accepted);
        yield return new("union/generic-two-instantiations",
            "union M[T] { S(T v), N } " +
            "realm kernel { entry func Main() { let M[int] a = M.S(1); let M[bool] b = M.S(true); } }",
            Expect.Accepted);
        yield return new("union/generic-two-params",
            "union E[A, B] { L(A a), R(B b) } " +
            "realm kernel { entry func Main() { let E[int, bool] e = E.L(1); } }", Expect.Accepted);
        yield return new("union/generic-payload-free-needs-target-type",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let M[int] m = M.N(); } }",
            Expect.Accepted);
        yield return new("union/generic-nested-in-itself-by-value",
            "union M[T] { S(M[T] v), N } realm kernel { entry func Main() { let M[int] m = M.N(); } }",
            Expect.Rejected, Codes.TypeMismatch);
        yield return new("union/generic-mutual-by-value",
            "union A[T] { X(B[T] b), Y } union B[T] { Z(A[T] a), W } " +
            "realm kernel { entry func Main() { let A[int] a = A.Y(); } }", Expect.Rejected, Codes.TypeMismatch);
        yield return new("union/generic-duplicate-type-param",
            "union M[T, T] { S(T v) } realm kernel { entry func Main() { let M[int, int] m = M.S(1); } }",
            Expect.Rejected, Codes.DuplicateName);
        yield return new("union/generic-clashes-with-generic-class",
            "class M[T] { public T v; } union M[T] { S(T v) } realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);
        yield return new("union/generic-void-argument",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let M[void] m = M.N(); } }",
            Expect.Rejected);
        yield return new("union/generic-wrong-arity",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let M[int, int] m = M.N(); } }",
            Expect.Rejected);
        yield return new("union/generic-unknown-variant",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let M[int] m = M.Zzz(1); } }",
            Expect.Rejected);
        yield return new("union/generic-equality",
            "union M[T] { S(T v), N } " +
            "realm kernel { entry func Main() { let M[int] a = M.S(1); let bool b = a == M.S(2); } }",
            Expect.Accepted);
        // Type arguments are settled before any expression is resolved, so a construction on its
        // own cannot ask for an instantiation - the type has to be named somewhere.
        yield return new("union/generic-never-named-as-a-type",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let bool b = M.S(1) == M.S(2); } }",
            Expect.Rejected, Codes.CannotInfer);
        yield return new("union/generic-match-all-arms-return",
            "union M[T] { S(T v), N } " +
            "int func W(M[int] m) { match (m) { case S(v) { return 1; } case N { return 0; } } } " +
            "realm kernel { entry func Main() { let int r = W(M.N()); } }", Expect.Accepted);
        // 'Name[Args].Variant(...)' - the instantiation named outright. The brackets read as
        // both a type list and an index, so these pin that the resolver picks correctly in each
        // direction and that ordinary indexing is untouched.
        yield return new("union/generic-explicit-instantiation",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let M[int] m = M[int].S(1); } }",
            Expect.Accepted);
        yield return new("union/generic-explicit-payload-free",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let M[int] m = M[int].N(); } }",
            Expect.Accepted);
        yield return new("union/generic-explicit-two-params",
            "union E[A, B] { L(A a), R(B b) } " +
            "realm kernel { entry func Main() { let E[int, bool] e = E[int, bool].R(true); } }", Expect.Accepted);
        // The index reading of the same shape. Declaring a class here would make the module
        // ARC-managed, which this libgata-free corpus cannot bind, so the member-access form
        // ('a[i].n') is covered in the execution tests instead.
        yield return new("union/generic-explicit-index-still-parses",
            "realm kernel { entry func Main() { let [2]int a = [1, 2]; let int i = 0; let int n = a[i]; } }",
            Expect.Accepted);
        yield return new("union/generic-explicit-unknown-instantiation",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let M[int] m = M[bool].N(); } }",
            Expect.Rejected);
        yield return new("union/generic-type-used-as-a-value",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { let M[int] a = M[int].N(); let int n = M[int]; } }",
            Expect.Rejected);

        yield return new("union/generic-never-instantiated",
            "union M[T] { S(T v), N } realm kernel { entry func Main() { } }", Expect.Accepted);

        yield return new("union/eq-many-variants",
            "union U { A(int a), B(int b), C(int c), D(int d), E(int e), F } " +
            "realm kernel { entry func Main() { let bool b = U.F() == U.A(1); } }", Expect.Accepted);
        // An exhaustive match whose every arm returns, with no default. Gata's return analysis
        // knows it is total; gcc only does if the lowered chain ends in an else, which is why
        // Desugar collapses the last arm. Without it, gcc sees a path falling off the end.
        yield return new("union/match-all-arms-return",
            "union U { A, B(int n), C(int x, int y) } " +
            "int func W(U u) { match (u) { case A { return 0; } case B(n) { return n; } case C(x, y) { return x + y; } } } " +
            "realm kernel { entry func Main() { let int r = W(U.A()); } }", Expect.Accepted);
        // The same shape with a default arm, which was always fine, so the fix must not have
        // been to special-case the defaultless form into something the default form loses.
        yield return new("union/match-default-returns",
            "union U { A, B(int n) } " +
            "int func W(U u) { match (u) { case A { return 0; } default { return 1; } } } " +
            "realm kernel { entry func Main() { let int r = W(U.A()); } }", Expect.Accepted);

        #endregion

        #region classes
        yield return new("class/dup-field", "class C { int n; int n; } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("class/dup-method",
            "class C { public void func M() { } public void func M() { } } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("class/self-field", "class C { C c; } realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("class/private-access",
            "class C { int n; } realm kernel { entry func Main() { let C c = new C(); let int v = c.n; } }",
            Expect.Rejected, Codes.PrivateMember);
        yield return new("class/static-on-instance",
            "class C { public static void func S() { } } realm kernel { entry func Main() { let C c = new C(); c.S(); } }",
            Expect.Rejected);
        yield return new("class/instance-on-static",
            "class C { public void func M() { } } realm kernel { entry func Main() { C.M(); } }", Expect.Rejected);
        yield return new("class/self-in-static",
            "class C { int n; public static void func S() { self.n = 1; } } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("class/empty", "class C { } realm kernel { entry func Main() { let C c = new C(); } }", Expect.Any);
        yield return new("module/instance-field",
            "module M { int n; } realm kernel { entry func Main() { } }", Expect.Rejected, Codes.ModuleField);
        yield return new("module/new",
            "module M { public static void func F() { } } realm kernel { entry func Main() { let M m = new M(); } }", Expect.Rejected);

        #endregion

        #region operators
        yield return new("operator/wrong-arity",
            "class C { public operator C func +(C a, C b) { return a; } } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("operator/no-return",
            "class C { public operator C func +(C o) { } } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("operator/dup",
            "class C { public operator C func +(C o) { return o; } public operator C func +(C o) { return o; } } realm kernel { entry func Main() { } }",
            Expect.Rejected);
        yield return new("operator/index-get-no-set",
            "class C { public operator int func [](int k) { return k; } } realm kernel { entry func Main() { let C c = new C(); c[0] = 1; } }",
            Expect.Rejected, Codes.NoIndexSetter);
        yield return new("operator/mod-not-overloadable",
            "class C { public operator C func %(C o) { return o; } } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("operator/on-module",
            "module M { public operator int func +(int a) { return a; } } realm kernel { entry func Main() { } }", Expect.Any);

        #endregion

        #region generics
        yield return new("generic/unknown-param",
            "class Box[T] { T v; public void func Set(U x) { } } realm kernel { entry func Main() { let Box[int] b = new Box[int](); } }",
            Expect.Rejected);
        yield return new("generic/arity-mismatch",
            "class Box[T] { T v; } realm kernel { entry func Main() { let Box[int, int] b = new Box[int, int](); } }", Expect.Rejected);
        yield return new("generic/uninstantiated",
            "class Box[T] { T v; } realm kernel { entry func Main() { let Box b = new Box(); } }", Expect.Rejected);
        yield return new("generic/func-uninferable",
            "T func Id[T](int n) { return default(T); } realm kernel { entry func Main() { Id(1); } }", Expect.Any);
        yield return new("generic/recursive-instantiation",
            "class Box[T] { T v; } realm kernel { entry func Main() { let Box[Box[int]] b = new Box[Box[int]](); } }", Expect.Any);

        #endregion

        #region control flow
        yield return new("cf/break-outside-loop",
            "realm kernel { entry func Main() { break; } }", Expect.Rejected, Codes.BreakOutsideLoop);
        yield return new("cf/continue-outside-loop",
            "realm kernel { entry func Main() { continue; } }", Expect.Rejected, Codes.BreakOutsideLoop);
        yield return new("cf/missing-return",
            "int func H() { } realm kernel { entry func Main() { H(); } }", Expect.Rejected, Codes.MissingReturn);
        yield return new("cf/return-value-from-void",
            "void func H() { return 1; } realm kernel { entry func Main() { H(); } }", Expect.Rejected);
        yield return new("cf/return-nothing-from-int",
            "int func H() { return; } realm kernel { entry func Main() { H(); } }", Expect.Rejected);
        yield return new("cf/unreachable",
            "int func H() { return 1; VoidH(); } void func VoidH() { } realm kernel { entry func Main() { H(); } }", Expect.Any);
        yield return new("cf/cond-not-bool",
            "realm kernel { entry func Main() { if (1) { } } }", Expect.Rejected, Codes.ConditionNotBool);
        yield return new("cf/infinite-for-missing-cond",
            "realm kernel { entry func Main() { for (;;) { break; } } }", Expect.Any);
        yield return new("cf/entry-call",
            "realm kernel { entry func Main() { Main(); } }", Expect.Rejected, Codes.CallToEntry);

        #endregion

        #region realm scopes
        // The declaration two realms may each make. Accepted, so EmittedCCompilesTests puts the
        // generated C through the host compiler - which is where a name collision would surface as
        // a duplicate struct rather than as anything the front end could have caught.
        yield return new("scope/same-class-in-both-realms", """
            realm kernel {
                class Config { public int n; }
                void func Use(Config c) { }
                entry func Main() { }
            }
            realm userspace {
                class Config { public int n; }
                void func Use(Config c) { }
                foreground process App {
                    thread T { entry func Run() { } }
                }
            }
            """, Expect.Accepted);

        yield return new("scope/same-enum-and-union-in-both-realms", """
            realm kernel {
                enum Mode { Idle }
                union Msg { Ping, Pong(int n) }
                entry func Main() { let Mode m = Mode.Idle; let Msg s = Msg.Ping(); }
            }
            realm userspace {
                enum Mode { Idle }
                union Msg { Ping, Pong(int n) }
                foreground process App {
                    thread T { entry func Run() { let Mode m = Mode.Idle; let Msg s = Msg.Pong(1); } }
                }
            }
            """, Expect.Accepted);

        // A realm-scoped declaration is not visible from the enclosing scope. This is the whole
        // point of the feature, so it gets a case rather than only a unit test.
        yield return new("scope/realm-type-not-visible-outside",
            "void func Take(Config c) { } realm kernel { class Config { int n; } entry func Main() { } }",
            Expect.Rejected, Codes.ScopedNameNotVisible);

        yield return new("scope/realm-type-not-visible-from-sibling",
            "realm kernel { class Config { int n; } entry func Main() { } } " +
            "realm userspace { void func Take(Config c) { } }",
            Expect.Rejected, Codes.ScopedNameNotVisible);

        // A realm may shadow a top-level name; the inner one wins, silently.
        yield return new("scope/realm-shadows-top-level", """
            class Config { public int narrow; }
            realm kernel {
                // Displaces the top-level Config, and says so. The inner one wins here; the outer
                // one stays what it was everywhere else.
                @shadows class Config { public int wide; }
                void func Use(Config c) { let int v = c.wide; }
                entry func Main() { }
            }
            """, Expect.Accepted);

        // The same program without the annotation. Shadowing is legal, never implicit.
        yield return new("scope/realm-shadows-top-level-unmarked",
            "class Config { public int narrow; } " +
            "realm kernel { class Config { public int wide; } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        // Marked but displacing nothing: the annotation has to mean something where it is written.
        yield return new("scope/shadows-nothing",
            "realm kernel { @shadows class Config { public int n; } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        // Every declaration kind that can be scoped, shadowing the same kind at the top level -
        // marked, then not. A kind the annotation forgets is one that shadows in silence.
        yield return new("scope/shadows-kind-class",
            "class Twin { int n; } realm kernel { @shadows class Twin { int m; } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-kind-class-unmarked",
            "class Twin { int n; } realm kernel { class Twin { int m; } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("scope/shadows-kind-module",
            "module Twin { public static int func F() { return 1; } } realm kernel { @shadows module Twin { public static int func F() { return 2; } } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-kind-module-unmarked",
            "module Twin { public static int func F() { return 1; } } realm kernel { module Twin { public static int func F() { return 2; } } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("scope/shadows-kind-enum",
            "enum Twin { A } realm kernel { @shadows enum Twin { B } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-kind-enum-unmarked",
            "enum Twin { A } realm kernel { enum Twin { B } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("scope/shadows-kind-union",
            "union Twin { A(int n) } realm kernel { @shadows union Twin { B(int n) } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-kind-union-unmarked",
            "union Twin { A(int n) } realm kernel { union Twin { B(int n) } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("scope/shadows-kind-func",
            "void func Twin() { } realm kernel { @shadows void func Twin() { } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-kind-func-unmarked",
            "void func Twin() { } realm kernel { void func Twin() { } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("scope/shadows-kind-generic",
            "class Twin[T] { public T v; } realm kernel { @shadows class Twin[T] { public T w; } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-kind-generic-unmarked",
            "class Twin[T] { public T v; } realm kernel { class Twin[T] { public T w; } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("scope/shadows-kind-generic-union",
            "union Twin[T] { A(T v), B } realm kernel { @shadows union Twin[T] { C(T v), D } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-kind-generic-union-unmarked",
            "union Twin[T] { A(T v), B } realm kernel { union Twin[T] { C(T v), D } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("scope/shadows-kind-native-type",
            "native type Twin { int fd; } realm kernel { @shadows native type Twin { int gd; } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-kind-native-type-unmarked",
            "native type Twin { int fd; } realm kernel { native type Twin { int gd; } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        // A process shadowing its own realm, one level further in.
        yield return new("scope/shadows-process-over-realm",
            "realm kernel { class Twin { int n; } foreground process P { @shadows class Twin { int m; } " +
            "thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-process-over-realm-unmarked",
            "realm kernel { class Twin { int n; } foreground process P { class Twin { int m; } " +
            "thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        // A process shadowing the file's root past a realm that declares nothing of the name.
        yield return new("scope/shadows-process-over-root",
            "class Twin { int n; } realm kernel { foreground process P { @shadows class Twin { int m; } " +
            "thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Accepted);

        // Sibling realms cannot see each other, so neither shadows the other and marking is wrong.
        yield return new("scope/sibling-realms-do-not-shadow",
            "realm kernel { class Twin { int n; } entry func Main() { } } " +
            "realm userspace { class Twin { int m; } foreground process P { thread T { entry func R() { } } } }",
            Expect.Accepted);

        yield return new("scope/sibling-realms-marked-is-an-error",
            "realm kernel { class Twin { int n; } entry func Main() { } } " +
            "realm userspace { @shadows class Twin { int m; } foreground process P { thread T { entry func R() { } } } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        // Sibling processes cannot see each other either.
        yield return new("scope/sibling-processes-do-not-shadow",
            "realm kernel { foreground process A { class Twin { int n; } thread T { entry func R() { } } } " +
            "background process B { class Twin { int m; } thread U { entry func R() { } } } entry func Main() { } }",
            Expect.Accepted);

        // A declaration written before the one it shadows: the realm's Twin comes after the process.
        yield return new("scope/shadows-declaration-written-later",
            "realm kernel { foreground process P { @shadows class Twin { int m; } thread T { entry func R() { } } } " +
            "class Twin { int n; } entry func Main() { } }",
            Expect.Accepted);

        // Illegal positions: the annotation belongs on a scoped declaration and nowhere else.
        yield return new("scope/shadows-on-class-member",
            "class C { @shadows int func F() { return 1; } } realm kernel { entry func Main() { } }",
            Expect.Rejected);

        // Every position that is not a scoped declaration. The list is the answer to "what can be
        // shadowed", so it is pinned rather than left to the reader of DeclareItem.
        yield return new("scope/shadows-on-local",
            "realm kernel { entry func Main() { let int x = 1; if (true) { @shadows let int x = 2; } } }",
            Expect.Rejected);

        yield return new("scope/shadows-on-field",
            "class Twin { int n; } realm kernel { class C { @shadows int Twin; } entry func Main() { } }",
            Expect.Rejected, Codes.BadAnnotation);

        yield return new("scope/shadows-on-method",
            "void func Helper() { } " +
            "realm kernel { class C { @shadows public int func Helper() { return 1; } } entry func Main() { } }",
            Expect.Rejected, Codes.WrongAnnotationKind);

        yield return new("scope/shadows-on-param",
            "void func Twin() { } realm kernel { void func F(@shadows int Twin) { } entry func Main() { } }",
            Expect.Rejected);

        yield return new("scope/shadows-on-thread",
            "realm kernel { foreground process P { @shadows thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.BadAnnotation);

        yield return new("scope/shadows-on-enum-member",
            "class A { int n; } realm kernel { enum E { @shadows A } entry func Main() { } }",
            Expect.Rejected);

        yield return new("scope/shadows-on-union-variant",
            "class A { int n; } realm kernel { union U { @shadows A(int n) } entry func Main() { } }",
            Expect.Rejected);

        yield return new("scope/shadows-on-import",
            "@shadows import gata; realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.BadAnnotation);

        yield return new("scope/shadows-on-extern",
            "@extern int func puts(String s); " +
            "realm kernel { @shadows @extern int func puts(String s); entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("scope/shadows-on-process",
            "realm kernel { @shadows foreground process P { thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.BadAnnotation);

        yield return new("scope/shadows-on-realm",
            "@shadows realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.BadAnnotation);

        yield return new("scope/shadows-on-native-block",
            "realm kernel { @shadows native { int zz; } entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("scope/shadows-on-entry-func",
            "class Main { int n; } realm kernel { @shadows entry func Main() { } }",
            Expect.Rejected);

        // Two marks on one declaration, and the mark beside another annotation.
        yield return new("scope/shadows-twice",
            "class Twin { int n; } realm kernel { @shadows @shadows class Twin { int m; } entry func Main() { } }",
            Expect.Any);

        yield return new("scope/shadows-beside-keep",
            "module Twin { public static int func F() { return 1; } } " +
            "realm kernel { @keep @shadows module Twin { public static int func F() { return 2; } } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/shadows-at-root-shadows-nothing",
            "@shadows class Config { public int n; } realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        // Same name twice in one scope is still a collision - shadowing is between scopes, never
        // within one. Pinned here because the message names a scoped type, and a raw internal
        // spelling reaching a user is a bug AllDiagnosticsAreWellFormed watches for.
        yield return new("scope/duplicate-generic-in-one-realm",
            "realm kernel { class Box[T] { T v; } class Box[T] { T w; } entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        yield return new("scope/duplicate-class-in-one-realm",
            "realm kernel { class Cargo { int n; } class Cargo { int w; } entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        yield return new("scope/duplicate-class-in-one-process",
            "realm kernel { foreground process P { class Cargo { int n; } class Cargo { int w; } " +
            "thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        // Three levels, one name at each. The discriminator is the field name: every Cargo has a
        // different one, so if two stamps of Box[Cargo] ever merged, a field access would fail to
        // resolve rather than silently compiling to the wrong type.
        yield return new("scope/three-levels-of-the-same-name", """
            class Box[T] { public T v; }
            class Cargo { public int root; }
            void func UseRoot(Box[Cargo] b) { let int n = b.v.root; }
            realm kernel {
                @shadows class Box[T] { public T v; }
                @shadows class Cargo { public int inRealm; }
                void func UseRealm(Box[Cargo] b) { let int n = b.v.inRealm; }
                foreground process P {
                    @shadows class Box[T] { public T v; }
                    @shadows class Cargo { public int inProc; }
                    void func UseProc(Box[Cargo] b) { let int n = b.v.inProc; }
                    thread T { entry func Run() { } }
                }
                entry func Main() { }
            }
            """, Expect.Accepted);

        // A local owns its name against a scoped declaration exactly as it does against a root one.
        // The rewrite that qualifies scoped names walked bare identifiers blindly, so each of these
        // read as the type and the emitted C never got as far as a compiler.
        yield return new("scope/local-named-like-a-scoped-type", """
            realm kernel {
                class Cfg { public int a; }
                module M { public static int func G() { return 7; } }
                int func Param(int Cfg) { return Cfg; }
                int func Field(Cfg before) { return before.a; }
                entry func Main() {
                    let int Cfg = 5;
                    let int q = Cfg + Param(1) + M.G();
                    for (let int M = 0; M < 2; M++) { let int inner = M; }
                    let int after = M.G();
                }
            }
            """, Expect.Accepted);

        // One meaning per name in a scope. A type and a function of one name are both reachable at
        // root only because nothing there can shadow them; a scoped declaration takes the whole
        // name, so the pairing has no answer and was accepted in silence.
        yield return new("scope/type-and-func-of-one-name",
            "class Twin { public int n; } int func Twin() { return 1; } realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        yield return new("scope/type-and-func-of-one-name-in-realm",
            "realm kernel { class Twin { public int n; } int func Twin() { return 1; } entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        yield return new("scope/generic-and-plain-of-one-name",
            "realm kernel { class Box { public int n; } class Box[T] { public T v; } entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        yield return new("scope/process-and-type-of-one-name",
            "realm kernel { class P { public int n; } foreground process P { thread T { entry func R() { } } } " +
            "entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        // A scoped declaration takes over its whole name, so a use in the wrong position finds it
        // rather than the outer one it displaced - and must be told exactly that, once.
        yield return new("scope/scoped-func-read-as-a-type",
            "class Twin { public int n; } realm kernel { @shadows int func Twin() { return 1; } " +
            "entry func Main() { let Twin v = new Twin(); } }",
            Expect.Rejected, Codes.UndefinedType);

        yield return new("scope/scoped-type-read-as-a-function",
            "int func Twin() { return 1; } realm kernel { @shadows class Twin { public int n; } " +
            "entry func Main() { let int n = Twin(); } }",
            Expect.Rejected, Codes.UndefinedMethod);

        // The repeat declares nothing, including when it holds the only scoped declarations in the
        // build - the rewrite that empties it used to be skipped as a no-op in exactly that case.
        yield return new("scope/duplicate-process-declares-nothing",
            "realm kernel { foreground process P { thread T { entry func R() { } } } " +
            "foreground process P { int func Only() { return 7; } } entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        // An @extern names a C symbol under its own spelling and cannot be qualified, but a scoped
        // declaration of that name still takes the name over.
        yield return new("scope/shadows-an-extern",
            "@extern int func gata_probe(int n); realm kernel { @shadows int func gata_probe(int n) { return n; } " +
            "entry func Main() { let int q = gata_probe(1); } }",
            Expect.Accepted);

        yield return new("scope/shadows-an-extern-unmarked",
            "@extern int func gata_probe(int n); realm kernel { int func gata_probe(int n) { return n; } " +
            "entry func Main() { } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        #region Scope qualifiers

        // What '@shadows' declares, a qualifier undoes: each level's name stays reachable from
        // inside the one that displaced it. Every accepted case here is linked, because a qualifier
        // resolving to the wrong symbol still compiles per translation unit.
        yield return new("qualify/realm-from-process", """
            realm kernel {
                int func Step() { return 1; }
                foreground process P {
                    @shadows int func Step() { return 2; }
                    thread T { entry func R() { let int a = Step(); let int b = kernel.Step(); } }
                }
                entry func Main() { }
            }
            """, Expect.Accepted);

        yield return new("qualify/root-from-realm",
            "int func Step() { return 1; } realm kernel { @shadows int func Step() { return 2; } " +
            "entry func Main() { let int a = Step(); let int b = ::Step(); } }",
            Expect.Accepted);

        yield return new("qualify/every-level-from-a-thread", """
            int func D() { return 1; }
            realm userspace {
                @shadows int func D() { return 2; }
                foreground process P {
                    @shadows int func D() { return 3; }
                    thread T {
                        entry func R() { let int q = D() + userspace.P.D() + userspace.D() + ::D(); }
                    }
                }
            }
            realm kernel { entry func Main() { } }
            """, Expect.Accepted);

        // A type position, which is most of what shadowing applies to: four of the six forms a
        // '@shadows' may mark are types, so an expression-only qualifier would be half a feature.
        yield return new("qualify/type-position", """
            class Cargo { public int root; }
            realm kernel {
                @shadows class Cargo { public int inr; }
                class Holder { public ::Cargo held; }
                int func Outer(::Cargo c) { return c.root; }
                int func Inner(Cargo c) { return c.inr; }
                entry func Main() { }
            }
            """, Expect.Accepted);

        yield return new("qualify/process-type-from-a-thread",
            "realm kernel { foreground process P { class C { public int a; } " +
            "int func Read(kernel.P.C x) { return x.a; } thread T { entry func R() { } } } " +
            "entry func Main() { } }",
            Expect.Accepted);

        // A qualified type argument: the stamp must be over the scope the argument names, not the
        // one the instantiation was written in.
        yield return new("qualify/generic-argument",
            "class Box[T] { public T v; } class Cargo { public int r; } " +
            "realm kernel { @shadows class Cargo { public int k; } " +
            "void func U(Box[::Cargo] b) { let int n = b.v.r; } entry func Main() { } }",
            Expect.Accepted);

        yield return new("qualify/generic-base",
            "class Box[T] { public T v; } " +
            "realm kernel { @shadows class Box[T] { public T w; } class Cargo { public int a; } " +
            "void func U(::Box[Cargo] b) { let int n = b.v.a; } entry func Main() { } }",
            Expect.Accepted);

        // A module reached past a class that displaced its name, then its member: the segment split
        // that only the scope tree can make.
        yield return new("qualify/module-member",
            "module Algo { public static int func Min(int a, int b) { return a; } } " +
            "realm kernel { @shadows class Algo { public int Min; } " +
            "entry func Main() { let int z = ::Algo.Min(1, 2); } }",
            Expect.Accepted);

        // Outward only. A sibling realm and a sibling process are exactly what scopes exist to
        // separate, so naming one is an error rather than a way in.
        yield return new("qualify/sibling-realm",
            "realm kernel { class Cfg { public int a; } entry func Main() { } } " +
            "realm userspace { void func F() { let kernel.Cfg c = new kernel.Cfg(); } }",
            Expect.Rejected, Codes.ScopeNotEnclosing);

        yield return new("qualify/sibling-process",
            "realm kernel { foreground process A { class Cfg { public int a; } thread T { entry func R() { } } } " +
            "foreground process B { thread T { entry func R() { let kernel.A.Cfg c = new kernel.A.Cfg(); } } } " +
            "entry func Main() { } }",
            Expect.Rejected, Codes.ScopeNotEnclosing);

        yield return new("qualify/realm-from-root",
            "realm kernel { int func Step() { return 1; } entry func Main() { } } " +
            "int func Outer() { return kernel.Step(); }",
            Expect.Rejected, Codes.ScopeNotEnclosing);

        yield return new("qualify/no-such-scope",
            "realm kernel { entry func Main() { let int z = kernel.Q.Nope(); } }",
            Expect.Rejected, Codes.UnknownInScope);

        // A qualifier names one exact scope, so a name it does not declare is an error rather than
        // a quiet walk further out.
        yield return new("qualify/name-not-in-scope",
            "realm kernel { entry func Main() { let int z = kernel.Nope(); } }",
            Expect.Rejected, Codes.UnknownInScope);

        yield return new("qualify/name-not-at-root",
            "realm kernel { entry func Main() { let int z = ::Nope(); } }",
            Expect.Rejected, Codes.UnknownInScope);

        // The qualifier does not replace '@shadows': one declares the intent, the other reaches
        // past it, and writing the second never excuses omitting the first.
        yield return new("qualify/does-not-excuse-shadows",
            "int func Step() { return 1; } realm kernel { int func Step() { return 2; } " +
            "entry func Main() { let int z = ::Step(); } }",
            Expect.Rejected, Codes.UnmarkedShadow);

        yield return new("qualify/redundant-is-allowed",
            "realm kernel { int func Step() { return 1; } entry func Main() { let int z = kernel.Step(); } }",
            Expect.Accepted);

        yield return new("qualify/bare-realm-is-not-a-value",
            "realm kernel { entry func Main() { let int z = kernel; } }",
            Expect.Rejected, Codes.Syntax);

        yield return new("qualify/realm-name-is-reserved",
            "class userspace { public int a; } realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.Syntax);

        // Every readable C name joins its parts with '_', which is also legal inside each part, so
        // two differently split names can spell the same symbol. Reported here rather than left to
        // the C compiler, which names it against a generated symbol the author never wrote.
        yield return new("qualify/method-name-join-collides",
            "class A_B { public int func M() { return 1; } } class A { public int func B_M() { return 2; } } " +
            "realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        yield return new("qualify/thread-name-join-collides",
            "realm kernel { entry func Main() { } } " +
            "realm userspace { foreground process A_B { thread T { entry func R() { } } } " +
            "foreground process A { thread B_T { entry func R() { } } } }",
            Expect.Rejected, Codes.DuplicateName);

        // Realms are separate namespaces, so one process name in each is legal - and has to reach
        // the emitter as two symbols, or each translation unit defines the same thread entry.
        yield return new("qualify/one-process-name-per-realm",
            "realm kernel { foreground process App { thread T { entry func R() { } } } entry func Main() { } } " +
            "realm userspace { foreground process App { thread T { entry func R() { } } } }",
            Expect.Accepted);

        // An '@extern' names a C symbol that already exists, so it is the one declaration that can
        // take over a name the compiler generates - and it links, binding to the generated body.
        yield return new("qualify/extern-takes-a-generated-name",
            "@extern void func uapps(); realm kernel { entry func Main() { uapps(); } } " +
            "realm userspace { foreground process P { thread T { entry func R() { } } } }",
            Expect.Rejected, Codes.DuplicateName);

        yield return new("qualify/extern-takes-the-entry-symbol",
            "@extern void func gata_kernelspace_main(); realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        // A process name is a runtime string, not a C identifier, so a C keyword is fine.
        yield return new("qualify/process-named-like-a-c-keyword",
            "realm kernel { entry func Main() { } } " +
            "realm userspace { foreground process register { thread T { entry func R() { } } } }",
            Expect.Accepted);

        #endregion

        // A generic template may be declared inside a scope, and its stamps follow it there.
        yield return new("scope/generic-class-in-realm",
            "realm kernel { class Box[T] { public T v; } void func Use(Box[int] b) { } entry func Main() { } }",
            Expect.Accepted);

        yield return new("scope/generic-func-in-realm",
            "realm kernel { T func Id[T](T v) { return v; } entry func Main() { let int n = Id(1); } }",
            Expect.Accepted);

        // The bar the whole design was aimed at: one template name per realm, one per process, each
        // stamped over its own scope's type, all four distinct in the emitted C.
        yield return new("scope/same-generic-in-both-realms", """
            realm kernel {
                class Box[T] { public T v; }
                class Cargo { public int n; }
                void func Use(Box[Cargo] b) { }
                entry func Main() { }
            }
            realm userspace {
                class Box[T] { public T v; }
                class Cargo { public int n; }
                void func Use(Box[Cargo] b) { }
                foreground process App { thread T { entry func Run() { } } }
            }
            """, Expect.Accepted);

        yield return new("scope/same-generic-in-two-processes", """
            realm kernel { entry func Main() { } }
            realm userspace {
                foreground process One {
                    class Box[T] { public T v; }
                    class Cargo { public int n; }
                    void func Use(Box[Cargo] b) { }
                    thread T { entry func Run() { } }
                }
                background process Two {
                    class Box[T] { public T v; }
                    class Cargo { public int w; }
                    void func Use(Box[Cargo] b) { }
                    thread T { entry func Run() { } }
                }
            }
            """, Expect.Accepted);

        // A scoped generic over a scoped type, and the outer generic of the same name over the
        // outer type, side by side.
        yield return new("scope/scoped-generic-shadows-top-level-generic", """
            class Box[T] { public T v; }
            class Cargo { public int outer; }
            realm kernel {
                @shadows class Box[T] { public T v; }
                @shadows class Cargo { public int inner; }
                void func Inside(Box[Cargo] b) { }
                entry func Main() { }
            }
            void func Outside(Box[Cargo] b) { }
            """, Expect.Accepted);

        // A generic union declared in a scope, whose variant payload is a scoped type.
        yield return new("scope/generic-union-in-realm", """
            realm kernel {
                enum Grade { Low, High }
                union Slot[T] { Full(T v), Empty }
                void func Use(Slot[Grade] s) { }
                entry func Main() { }
            }
            """, Expect.Accepted);

        // A top-level generic instantiated over a realm-scoped type: the template stays global, the
        // argument is scoped, and the stamp is per-realm. This is the case that must keep working.
        yield return new("scope/top-level-generic-over-realm-type", """
            class Box[T] { public T v; }
            realm kernel {
                class Config { public int n; }
                void func Use(Box[Config] b) { }
                entry func Main() { }
            }
            """, Expect.Accepted);

        // A process is the address space on the target, so a declaration here is shared by exactly
        // the threads that share memory. Accepted, so the emitted C goes through the host compiler.
        yield return new("scope/process-scoped-declarations", """
            realm kernel { entry func Main() { } }
            realm userspace {
                foreground process App {
                    class Frame { public int w; }
                    enum Phase { Boot, Ready }
                    int func Step(int n) { return n + 1; }
                    thread A { entry func Run() { let Phase p = Phase.Boot; let int n = Step(1); } }
                    thread B { entry func Run() { let int n = Step(2); } }
                }
            }
            """, Expect.Accepted);

        yield return new("scope/same-names-in-two-processes", """
            realm kernel { entry func Main() { } }
            realm userspace {
                foreground process One {
                    class Frame { public int w; }
                    int func Step(int n) { return n + 1; }
                    thread T { entry func Run() { let int n = Step(1); } }
                }
                background process Two {
                    class Frame { public int h; }
                    int func Step(int n) { return n + 2; }
                    thread T { entry func Run() { let int n = Step(1); } }
                }
            }
            """, Expect.Accepted);

        // Symmetric: a kernel-realm process is a process too.
        yield return new("scope/process-scope-in-kernel-realm", """
            realm kernel {
                foreground process Svc {
                    class Frame { public int w; }
                    int func Step(int n) { return n + 1; }
                    thread T { entry func Run() { let int n = Step(1); } }
                }
                entry func Main() { }
            }
            """, Expect.Accepted);

        // A process's declarations are its own: the realm around it cannot see them.
        yield return new("scope/process-type-not-visible-from-realm",
            "realm kernel { foreground process P { class Frame { int w; } thread T { entry func R() { } } } " +
            "void func Take(Frame f) { } entry func Main() { } }",
            Expect.Rejected, Codes.ScopedNameNotVisible);

        yield return new("scope/process-type-not-visible-from-sibling-process",
            "realm kernel { entry func Main() { } } realm userspace { " +
            "foreground process One { class Frame { int w; } thread T { entry func R() { } } } " +
            "background process Two { void func Take(Frame f) { } thread T { entry func R() { } } } }",
            Expect.Rejected, Codes.ScopedNameNotVisible);

        // A process may shadow a realm name, and the innermost wins.
        yield return new("scope/process-shadows-realm", """
            realm kernel {
                class Frame { public int outer; }
                foreground process P {
                    @shadows class Frame { public int inner; }
                    void func Take(Frame f) { let int v = f.inner; }
                    thread T { entry func R() { } }
                }
                entry func Main() { }
            }
            """, Expect.Accepted);

        yield return new("scope/entry-func-in-process",
            "realm kernel { foreground process P { entry func Oops() { } thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.EntryOutsideKernel);

        yield return new("scope/nested-process",
            "realm kernel { foreground process P { foreground process Q { } thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.InvalidNesting);

        // A type argument that is itself an instantiation is one flat string on the request, and no
        // scope declares that string - so the request has to be rebuilt from the structural spec or
        // it names a stamp nothing will ever produce.
        yield return new("scope/nested-generic-argument", """
            realm kernel {
                class Box[T] { public T v; }
                class Cargo { public int n; }
                void func Use(Box[Box[Cargo]] b) { let int n = b.v.v.n; }
                entry func Main() { }
            }
            """, Expect.Accepted);

        yield return new("scope/nested-generic-argument-outer-template", """
            class Box[T] { public T v; }
            realm kernel {
                class Cargo { public int n; }
                void func Use(Box[Box[Cargo]] b) { let int n = b.v.v.n; }
                entry func Main() { }
            }
            """, Expect.Accepted);

        // A function declared in a process, called from the realm around it. The name exists but
        // not here, and saying only "undefined" would be a lie about what is wrong.
        yield return new("scope/process-func-not-visible-from-realm",
            "realm kernel { foreground process P { int func Step(int n) { return n; } thread T { entry func R() { } } } " +
            "entry func Main() { let int n = Step(1); } }",
            Expect.Rejected, Codes.ScopedNameNotVisible);

        // Topology has to live where a realm can place it. A process outside every realm used to
        // parse clean and then emit nothing at all - threads included.
        yield return new("scope/process-outside-realm",
            "foreground process P { thread T { entry func R() { } } } realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.TopologyOutsideRealm);

        yield return new("scope/thread-outside-process",
            "realm kernel { thread T { entry func R() { } } entry func Main() { } }",
            Expect.Rejected, Codes.TopologyOutsideRealm);

        yield return new("scope/import-inside-realm",
            "realm kernel { import gata; entry func Main() { } }",
            Expect.Rejected, Codes.TopologyOutsideRealm);

        yield return new("scope/import-inside-process",
            "realm kernel { foreground process P { import gata; thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.TopologyOutsideRealm);

        yield return new("scope/environment-inside-process",
            "realm kernel { foreground process P { @environment thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.MisplacedEnvironment);

        // Two processes of one name intern to one scope, so their declarations merge. Reported at
        // the process, not as a collision between the declarations it dragged together.
        yield return new("scope/duplicate-process-in-one-realm",
            "realm kernel { foreground process P { class A { int n; } thread T { entry func R() { } } } " +
            "foreground process P { class A { int m; } thread U { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.DuplicateName);

        yield return new("scope/same-process-name-across-realms",
            "realm kernel { foreground process P { thread T { entry func R() { } } } entry func Main() { } } " +
            "realm userspace { foreground process P { thread T { entry func R() { } } } }",
            Expect.Accepted);

        yield return new("scope/realm-generic-not-visible-outside",
            "realm kernel { class Box[T] { public T v; } entry func Main() { } } void func Use(Box[int] b) { }",
            Expect.Rejected, Codes.ScopedNameNotVisible);
        #endregion

        #region realms / structure
        yield return new("struct/no-entry", "void func H() { }", Expect.Rejected);
        yield return new("struct/two-entries",
            "realm kernel { entry func A() { } entry func B() { } }", Expect.Rejected);
        yield return new("struct/nested-kernel", "realm kernel { realm kernel { } }", Expect.Rejected);
        yield return new("struct/dup-kernel",
            "realm kernel { entry func Main() { } } realm kernel { }", Expect.Rejected);
        yield return new("struct/entry-outside-kernel",
            "entry func Main() { }", Expect.Rejected);
        yield return new("struct/panic-in-user",
            "realm kernel { entry func Main() { } } realm userspace { void func H() { panic \"x\"; } }", Expect.Any);

        #endregion

        #region process / thread
        yield return new("proc/no-mode", "realm kernel { process P { thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.MissingProcessMode);
        yield return new("proc/mode-twice",
            "realm kernel { foreground process P : background { thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected);
        yield return new("proc/thread-mode",
            "realm kernel { foreground process P { background thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.ThreadModeNotAllowed);
        yield return new("proc/empty", "realm kernel { foreground process P { } entry func Main() { } }", Expect.Any);
        yield return new("proc/thread-entry-params",
            "realm kernel { foreground process P { thread T { entry func R(int n) { } } } entry func Main() { } }", Expect.Rejected);
        yield return new("proc/dup-thread",
            "realm kernel { foreground process P { thread T { entry func R() { } } thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected);

        #endregion

        #region unsafe / pointers
        yield return new("unsafe/deref-outside",
            "realm kernel { entry func Main() { let int n = 1; let int* p = &n; let int v = *p; } }",
            Expect.Rejected, Codes.UnsafeRequired);
        yield return new("unsafe/addrof-outside",
            "realm kernel { entry func Main() { let int n = 1; let int* p = &n; } }", Expect.Any);
        yield return new("unsafe/nested",
            "realm kernel { entry func Main() { unsafe { unsafe { } } } }", Expect.Any);
        yield return new("unsafe/deref-int",
            "realm kernel { entry func Main() { unsafe { let int n = 1; let int v = *n; } } }", Expect.Rejected);
        yield return new("unsafe/addrof-literal",
            "realm kernel { entry func Main() { unsafe { let int* p = &1; } } }", Expect.Rejected);

        #endregion

        #region casts
        yield return new("cast/int-to-bool", "realm kernel { entry func Main() { let bool b = 1 as bool; } }", Expect.Any);
        yield return new("cast/to-void", "realm kernel { entry func Main() { let v = 1 as void; } }", Expect.Rejected);
        yield return new("cast/class-to-int",
            "class C { } realm kernel { entry func Main() { let C c = new C(); let int v = c as int; } }", Expect.Rejected);
        yield return new("cast/prim-paren-deref",
            "realm kernel { entry func Main() { unsafe { let int n = 1; let int* p = &n; let int v = (int)*p; } } }", Expect.Any);
        yield return new("cast/redundant", "realm kernel { entry func Main() { let int v = 1 as int; } }", Expect.Any);

        #endregion

        #region literals / lexer edges
        yield return new("lit/int-overflow", "realm kernel { entry func Main() { let int v = 99999999999999999999; } }", Expect.Rejected);
        yield return new("lit/hex", "realm kernel { entry func Main() { let int v = 0xFF; } }", Expect.Accepted);
        yield return new("lit/bad-hex", "realm kernel { entry func Main() { let int v = 0xZZ; } }", Expect.Rejected);
        yield return new("lit/empty-char", "realm kernel { entry func Main() { let char c = ''; } }", Expect.Rejected);
        yield return new("lit/multi-char", "realm kernel { entry func Main() { let char c = 'ab'; } }", Expect.Rejected);
        yield return new("lit/bad-escape", "realm kernel { entry func Main() { let s = \"\\q\"; } }", Expect.Rejected);
        yield return new("lit/unterminated-str", "realm kernel { entry func Main() { let s = \"abc; } }", Expect.Rejected);
        yield return new("lit/interp-no-holes", "realm kernel { entry func Main() { let s = $\"plain\"; } }", Expect.Any);
        yield return new("lit/interp-empty-hole", "realm kernel { entry func Main() { let s = $\"{}\"; } }", Expect.Rejected);
        yield return new("lit/interp-unclosed-hole", "realm kernel { entry func Main() { let s = $\"{1\"; } }", Expect.Rejected);

        #endregion

        #region trailing commas (each list form)
        yield return new("comma/enum", "enum E { A, } realm kernel { entry func Main() { } }", Expect.Rejected, Codes.TrailingComma);
        yield return new("comma/union", "union U { A, } realm kernel { entry func Main() { } }", Expect.Rejected, Codes.TrailingComma);
        yield return new("comma/union-fields", "union U { A(int x,) } realm kernel { entry func Main() { } }", Expect.Rejected, Codes.TrailingComma);
        yield return new("comma/params", "void func H(int a,) { } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("comma/args", "void func H(int a) { } realm kernel { entry func Main() { H(1,); } }", Expect.Rejected);
        yield return new("comma/array-lit", "realm kernel { entry func Main() { let a = [1,]; } }", Expect.Rejected);
        yield return new("comma/coll-init", "realm kernel { entry func Main() { let a = new [2]int { 1, }; } }", Expect.Rejected);
        yield return new("comma/generic-params", "class Box[T,] { T v; } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("comma/match-binds",
            "union U { A(int x) } realm kernel { entry func Main() { let U u = U.A(1); match (u) { case A(x,) { } } } }", Expect.Rejected);
        yield return new("comma/switch-labels", "realm kernel { entry func Main() { switch (1) { case 1, { } } } }", Expect.Rejected);

        #endregion

        #region declaration headers
        yield return new("decl/dup-modifier", "realm kernel { entry func Main() { } } public public void func H() { }", Expect.Rejected);
        yield return new("decl/public-private", "class C { public private void func M() { } } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("decl/static-free-func", "static void func H() { } realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.StaticOnFreeFunc);
        yield return new("decl/return-type-after-params", "realm kernel { entry func Main() { } } func H() -> int { }", Expect.Rejected);
        yield return new("decl/dup-param", "void func H(int a, int a) { } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("decl/dup-free-func", "void func H() { } void func H() { } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("decl/generic-param-generic", "class Box[T[U]] { } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("decl/func-ptr-ptr", "realm kernel { entry func Main() { let func(int) -> int* f = null; } }", Expect.Any);
        yield return new("decl/nested-class", "class A { class B { } } realm kernel { entry func Main() { } }", Expect.Rejected);

        #endregion

        #region annotations
        // @keep is what roots a symbol named only from raw C, so it must survive DCE - which is why
        // the class form needs the ARC runtime a corpus case has no way to supply, and is pinned by
        // KeepIsAcceptedWhereItIsConsumed instead.
        yield return new("ann/keep-on-module",
            "@keep module M { public static int func F() { return 1; } } realm kernel { entry func Main() { } }",
            Expect.Accepted);
        yield return new("ann/keep-on-free-func",
            "@keep int func F() { return 1; } realm kernel { entry func Main() { } }", Expect.Accepted);

        yield return new("ann/on-enum", "@keep enum E { A } realm kernel { entry func Main() { } }", Expect.Rejected, Codes.BadAnnotation);
        yield return new("ann/on-field", "class C { @keep int n; } realm kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("ann/unknown-intrinsic",
            "@intrinsic(not_a_real_role) native { } realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("ann/env-misplaced",
            "realm kernel { entry func Main() { } @environment }", Expect.Rejected);

        #endregion

        #region warnings that must not be errors
        yield return new("warn/unused-var", "realm kernel { entry func Main() { let int unused = 1; } }", Expect.Accepted);
        yield return new("warn/self-assign", "realm kernel { entry func Main() { let int v = 1; v = v; } }", Expect.Accepted);
        yield return new("warn/const-cond", "realm kernel { entry func Main() { if (true) { VoidH(); } } void func VoidH() { } }", Expect.Any);

        #endregion

        #region deep but bounded nesting
        yield return new("depth/parens", "realm kernel { entry func Main() { let int v = " +
            new string('(', 300) + "1" + new string(')', 300) + "; } }", Expect.Rejected);
        yield return new("depth/blocks", "realm kernel { entry func Main() { " +
            string.Concat(Enumerable.Repeat("{", 300)) + string.Concat(Enumerable.Repeat("}", 300)) + " } }", Expect.Rejected);
        yield return new("depth/unary", "realm kernel { entry func Main() { let int v = " +
            new string('-', 300) + "1; } }", Expect.Rejected);
        yield return new("depth/generic-type",
            "realm kernel { entry func Main() { let " + string.Concat(Enumerable.Repeat("Box[", 300)) + "int" +
            new string(']', 300) + " b = null; } }", Expect.Rejected);

        #endregion

        #region truncation / unbalanced input
        yield return new("trunc/open-brace", "realm kernel { entry func Main() {", Expect.Rejected);
        yield return new("trunc/open-paren", "realm kernel { entry func Main() { VoidH(", Expect.Rejected);
        yield return new("trunc/empty", "", Expect.Rejected);
        yield return new("trunc/only-brace", "}", Expect.Rejected);
        yield return new("trunc/stray-semi", ";", Expect.Rejected);
        yield return new("trunc/keyword-only", "class", Expect.Rejected);
        yield return new("trunc/dangling-else", "realm kernel { entry func Main() { else { } } }", Expect.Rejected);
        yield return new("trunc/dangling-catch", "realm kernel { entry func Main() { catch { } } }", Expect.Rejected);
        yield return new("trunc/dangling-case", "realm kernel { entry func Main() { case 1 { } } }", Expect.Rejected);
    }

    /// <summary>
    /// A second curated batch, covering the areas the first pass did not reach: 'ref' parameters,
    /// the for..in protocol, interpolation, scoping, native blocks, and the interactions between
    /// throws, generics, and catch handlers.
    /// </summary>
    private static IEnumerable<TortureCase> Curated2()
    {

        #endregion

        #region ref parameters
        yield return new("ref/basic",
            "void func Bump(ref int n) { n = n + 1; } realm kernel { entry func Main() { let int v = 1; Bump(ref v); } }",
            Expect.Accepted);
        yield return new("ref/missing-keyword",
            "void func Bump(ref int n) { } realm kernel { entry func Main() { let int v = 1; Bump(v); } }",
            Expect.Rejected, Codes.RefArgMismatch);
        yield return new("ref/unexpected-keyword",
            "void func Take(int n) { } realm kernel { entry func Main() { let int v = 1; Take(ref v); } }",
            Expect.Rejected, Codes.RefArgMismatch);
        yield return new("ref/literal-arg",
            "void func Bump(ref int n) { } realm kernel { entry func Main() { Bump(ref 1); } }", Expect.Rejected);
        yield return new("ref/call-arg",
            "int func Two() { return 2; } void func Bump(ref int n) { } realm kernel { entry func Main() { Bump(ref Two()); } }",
            Expect.Rejected);
        // Not Accepted: any class at all makes ValidateIntrinsics demand the ARC role set,
        // which a corpus case importing no libgata cannot bind.
        yield return new("ref/field-arg",
            "class C { public int n; func _init() { self.n = 0; } } void func Bump(ref int n) { } realm kernel { entry func Main() { let C c = new C(); Bump(ref c.n); } }",
            Expect.Any);
        yield return new("ref/elem-arg",
            "void func Bump(ref int n) { } realm kernel { entry func Main() { let a = [1, 2]; Bump(ref a[0]); } }",
            Expect.Accepted);
        yield return new("ref/type-mismatch",
            "void func Bump(ref int n) { } realm kernel { entry func Main() { let bool b = true; Bump(ref b); } }",
            Expect.Rejected);
        yield return new("ref/on-class",
            "class C { } void func Take(ref C c) { } realm kernel { entry func Main() { let C c = new C(); Take(ref c); } }",
            Expect.Any);
        yield return new("ref/in-operator",
            "class C { public operator C func +(ref C o) { return o; } } realm kernel { entry func Main() { } }", Expect.Any);

        #endregion

        #region for..in protocol
        yield return new("forin/array", "realm kernel { entry func Main() { for x in [1, 2] { } } }", Expect.Any);
        yield return new("forin/int", "realm kernel { entry func Main() { for x in 1 { } } }",
            Expect.Rejected, Codes.NotIterable);
        yield return new("forin/no-protocol",
            "class C { } realm kernel { entry func Main() { let C c = new C(); for x in c { } } }",
            Expect.Rejected, Codes.NotIterable);
        yield return new("forin/length-only",
            "class C { public int func Length() { return 0; } } realm kernel { entry func Main() { let C c = new C(); for x in c { } } }",
            Expect.Rejected);
        yield return new("forin/wrong-get-arity",
            "class C { public int func Length() { return 0; } public int func Get() { return 0; } } realm kernel { entry func Main() { let C c = new C(); for x in c { } } }",
            Expect.Rejected);
        yield return new("forin/full-protocol",
            "class C { public int func Length() { return 0; } public int func Get(int i) { return i; } } realm kernel { entry func Main() { for x in new C() { } } }",
            Expect.Any);
        yield return new("forin/assign-to-binding",
            "realm kernel { entry func Main() { for x in [1, 2] { x = 5; } } }", Expect.Any);
        yield return new("forin/shadow-outer",
            "realm kernel { entry func Main() { let int x = 1; for x in [1, 2] { } } }", Expect.Any);
        yield return new("forin/nested-same-name",
            "realm kernel { entry func Main() { for x in [1, 2] { for x in [3, 4] { } } } }", Expect.Any);

        #endregion

        #region interpolation
        yield return new("interp/class-operand",
            "class C { } realm kernel { entry func Main() { let C c = new C(); let s = $\"{c}\"; } }", Expect.Any);
        yield return new("interp/void-operand",
            "void func H() { } realm kernel { entry func Main() { let s = $\"{H()}\"; } }", Expect.Rejected);
        yield return new("interp/array-operand",
            "realm kernel { entry func Main() { let a = [1, 2]; let s = $\"{a}\"; } }", Expect.Any);
        yield return new("interp/nested-interp",
            "realm kernel { entry func Main() { let s = $\"{$\"{1}\"}\"; } }", Expect.Any);
        yield return new("interp/assign-inside",
            "realm kernel { entry func Main() { let int v = 0; let s = $\"{v = 1}\"; } }", Expect.Rejected);
        yield return new("interp/throws-inside",
            "throws int func T() { throw; } realm kernel { entry func Main() { let s = $\"{T()}\"; } }", Expect.Rejected);
        yield return new("interp/many-parts",
            "realm kernel { entry func Main() { let s = $\"a{1}b{2}c{3}d{4}e\"; } }", Expect.Any);
        yield return new("interp/plain-string-no-dollar",
            "realm kernel { entry func Main() { let s = \"{1}\"; } }", Expect.Any);

        #endregion

        #region scoping and shadowing
        yield return new("scope/redeclare-same-block",
            "realm kernel { entry func Main() { let int v = 1; let int v = 2; } }", Expect.Any);
        yield return new("scope/shadow-param",
            "void func H(int p) { let int p = 1; } realm kernel { entry func Main() { H(1); } }", Expect.Any);
        yield return new("scope/shadow-self",
            "class C { public void func M() { let int self = 1; } } realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("scope/use-before-decl",
            "realm kernel { entry func Main() { let int a = b; let int b = 1; } }", Expect.Rejected);
        yield return new("scope/self-referential-init",
            "realm kernel { entry func Main() { let int a = a; } }", Expect.Rejected);
        yield return new("scope/leak-from-if",
            "realm kernel { entry func Main() { if (true) { let int a = 1; } let int b = a; } }", Expect.Rejected);
        yield return new("scope/leak-from-for",
            "realm kernel { entry func Main() { for (let int i = 0; i < 1; i++) { } let int j = i; } }", Expect.Rejected);
        yield return new("scope/name-shadows-class",
            "class C { } realm kernel { entry func Main() { let int C = 1; } }", Expect.Any);
        yield return new("scope/name-shadows-func",
            "void func H() { } realm kernel { entry func Main() { let int H = 1; } }", Expect.Any);

        #endregion

        #region throws x generics x catch
        yield return new("throws/generic-func",
            "throws T func Pick[T](T v) { return v; } realm kernel { entry func Main() { let int x = Pick(1) catch { assign 0; }; } }",
            Expect.Any);
        yield return new("throws/generic-class-method",
            "class Box[T] { public throws T func Get() { throw; } } realm kernel { entry func Main() { let Box[int] b = new Box[int](); let int v = b.Get() catch { assign 0; }; } }",
            Expect.Any);
        yield return new("throws/returns-class",
            "class C { } throws C func Make() { throw; } realm kernel { entry func Main() { let C c = Make() catch { assign new C(); }; } }",
            Expect.Any);
        yield return new("throws/in-operator",
            "class C { public throws operator C func +(C o) { return o; } } realm kernel { entry func Main() { } }",
            Expect.Rejected);
        // A constructor is a method named '_init'; 'func C()' on class C is just a method.
        yield return new("throws/in-ctor",
            "class C { throws func _init() { throw; } } realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.LifecycleThrows);
        yield return new("throws/in-dtor",
            "class C { throws func _deinit() { throw; } } realm kernel { entry func Main() { } }",
            Expect.Rejected, Codes.LifecycleThrows);
        yield return new("ctor/named-like-class",
            "class C { public void func C() { } } realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("throws/nested-call-arg",
            "throws int func T() { throw; } void func H(int n) { } realm kernel { entry func Main() { try { H(T()); } catch { } } }",
            Expect.Rejected);
        yield return new("throws/in-condition",
            "throws bool func T() { throw; } realm kernel { entry func Main() { try { if (T()) { } } catch { } } }",
            Expect.Rejected);
        yield return new("throws/in-return",
            "throws int func T() { throw; } throws int func H() { return T(); } realm kernel { entry func Main() { try { H(); } catch { } } }",
            Expect.Rejected);
        yield return new("throws/double-call-one-stmt",
            "throws int func T() { throw; } realm kernel { entry func Main() { try { let int v = T() + T(); } catch { } } }",
            Expect.Rejected);
        yield return new("throws/catch-in-catch-block",
            "throws int func T() { throw; } realm kernel { entry func Main() { try { T(); } catch { let int v = T() catch { assign 0; }; } } }",
            Expect.Any);

        #endregion

        #region operator overload interactions
        yield return new("op/compound-uses-binary",
            "class C { public operator C func +(C o) { return o; } } realm kernel { entry func Main() { let C a = new C(); a += a; } }",
            Expect.Any);
        yield return new("op/compound-no-binary",
            "class C { } realm kernel { entry func Main() { let C a = new C(); a += a; } }", Expect.Rejected);
        yield return new("op/indexer-compound",
            "class C { public operator int func [](int k) { return k; } public operator func []=(int k, int v) { } } realm kernel { entry func Main() { let C c = new C(); c[0] += 1; } }",
            Expect.Any);
        yield return new("op/setter-without-getter-compound",
            "class C { public operator func []=(int k, int v) { } } realm kernel { entry func Main() { let C c = new C(); c[0] += 1; } }",
            Expect.Rejected);
        yield return new("op/unary-not",
            "class C { public operator bool func !() { return true; } } realm kernel { entry func Main() { let C c = new C(); let bool b = !c; } }",
            Expect.Any);
        yield return new("op/as-conversion",
            "class C { public operator C func as(int n) { return new C(); } } realm kernel { entry func Main() { let C c = 1 as C; } }",
            Expect.Any);
        yield return new("op/as-wrong-direction",
            "class C { public operator int func as(C c) { return 1; } } realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("op/private-access",
            "class C { private operator C func +(C o) { return o; } } realm kernel { entry func Main() { let C a = new C(); let C b = a + a; } }",
            Expect.Rejected, Codes.PrivateMember);
        yield return new("op/eq-returns-int",
            "class C { public operator int func ==(C o) { return 1; } } realm kernel { entry func Main() { } }", Expect.Any);

        #endregion

        #region generics stress
        yield return new("gen/method-on-generic-class",
            "class Box[T] { T v; public U func Map[U](U seed) { return seed; } } realm kernel { entry func Main() { let Box[int] b = new Box[int](); let int r = b.Map(1); } }",
            Expect.Any);
        yield return new("gen/same-param-name-nested",
            "class Box[T] { public T func Id[T](T v) { return v; } } realm kernel { entry func Main() { let Box[int] b = new Box[int](); } }",
            Expect.Any);
        yield return new("gen/generic-throws-in-handler",
            "throws T func Pick[T](T v) { return v; } realm kernel { entry func Main() { let int a = Pick(1) catch { let int b = Pick(2) catch { assign 0; }; assign b; }; } }",
            Expect.Any);
        yield return new("gen/instantiate-with-void",
            "class Box[T] { T v; } realm kernel { entry func Main() { let Box[void] b = new Box[void](); } }", Expect.Rejected);
        yield return new("gen/instantiate-with-self",
            "class Box[T] { T v; } realm kernel { entry func Main() { let Box[Box[Box[int]]] b = new Box[Box[Box[int]]](); } }",
            Expect.Any);
        yield return new("gen/param-shadows-class",
            "class Thing { } class Box[Thing] { Thing v; } realm kernel { entry func Main() { let Box[int] b = new Box[int](); } }",
            Expect.Any);
        yield return new("gen/unused-param",
            "class Box[T] { int n; } realm kernel { entry func Main() { let Box[int] b = new Box[int](); } }", Expect.Any);

        // A generic function whose body builds a generic type over its own parameter. Generic
        // types are stamped by the Monomorphizer over the AST while generic functions are stamped
        // later, during resolution, so 'Box[T]' only becomes 'Box[Widget]' after the pass that
        // creates it has run. The pipeline now discovers these and runs the front end again with
        // them seeded; these pin that the ordinary shapes compile rather than being rejected.
        yield return new("gen/function-body-instantiates-a-generic-type",
            "class Box[T] { public T v; func _init(T x) { self.v = x; } } class Widget { public int n; } " +
            "T func Wrap[T](T x) { let Box[T] b = new Box[T](x); return b.v; } " +
            "realm kernel { entry func Main() { let Widget w = Wrap(new Widget()); } }",
            Expect.Any);
        yield return new("gen/method-body-instantiates-a-generic-type",
            "class Box[T] { public T v; func _init(T x) { self.v = x; } } class Widget { public int n; } " +
            "class Util { public T func Wrap[T](T x) { let Box[T] b = new Box[T](x); return b.v; } } " +
            "realm kernel { entry func Main() { let Util u = new Util(); let Widget w = u.Wrap(new Widget()); } }",
            Expect.Any);
        // A family with no fixed point: creating each level only reveals the next. Seeding is capped
        // by nesting depth so this terminates with one diagnostic instead of looping. Written over a
        // union of an unmanaged payload so the case needs no reference-counting runtime.
        yield return new("gen/function-body-instantiation-never-settles",
            "union Cell[T] { None, Some(T t) } " +
            "int func Bad[T](T x) { let Cell[T] c = Cell[T].Some(x); return Bad(c); } " +
            "realm kernel { entry func Main() { let int r = Bad(1); } }",
            Expect.Rejected, Codes.UndefinedType);

        // The positive controls - the seeded workaround, and a generic class body doing the same
        // thing legally - need real ARC intrinsics, so they live in the multi-file corpus, which has
        // an environment. See 'gen/function-body-instantiation-seeded-concretely' there.

        #endregion

        #region native blocks and fields
        yield return new("native/top-level", "native { int x; } realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("native/method-body",
            "class C { public int func M() native { return 1; } } realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("native/fields-block",
            "class C { fields { int raw; } } realm kernel { entry func Main() { let C c = new C(); } }", Expect.Any);
        yield return new("native/statement",
            "realm kernel { entry func Main() { native { int local = 1; } } }", Expect.Any);
        yield return new("native/unbalanced-brace",
            "realm kernel { entry func Main() { } } native { if (1) { }", Expect.Rejected);
        yield return new("native/empty", "native { } realm kernel { entry func Main() { } }", Expect.Any);

        #endregion

        #region compound and unary numeric edges
        yield return new("num/bitwise-on-double",
            "realm kernel { entry func Main() { let double d = 1.5; let v = d & 1; } }", Expect.Rejected);
        yield return new("num/mod-on-double",
            "realm kernel { entry func Main() { let double d = 1.5; let v = d % 2.0; } }", Expect.Any);
        yield return new("num/shift-negative",
            "realm kernel { entry func Main() { let v = 1 << -1; } }", Expect.Any);
        yield return new("num/not-on-int",
            "realm kernel { entry func Main() { let v = !1; } }", Expect.Rejected);
        yield return new("num/bitnot-on-bool",
            "realm kernel { entry func Main() { let v = ~true; } }", Expect.Rejected);
        yield return new("num/neg-on-bool",
            "realm kernel { entry func Main() { let v = -true; } }", Expect.Rejected);
        yield return new("num/neg-on-string",
            "realm kernel { entry func Main() { let v = -\"s\"; } }", Expect.Rejected);
        // Nested unary minus: the two signs must not be printed adjacent, or C reads "--".
        yield return new("num/double-negate", "realm kernel { entry func Main() { let v = -(-1); } }", Expect.Any);
        yield return new("num/triple-negate", "realm kernel { entry func Main() { let int n = 1; let v = -(-(-n)); } }", Expect.Any);
        yield return new("num/negate-postfix", "realm kernel { entry func Main() { let int n = 1; let v = -(n++); } }", Expect.Any);
        yield return new("num/double-bitnot", "realm kernel { entry func Main() { let v = ~(~1); } }", Expect.Any);
        yield return new("num/double-not", "realm kernel { entry func Main() { let v = !(!true); } }", Expect.Any);
        yield return new("num/int-div-zero-const",
            "realm kernel { entry func Main() { let v = 1 / 0; } }", Expect.Any);

        #endregion

        #region import edges
        yield return new("import/unknown", "import NoSuchModule; realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("import/empty-path", "import \"\"; realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("import/after-decls", "realm kernel { entry func Main() { } } import gata;", Expect.Any);
        yield return new("import/duplicate", "import gata; import gata; realm kernel { entry func Main() { } }", Expect.Any);
        yield return new("import/inside-kernel", "realm kernel { import gata; entry func Main() { } }", Expect.Rejected);

        #endregion
    }

    #endregion
}
