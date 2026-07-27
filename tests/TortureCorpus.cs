namespace Appa.Tests;

/// <summary>
/// What a torture case is expected to do. The corpus deliberately contains programs
/// that are syntactically plausible but semantically nonsense, so most cases are
/// <see cref="Rejected"/>: the point of the suite is that the compiler says *why*
/// instead of crashing, silently accepting, or emitting broken C.
/// </summary>
public enum Expect
{
    /// <summary>The compiler must produce at least one error diagnostic.</summary>
    Rejected,

    /// <summary>The program is legal and must check without any error.</summary>
    Accepted,

    /// <summary>
    /// Either outcome is defensible; only the no-crash / well-formed-output
    /// properties are asserted. Used for machine-generated combinations whose
    /// legality depends on context.
    /// </summary>
    Any
}

/// <summary>One torture case: a name for failure messages, a source program, and its expectation.</summary>
public sealed record TortureCase(string Name, string Source, Expect Expect, string? Code = null)
{
    public override string ToString() => Name;
}

/// <summary>
/// Builds the torture corpus.
///
/// Three generators feed it:
///
/// 1. <see cref="Curated"/>   -- hand-written cases pinning one specific rule each.
/// 2. <see cref="StatementMatrix"/> -- every "weird statement" placed in every
///    syntactic position a statement can occupy. This is what catches the
///    "does an 'assign' inside a try/catch block parse, and is the diagnostic
///    good?" class of hole, where a construct is only rejected in the position
///    its author happened to think about.
/// 3. <see cref="ExpressionMatrix"/> -- the same idea for expression positions.
///
/// The matrices are combinatorial rather than exhaustive over the grammar: they
/// cross the positions that actually differ in how they reach the checker.
/// </summary>
public static class TortureCorpus
{
    // Lazy rather than a static initializer: the matrices read the position/probe
    // arrays declared further down, which a field initializer here would run before.
    private static IReadOnlyList<TortureCase>? _all;

    /// <summary>The full corpus: curated cases plus every generated matrix.</summary>
    public static IReadOnlyList<TortureCase> All =>
        _all ??= [.. Curated(), .. Curated2(), .. StatementMatrix(), .. ExpressionMatrix(),
                  .. DeclarationMatrix(), .. TypeMatrix(), .. BinaryOperatorMatrix(),
                  .. AssignmentMatrix(), .. MemberAccessMatrix(), .. IdentifierMatrix()];

    #region Statement matrix
    /// <summary>
    /// Every syntactic position that can hold a statement. "%S%" is the hole.
    /// Each template is otherwise a complete, valid program, so any diagnostic
    /// produced is attributable to the injected statement.
    /// </summary>
    private static readonly (string Name, string Template)[] StmtPositions =
    [
        ("free-func",     "void func H() { %S% } kernel { entry func Main() { H(); } }"),
        ("entry",         "kernel { entry func Main() { %S% } }"),
        ("class-method",  "class C { public void func M() { %S% } } kernel { entry func Main() { let C c = new C(); c.M(); } }"),
        ("module-method", "module M { public static void func F() { %S% } } kernel { entry func Main() { M.F(); } }"),
        ("ctor",          "class C { func _init() { %S% } } kernel { entry func Main() { let C c = new C(); } }"),
        ("operator",      "class C { int n; public operator C func +(C o) { %S% return self; } } kernel { entry func Main() { let C a = new C(); let C b = a + a; } }"),
        ("try",           "throws void func T() { throw; } kernel { entry func Main() { try { T(); %S% } catch { } } }"),
        ("catch",         "throws void func T() { throw; } kernel { entry func Main() { try { T(); } catch { %S% } } }"),
        ("catch-handler", "throws int func T() { throw; } kernel { entry func Main() { let int v = T() catch { %S% assign 0; }; } }"),
        ("defer",         "kernel { entry func Main() { defer %S% } }"),
        ("while-body",    "kernel { entry func Main() { while (false) { %S% } } }"),
        ("for-body",      "kernel { entry func Main() { for (let int i = 0; i < 1; i++) { %S% } } }"),
        ("forin-body",    "kernel { entry func Main() { let a = [1, 2]; for x in a { %S% } } }"),
        ("if-then",       "kernel { entry func Main() { if (true) { %S% } } }"),
        ("else",          "kernel { entry func Main() { if (true) { } else { %S% } } }"),
        ("switch-case",   "kernel { entry func Main() { switch (1) { case 1 { %S% } } } }"),
        ("switch-def",    "kernel { entry func Main() { switch (1) { case 1 { } default { %S% } } } }"),
        ("match-case",    "union U { A, B } kernel { entry func Main() { let U u = U.A(); match (u) { case A { %S% } case B { } } } }"),
        ("match-def",     "union U { A, B } kernel { entry func Main() { let U u = U.A(); match (u) { case A { } default { %S% } } } }"),
        ("unsafe",        "kernel { entry func Main() { unsafe { %S% } } }"),
        ("nested-block",  "kernel { entry func Main() { { { %S% } } } }"),
        ("thread-entry",  "kernel { foreground process P { thread T { entry func Run() { %S% } } } entry func Main() { } }"),
    ];

    /// <summary>
    /// Statements that are legal somewhere and nonsense elsewhere. Each must be
    /// either accepted or rejected with a diagnostic in every position above --
    /// never crash, never fall through into the emitter.
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
    /// Crosses every statement probe with every statement position. Legality varies
    /// by position, so the expectation is <see cref="Expect.Any"/>: the assertions
    /// that matter here are no-crash and well-formed output.
    /// </summary>
    private static IEnumerable<TortureCase> StatementMatrix()
    {
        foreach (var (pn, tpl) in StmtPositions)
            foreach (var (sn, stmt) in StmtProbes)
                yield return new TortureCase($"stmt/{pn}/{sn}", tpl.Replace("%S%", stmt), Expect.Any);
    }

    #endregion

    #region Expression matrix
    /// <summary>Every syntactic position that can hold an expression. "%E%" is the hole.</summary>
    private static readonly (string Name, string Template)[] ExprPositions =
    [
        ("let-init",      "kernel { entry func Main() { let v = %E%; } }"),
        ("let-typed",     "kernel { entry func Main() { let int v = %E%; } }"),
        ("expr-stmt",     "kernel { entry func Main() { %E%; } }"),
        ("if-cond",       "kernel { entry func Main() { if (%E%) { } } }"),
        ("while-cond",    "kernel { entry func Main() { while (%E%) { } } }"),
        ("for-cond",      "kernel { entry func Main() { for (let int i = 0; %E%; i++) { } } }"),
        ("for-init",      "kernel { entry func Main() { for (%E%; false; ) { } } }"),
        ("for-step",      "kernel { entry func Main() { for (; false; %E%) { } } }"),
        ("forin-subject", "kernel { entry func Main() { for x in %E% { } } }"),
        ("switch-scrut",  "kernel { entry func Main() { switch (%E%) { case 1 { } } } }"),
        ("switch-label",  "kernel { entry func Main() { switch (1) { case %E% { } } } }"),
        ("match-scrut",   "union U { A } kernel { entry func Main() { match (%E%) { case A { } } } }"),
        ("return-val",    "int func H() { return %E%; } kernel { entry func Main() { H(); } }"),
        ("call-arg",      "void func H(int n) { } kernel { entry func Main() { H(%E%); } }"),
        ("ref-arg",       "void func H(ref int n) { } kernel { entry func Main() { H(ref %E%); } }"),
        ("index",         "kernel { entry func Main() { let a = [1, 2]; let int v = a[%E%]; } }"),
        ("array-elem",    "kernel { entry func Main() { let a = [%E%, 1]; } }"),
        ("ternary-then",  "kernel { entry func Main() { let v = true ? %E% : 0; } }"),
        ("ternary-cond",  "kernel { entry func Main() { let v = %E% ? 1 : 0; } }"),
        ("interp",        "kernel { entry func Main() { let s = $\"v={%E%}\"; } }"),
        ("field-init",    "class C { int n = %E%; } kernel { entry func Main() { let C c = new C(); } }"),
        ("enum-value",    "enum E { A = %E% } kernel { entry func Main() { let E e = E.A; } }"),
        ("new-arg",       "class C { func _init(int n) { } } kernel { entry func Main() { let C c = new C(%E%); } }"),
        ("coll-init",     "kernel { entry func Main() { let a = new [2]int { %E%, 0 }; } }"),
        ("assign-rhs",    "kernel { entry func Main() { let int v = 0; v = %E%; } }"),
        ("assign-lhs",    "kernel { entry func Main() { %E% = 1; } }"),
        ("binop-left",    "kernel { entry func Main() { let v = %E% + 1; } }"),
        ("unary-not",     "kernel { entry func Main() { let v = !%E%; } }"),
        ("cast-operand",  "kernel { entry func Main() { let v = %E% as bool; } }"),
        ("member-target", "kernel { entry func Main() { let v = (%E%).Length; } }"),
    ];

    /// <summary>Expressions ranging from legal to structurally invalid in most positions.</summary>
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
    /// Crosses every expression probe with every expression position. Two helpers
    /// (a void function and a throwing function) are prepended so the probes that
    /// reference them resolve rather than failing for an unrelated reason.
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
    /// Every container a declaration can syntactically appear in. "%D%" is the hole.
    /// Containers differ in which declaration forms they accept, and the rules are
    /// spread across ParseTopLevel, ParseContextItem, ParseClassMember and the
    /// process/thread parsers -- so a form rejected in one is easy to forget in another.
    /// </summary>
    private static readonly (string Name, string Template)[] DeclPositions =
    [
        ("top-level",  "%D% kernel { entry func Main() { } }"),
        ("kernel",     "kernel { %D% entry func Main() { } }"),
        ("user",       "kernel { entry func Main() { } } user { %D% }"),
        ("class",      "class Holder { %D% } kernel { entry func Main() { } }"),
        ("module",     "module Holder { %D% } kernel { entry func Main() { } }"),
        ("process",    "kernel { foreground process P { %D% thread T { entry func R() { } } } entry func Main() { } }"),
        ("thread",     "kernel { foreground process P { thread T { %D% entry func R() { } } } entry func Main() { } }"),
        ("func-body",  "kernel { entry func Main() { %D% } }"),
    ];

    /// <summary>Declaration forms, each legal in some containers and not others.</summary>
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
        ("kernel-block",  "kernel { }"),
        ("user-block",    "user { }"),
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
    ];

    /// <summary>Crosses every declaration form with every container that could hold one.</summary>
    private static IEnumerable<TortureCase> DeclarationMatrix()
    {
        foreach (var (pn, tpl) in DeclPositions)
            foreach (var (dn, decl) in DeclProbes)
                yield return new TortureCase($"decl/{pn}/{dn}", tpl.Replace("%D%", decl), Expect.Any);
    }

    #endregion

    #region Type matrix
    /// <summary>
    /// Every position that takes a type specifier. "%T%" is the hole. Types reach the
    /// checker through several different paths (ResolveType, CheckType, the parser's
    /// SkipTypeSpec lookahead), and a type that is nonsense in one has to be caught in
    /// all of them.
    /// </summary>
    private static readonly (string Name, string Template)[] TypePositions =
    [
        ("let",          "kernel { entry func Main() { let %T% v = default(%T%); } }"),
        ("param",        "void func H(%T% p) { } kernel { entry func Main() { } }"),
        ("ref-param",    "void func H(ref %T% p) { } kernel { entry func Main() { } }"),
        ("return",       "%T% func H() { return default(%T%); } kernel { entry func Main() { } }"),
        ("field",        "class C { %T% n; } kernel { entry func Main() { } }"),
        ("cast",         "kernel { entry func Main() { let v = 1 as %T%; } }"),
        ("sizeof",       "kernel { entry func Main() { let v = sizeof(%T%); } }"),
        ("default",      "kernel { entry func Main() { let v = default(%T%); } }"),
        ("new",          "kernel { entry func Main() { let v = new %T%(); } }"),
        ("generic-arg",  "class Box[T] { T v; } kernel { entry func Main() { let Box[%T%] b = new Box[%T%](); } }"),
        ("array-elem",   "kernel { entry func Main() { let [2]%T% a = new [2]%T%; } }"),
        ("ptr",          "kernel { entry func Main() { unsafe { let %T%* p = null; } } }"),
        ("funcptr-ret",  "kernel { entry func Main() { let func(int) -> %T% f = null; } }"),
        ("funcptr-arg",  "kernel { entry func Main() { let func(%T%) -> int f = null; } }"),
        ("union-field",  "union U { A(%T% x) } kernel { entry func Main() { } }"),
    ];

    /// <summary>Type specifiers ranging from ordinary to structurally invalid.</summary>
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
    /// Crosses every type probe with every type position. A generic Box is prepended so
    /// the generic probes name a real template rather than failing as an unknown type.
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
    /// Every binary operator crossed with every pair drawn from a set of operand
    /// expressions covering each type family. Most combinations are type errors; the
    /// invariant is that each one is either rejected or emits valid C, never silently
    /// lowered to a C operator that means something else.
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
                        prelude + "kernel { entry func Main() { " + body + " } }", Expect.Any);
                }
    }

    #endregion

    #region Assignment matrix
    /// <summary>
    /// Every assignment operator crossed with every kind of assignment target. Plain '=' and
    /// the compound forms take different resolver paths (the compound ones hoist, re-read the
    /// target, and can dispatch to an operator overload), and indexed and field targets take
    /// different paths again -- so a target that is illegal for one form is easy to leave
    /// legal for another.
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
                    prelude + "kernel { entry func Main() { " + body + " } }", Expect.Any);
            }
    }

    #endregion

    #region Member access matrix
    /// <summary>
    /// Field reads, method calls, and static-vs-instance access crossed with every kind of
    /// receiver. Member resolution has one path per receiver shape (enum name, module name,
    /// class name, instance, primitive), and the emitter prints '->' for all of them, so a
    /// receiver nobody checked produces C that dereferences a non-pointer.
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
                    prelude + "kernel { entry func Main() { " + body + " } }", Expect.Any);
            }
    }

    #endregion

    #region Identifier matrix
    /// <summary>
    /// Awkward identifiers placed in every kind of declaration.
    ///
    /// Locals and parameters are the only names the emitter prints as written, so they are
    /// the only ones that can collide with C's own vocabulary or with the temporaries the
    /// compiler generates. Classes, functions, fields and enum members all carry a gata_
    /// prefix or a dense token and are safe by construction -- but the matrix covers them
    /// anyway, because "safe by construction" is exactly the kind of claim that stops being
    /// true when someone changes the mangler.
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
            ("local",  "void func Sink(int v) { } kernel { entry func Main() { let int NAME = 1; Sink(NAME); } }"),
            ("param",  "void func H(int NAME) { } kernel { entry func Main() { H(1); } }"),
            ("func",   "void func NAME() { } kernel { entry func Main() { NAME(); } }"),
            ("class",  "class NAME { public int n; } kernel { entry func Main() { let NAME c = new NAME(); } }"),
            ("field",  "class C { public int NAME; } kernel { entry func Main() { let C c = new C(); c.NAME = 1; } }"),
            ("enum",   "enum E { NAME } kernel { entry func Main() { let E e = E.NAME; } }"),
            ("forin",  "kernel { entry func Main() { for NAME in [1, 2] { } } }"),
            ("bind",   "union U { A(int x) } kernel { entry func Main() { let U u = U.A(1); match (u) { case A(NAME) { } } } }"),
        ];

        foreach (var n in names)
            foreach (var (sn, tpl) in shapes)
                yield return new TortureCase($"ident/{sn}/{n}", tpl.Replace("NAME", n), Expect.Any);
    }

    #endregion

    #region Curated cases
    /// <summary>
    /// Hand-written cases, each pinning exactly one rule. Unlike the matrices these
    /// carry a real expectation, and where the diagnostic identity matters, the code.
    /// </summary>
    private static IEnumerable<TortureCase> Curated()
    {
        #region 'assign' outside a catch handler
        yield return new("assign/bare-entry",
            "kernel { entry func Main() { assign 1; } }", Expect.Rejected, Codes.AssignOutsideCatch);
        yield return new("assign/in-try-block",
            "throws void func T() { throw; } kernel { entry func Main() { try { T(); assign 1; } catch { } } }",
            Expect.Rejected, Codes.AssignOutsideCatch);
        yield return new("assign/in-catch-block",
            "throws void func T() { throw; } kernel { entry func Main() { try { T(); } catch { assign 1; } } }",
            Expect.Rejected, Codes.AssignOutsideCatch);
        yield return new("assign/in-loop-in-handler",
            "throws int func T() { throw; } kernel { entry func Main() { let int v = T() catch { while (true) { assign 0; } }; } }",
            Expect.Any);
        yield return new("assign/nested-func-in-handler",
            "throws int func T() { throw; } void func H() { assign 1; } kernel { entry func Main() { H(); } }",
            Expect.Rejected, Codes.AssignOutsideCatch);

        #endregion

        #region catch handlers
        yield return new("catch/no-assign",
            "throws int func T() { throw; } kernel { entry func Main() { let int v = T() catch { }; } }",
            Expect.Rejected, Codes.CatchHandlerNoAssign);
        yield return new("catch/partial-assign",
            "throws int func T() { throw; } kernel { entry func Main() { let int v = T() catch { if (true) { assign 0; } }; } }",
            Expect.Rejected, Codes.CatchHandlerNoAssign);
        yield return new("catch/both-branches-assign",
            "throws int func T() { throw; } kernel { entry func Main() { let int v = T() catch { if (true) { assign 0; } else { assign 1; } }; } }",
            Expect.Accepted);
        yield return new("catch/assign-wrong-type",
            "throws int func T() { throw; } kernel { entry func Main() { let int v = T() catch { assign \"s\"; }; } }",
            Expect.Rejected);
        yield return new("catch/on-non-throws-call",
            "int func H() { return 1; } kernel { entry func Main() { let int v = H() catch { assign 0; }; } }",
            Expect.Rejected);
        yield return new("catch/on-non-call",
            "kernel { entry func Main() { let int v = 1 catch { assign 0; }; } }", Expect.Rejected);
        yield return new("catch/nested-in-handler",
            "throws int func T() { throw; } kernel { entry func Main() { let int v = T() catch { let int w = T() catch { assign 1; }; assign w; }; } }",
            Expect.Any);
        yield return new("catch/subexpression",
            "throws int func T() { throw; } void func H(int n) { } kernel { entry func Main() { H(T() catch { assign 0; }); } }",
            Expect.Any);
        yield return new("catch/on-void-throws",
            "throws void func T() { throw; } kernel { entry func Main() { T() catch { assign 1; }; } }", Expect.Any);
        yield return new("catch/handler-falls-through-to-break",
            "throws int func T() { throw; } kernel { entry func Main() { while (true) { let int v = T() catch { break; }; } } }",
            Expect.Any);

        #endregion

        #region try/catch
        yield return new("try/catch-missing",
            "throws void func T() { throw; } kernel { entry func Main() { try { T(); } } }", Expect.Rejected);
        yield return new("try/empty-both",
            "kernel { entry func Main() { try { } catch { } } }", Expect.Any);
        yield return new("try/return-in-catch",
            "throws void func T() { throw; } int func H() { try { T(); return 1; } catch { return 0; } } kernel { entry func Main() { H(); } }",
            Expect.Accepted);
        yield return new("try/throw-in-catch-non-throws",
            "throws void func T() { throw; } void func H() { try { T(); } catch { throw; } } kernel { entry func Main() { H(); } }",
            Expect.Rejected);
        yield return new("throws/unhandled",
            "throws void func T() { throw; } kernel { entry func Main() { T(); } }", Expect.Rejected, Codes.ThrowsOutsideTry);
        yield return new("throws/void-return-type",
            "throws void func T() { throw; } kernel { entry func Main() { try { T(); } catch { } } }", Expect.Accepted);
        yield return new("throws/on-entry",
            "kernel { entry throws func Main() { throw; } }", Expect.Rejected);
        yield return new("throw/outside-throws-func",
            "void func H() { throw; } kernel { entry func Main() { H(); } }", Expect.Rejected);

        #endregion

        #region defer
        yield return new("defer/return", "void func H() { defer return; } kernel { entry func Main() { H(); } }",
            Expect.Rejected, Codes.DeferTransfer);
        yield return new("defer/break", "kernel { entry func Main() { while (true) { defer break; } } }",
            Expect.Rejected, Codes.DeferTransfer);
        yield return new("defer/continue", "kernel { entry func Main() { while (true) { defer continue; } } }",
            Expect.Rejected, Codes.DeferTransfer);
        yield return new("defer/defer", "kernel { entry func Main() { defer defer VoidH(); } } void func VoidH() { }",
            Expect.Rejected);
        yield return new("defer/let", "kernel { entry func Main() { defer let int x = 1; } }", Expect.Any);
        yield return new("defer/throw-in-throws",
            "throws void func T() { defer throw; } kernel { entry func Main() { try { T(); } catch { } } }", Expect.Any);
        yield return new("defer/nested-defer-in-block",
            "void func H() { } kernel { entry func Main() { defer { defer H(); } } }", Expect.Any);

        #endregion

        #region switch
        yield return new("switch/dup-label",
            "kernel { entry func Main() { switch (1) { case 1 { } case 1 { } } } }", Expect.Rejected);
        yield return new("switch/no-cases",
            "kernel { entry func Main() { switch (1) { } } }", Expect.Any);
        yield return new("switch/only-default",
            "kernel { entry func Main() { switch (1) { default { } } } }", Expect.Any);
        // Gata's switch desugars to an if/else-if equality chain, not a C switch, so a
        // case label is an ordinary expression and need not be a compile-time constant.
        yield return new("switch/non-constant-label",
            "kernel { entry func Main() { let int n = 1; switch (n) { case n { } } } }", Expect.Any);
        yield return new("switch/string-scrutinee",
            "kernel { entry func Main() { switch (\"a\") { case \"a\" { } } } }", Expect.Any);
        yield return new("switch/bool-scrutinee",
            "kernel { entry func Main() { switch (true) { case true { } } } }", Expect.Any);
        yield return new("switch/break-in-case",
            "kernel { entry func Main() { switch (1) { case 1 { break; } } } }", Expect.Any);

        #endregion

        #region match
        yield return new("match/non-union",
            "kernel { entry func Main() { match (1) { case A { } } } }", Expect.Rejected);
        yield return new("match/unknown-variant",
            "union U { A } kernel { entry func Main() { let U u = U.A(); match (u) { case Zzz { } } } }", Expect.Rejected);
        yield return new("match/non-exhaustive",
            "union U { A, B } kernel { entry func Main() { let U u = U.A(); match (u) { case A { } } } }",
            Expect.Rejected, Codes.NonExhaustiveMatch);
        yield return new("match/dup-variant",
            "union U { A, B } kernel { entry func Main() { let U u = U.A(); match (u) { case A { } case A { } case B { } } } }",
            Expect.Rejected);
        yield return new("match/too-many-binds",
            "union U { A(int n), B } kernel { entry func Main() { let U u = U.A(1); match (u) { case A(x, y) { } case B { } } } }",
            Expect.Rejected);
        yield return new("match/binds-on-payloadless",
            "union U { A, B } kernel { entry func Main() { let U u = U.A(); match (u) { case A(x) { } case B { } } } }",
            Expect.Rejected);
        yield return new("match/no-cases",
            "union U { A } kernel { entry func Main() { let U u = U.A(); match (u) { } } }", Expect.Any);

        #endregion

        #region enum / union declarations
        yield return new("enum/empty", "enum E { } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("enum/dup-member", "enum E { A, A } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("enum/non-constant-value",
            "int func H() { return 1; } enum E { A = H() } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("enum/string-value", "enum E { A = \"s\" } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("union/empty", "union U { } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("union/dup-variant", "union U { A, A } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("union/managed-payload",
            "union U { A(String s) } kernel { entry func Main() { } }", Expect.Any);
        yield return new("union/self-payload", "union U { A(U u) } kernel { entry func Main() { } }", Expect.Any);
        yield return new("union/dup-field", "union U { A(int x, int x) } kernel { entry func Main() { } }", Expect.Rejected);

        #endregion

        #region classes
        yield return new("class/dup-field", "class C { int n; int n; } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("class/dup-method",
            "class C { public void func M() { } public void func M() { } } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("class/self-field", "class C { C c; } kernel { entry func Main() { } }", Expect.Any);
        yield return new("class/private-access",
            "class C { int n; } kernel { entry func Main() { let C c = new C(); let int v = c.n; } }",
            Expect.Rejected, Codes.PrivateMember);
        yield return new("class/static-on-instance",
            "class C { public static void func S() { } } kernel { entry func Main() { let C c = new C(); c.S(); } }",
            Expect.Rejected);
        yield return new("class/instance-on-static",
            "class C { public void func M() { } } kernel { entry func Main() { C.M(); } }", Expect.Rejected);
        yield return new("class/self-in-static",
            "class C { int n; public static void func S() { self.n = 1; } } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("class/empty", "class C { } kernel { entry func Main() { let C c = new C(); } }", Expect.Any);
        yield return new("module/instance-field",
            "module M { int n; } kernel { entry func Main() { } }", Expect.Rejected, Codes.ModuleField);
        yield return new("module/new",
            "module M { public static void func F() { } } kernel { entry func Main() { let M m = new M(); } }", Expect.Rejected);

        #endregion

        #region operators
        yield return new("operator/wrong-arity",
            "class C { public operator C func +(C a, C b) { return a; } } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("operator/no-return",
            "class C { public operator C func +(C o) { } } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("operator/dup",
            "class C { public operator C func +(C o) { return o; } public operator C func +(C o) { return o; } } kernel { entry func Main() { } }",
            Expect.Rejected);
        yield return new("operator/index-get-no-set",
            "class C { public operator int func [](int k) { return k; } } kernel { entry func Main() { let C c = new C(); c[0] = 1; } }",
            Expect.Rejected, Codes.NoIndexSetter);
        yield return new("operator/mod-not-overloadable",
            "class C { public operator C func %(C o) { return o; } } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("operator/on-module",
            "module M { public operator int func +(int a) { return a; } } kernel { entry func Main() { } }", Expect.Any);

        #endregion

        #region generics
        yield return new("generic/unknown-param",
            "class Box[T] { T v; public void func Set(U x) { } } kernel { entry func Main() { let Box[int] b = new Box[int](); } }",
            Expect.Rejected);
        yield return new("generic/arity-mismatch",
            "class Box[T] { T v; } kernel { entry func Main() { let Box[int, int] b = new Box[int, int](); } }", Expect.Rejected);
        yield return new("generic/uninstantiated",
            "class Box[T] { T v; } kernel { entry func Main() { let Box b = new Box(); } }", Expect.Rejected);
        yield return new("generic/func-uninferable",
            "T func Id[T](int n) { return default(T); } kernel { entry func Main() { Id(1); } }", Expect.Any);
        yield return new("generic/recursive-instantiation",
            "class Box[T] { T v; } kernel { entry func Main() { let Box[Box[int]] b = new Box[Box[int]](); } }", Expect.Any);

        #endregion

        #region control flow
        yield return new("cf/break-outside-loop",
            "kernel { entry func Main() { break; } }", Expect.Rejected, Codes.BreakOutsideLoop);
        yield return new("cf/continue-outside-loop",
            "kernel { entry func Main() { continue; } }", Expect.Rejected, Codes.BreakOutsideLoop);
        yield return new("cf/missing-return",
            "int func H() { } kernel { entry func Main() { H(); } }", Expect.Rejected, Codes.MissingReturn);
        yield return new("cf/return-value-from-void",
            "void func H() { return 1; } kernel { entry func Main() { H(); } }", Expect.Rejected);
        yield return new("cf/return-nothing-from-int",
            "int func H() { return; } kernel { entry func Main() { H(); } }", Expect.Rejected);
        yield return new("cf/unreachable",
            "int func H() { return 1; VoidH(); } void func VoidH() { } kernel { entry func Main() { H(); } }", Expect.Any);
        yield return new("cf/cond-not-bool",
            "kernel { entry func Main() { if (1) { } } }", Expect.Rejected, Codes.ConditionNotBool);
        yield return new("cf/infinite-for-missing-cond",
            "kernel { entry func Main() { for (;;) { break; } } }", Expect.Any);
        yield return new("cf/entry-call",
            "kernel { entry func Main() { Main(); } }", Expect.Rejected, Codes.CallToEntry);

        #endregion

        #region realms / structure
        yield return new("struct/no-entry", "void func H() { }", Expect.Rejected);
        yield return new("struct/two-entries",
            "kernel { entry func A() { } entry func B() { } }", Expect.Rejected);
        yield return new("struct/nested-kernel", "kernel { kernel { } }", Expect.Rejected);
        yield return new("struct/dup-kernel",
            "kernel { entry func Main() { } } kernel { }", Expect.Rejected);
        yield return new("struct/entry-outside-kernel",
            "entry func Main() { }", Expect.Rejected);
        yield return new("struct/panic-in-user",
            "kernel { entry func Main() { } } user { void func H() { panic \"x\"; } }", Expect.Any);

        #endregion

        #region process / thread
        yield return new("proc/no-mode", "kernel { process P { thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.MissingProcessMode);
        yield return new("proc/mode-twice",
            "kernel { foreground process P : background { thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected);
        yield return new("proc/thread-mode",
            "kernel { foreground process P { background thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected, Codes.ThreadModeNotAllowed);
        yield return new("proc/empty", "kernel { foreground process P { } entry func Main() { } }", Expect.Any);
        yield return new("proc/thread-entry-params",
            "kernel { foreground process P { thread T { entry func R(int n) { } } } entry func Main() { } }", Expect.Rejected);
        yield return new("proc/dup-thread",
            "kernel { foreground process P { thread T { entry func R() { } } thread T { entry func R() { } } } entry func Main() { } }",
            Expect.Rejected);

        #endregion

        #region unsafe / pointers
        yield return new("unsafe/deref-outside",
            "kernel { entry func Main() { let int n = 1; let int* p = &n; let int v = *p; } }",
            Expect.Rejected, Codes.UnsafeRequired);
        yield return new("unsafe/addrof-outside",
            "kernel { entry func Main() { let int n = 1; let int* p = &n; } }", Expect.Any);
        yield return new("unsafe/nested",
            "kernel { entry func Main() { unsafe { unsafe { } } } }", Expect.Any);
        yield return new("unsafe/deref-int",
            "kernel { entry func Main() { unsafe { let int n = 1; let int v = *n; } } }", Expect.Rejected);
        yield return new("unsafe/addrof-literal",
            "kernel { entry func Main() { unsafe { let int* p = &1; } } }", Expect.Rejected);

        #endregion

        #region casts
        yield return new("cast/int-to-bool", "kernel { entry func Main() { let bool b = 1 as bool; } }", Expect.Any);
        yield return new("cast/to-void", "kernel { entry func Main() { let v = 1 as void; } }", Expect.Rejected);
        yield return new("cast/class-to-int",
            "class C { } kernel { entry func Main() { let C c = new C(); let int v = c as int; } }", Expect.Rejected);
        yield return new("cast/prim-paren-deref",
            "kernel { entry func Main() { unsafe { let int n = 1; let int* p = &n; let int v = (int)*p; } } }", Expect.Any);
        yield return new("cast/redundant", "kernel { entry func Main() { let int v = 1 as int; } }", Expect.Any);

        #endregion

        #region literals / lexer edges
        yield return new("lit/int-overflow", "kernel { entry func Main() { let int v = 99999999999999999999; } }", Expect.Rejected);
        yield return new("lit/hex", "kernel { entry func Main() { let int v = 0xFF; } }", Expect.Accepted);
        yield return new("lit/bad-hex", "kernel { entry func Main() { let int v = 0xZZ; } }", Expect.Rejected);
        yield return new("lit/empty-char", "kernel { entry func Main() { let char c = ''; } }", Expect.Rejected);
        yield return new("lit/multi-char", "kernel { entry func Main() { let char c = 'ab'; } }", Expect.Rejected);
        yield return new("lit/bad-escape", "kernel { entry func Main() { let s = \"\\q\"; } }", Expect.Rejected);
        yield return new("lit/unterminated-str", "kernel { entry func Main() { let s = \"abc; } }", Expect.Rejected);
        yield return new("lit/interp-no-holes", "kernel { entry func Main() { let s = $\"plain\"; } }", Expect.Any);
        yield return new("lit/interp-empty-hole", "kernel { entry func Main() { let s = $\"{}\"; } }", Expect.Rejected);
        yield return new("lit/interp-unclosed-hole", "kernel { entry func Main() { let s = $\"{1\"; } }", Expect.Rejected);

        #endregion

        #region trailing commas (each list form)
        yield return new("comma/enum", "enum E { A, } kernel { entry func Main() { } }", Expect.Rejected, Codes.TrailingComma);
        yield return new("comma/union", "union U { A, } kernel { entry func Main() { } }", Expect.Rejected, Codes.TrailingComma);
        yield return new("comma/union-fields", "union U { A(int x,) } kernel { entry func Main() { } }", Expect.Rejected, Codes.TrailingComma);
        yield return new("comma/params", "void func H(int a,) { } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("comma/args", "void func H(int a) { } kernel { entry func Main() { H(1,); } }", Expect.Rejected);
        yield return new("comma/array-lit", "kernel { entry func Main() { let a = [1,]; } }", Expect.Rejected);
        yield return new("comma/coll-init", "kernel { entry func Main() { let a = new [2]int { 1, }; } }", Expect.Rejected);
        yield return new("comma/generic-params", "class Box[T,] { T v; } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("comma/match-binds",
            "union U { A(int x) } kernel { entry func Main() { let U u = U.A(1); match (u) { case A(x,) { } } } }", Expect.Rejected);
        yield return new("comma/switch-labels", "kernel { entry func Main() { switch (1) { case 1, { } } } }", Expect.Rejected);

        #endregion

        #region declaration headers
        yield return new("decl/dup-modifier", "kernel { entry func Main() { } } public public void func H() { }", Expect.Rejected);
        yield return new("decl/public-private", "class C { public private void func M() { } } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("decl/static-free-func", "static void func H() { } kernel { entry func Main() { } }",
            Expect.Rejected, Codes.StaticOnFreeFunc);
        yield return new("decl/return-type-after-params", "kernel { entry func Main() { } } func H() -> int { }", Expect.Rejected);
        yield return new("decl/dup-param", "void func H(int a, int a) { } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("decl/dup-free-func", "void func H() { } void func H() { } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("decl/generic-param-generic", "class Box[T[U]] { } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("decl/func-ptr-ptr", "kernel { entry func Main() { let func(int) -> int* f = null; } }", Expect.Any);
        yield return new("decl/nested-class", "class A { class B { } } kernel { entry func Main() { } }", Expect.Rejected);

        #endregion

        #region annotations
        yield return new("ann/on-enum", "@keep enum E { A } kernel { entry func Main() { } }", Expect.Rejected, Codes.BadAnnotation);
        yield return new("ann/on-field", "class C { @keep int n; } kernel { entry func Main() { } }", Expect.Rejected);
        yield return new("ann/unknown-intrinsic",
            "@intrinsic(not_a_real_role) native { } kernel { entry func Main() { } }", Expect.Any);
        yield return new("ann/env-misplaced",
            "kernel { entry func Main() { } @environment }", Expect.Rejected);

        #endregion

        #region warnings that must not be errors
        yield return new("warn/unused-var", "kernel { entry func Main() { let int unused = 1; } }", Expect.Accepted);
        yield return new("warn/self-assign", "kernel { entry func Main() { let int v = 1; v = v; } }", Expect.Accepted);
        yield return new("warn/const-cond", "kernel { entry func Main() { if (true) { VoidH(); } } void func VoidH() { } }", Expect.Any);

        #endregion

        #region deep but bounded nesting
        yield return new("depth/parens", "kernel { entry func Main() { let int v = " +
            new string('(', 300) + "1" + new string(')', 300) + "; } }", Expect.Rejected);
        yield return new("depth/blocks", "kernel { entry func Main() { " +
            string.Concat(Enumerable.Repeat("{", 300)) + string.Concat(Enumerable.Repeat("}", 300)) + " } }", Expect.Rejected);
        yield return new("depth/unary", "kernel { entry func Main() { let int v = " +
            new string('-', 300) + "1; } }", Expect.Rejected);
        yield return new("depth/generic-type",
            "kernel { entry func Main() { let " + string.Concat(Enumerable.Repeat("Box[", 300)) + "int" +
            new string(']', 300) + " b = null; } }", Expect.Rejected);

        #endregion

        #region truncation / unbalanced input
        yield return new("trunc/open-brace", "kernel { entry func Main() {", Expect.Rejected);
        yield return new("trunc/open-paren", "kernel { entry func Main() { VoidH(", Expect.Rejected);
        yield return new("trunc/empty", "", Expect.Rejected);
        yield return new("trunc/only-brace", "}", Expect.Rejected);
        yield return new("trunc/stray-semi", ";", Expect.Rejected);
        yield return new("trunc/keyword-only", "class", Expect.Rejected);
        yield return new("trunc/dangling-else", "kernel { entry func Main() { else { } } }", Expect.Rejected);
        yield return new("trunc/dangling-catch", "kernel { entry func Main() { catch { } } }", Expect.Rejected);
        yield return new("trunc/dangling-case", "kernel { entry func Main() { case 1 { } } }", Expect.Rejected);
    }

    /// <summary>
    /// A second curated batch, covering the areas the first pass did not reach: 'ref'
    /// parameters, the for..in protocol, interpolation, scoping, native blocks, and the
    /// interactions between throws, generics, and catch handlers.
    /// </summary>
    private static IEnumerable<TortureCase> Curated2()
    {

        #endregion

        #region ref parameters
        yield return new("ref/basic",
            "void func Bump(ref int n) { n = n + 1; } kernel { entry func Main() { let int v = 1; Bump(ref v); } }",
            Expect.Accepted);
        yield return new("ref/missing-keyword",
            "void func Bump(ref int n) { } kernel { entry func Main() { let int v = 1; Bump(v); } }",
            Expect.Rejected, Codes.RefArgMismatch);
        yield return new("ref/unexpected-keyword",
            "void func Take(int n) { } kernel { entry func Main() { let int v = 1; Take(ref v); } }",
            Expect.Rejected, Codes.RefArgMismatch);
        yield return new("ref/literal-arg",
            "void func Bump(ref int n) { } kernel { entry func Main() { Bump(ref 1); } }", Expect.Rejected);
        yield return new("ref/call-arg",
            "int func Two() { return 2; } void func Bump(ref int n) { } kernel { entry func Main() { Bump(ref Two()); } }",
            Expect.Rejected);
        // Not Accepted: any class at all makes ValidateIntrinsics demand the ARC role set,
        // which a corpus case importing no libgata cannot bind.
        yield return new("ref/field-arg",
            "class C { public int n; func _init() { self.n = 0; } } void func Bump(ref int n) { } kernel { entry func Main() { let C c = new C(); Bump(ref c.n); } }",
            Expect.Any);
        yield return new("ref/elem-arg",
            "void func Bump(ref int n) { } kernel { entry func Main() { let a = [1, 2]; Bump(ref a[0]); } }",
            Expect.Accepted);
        yield return new("ref/type-mismatch",
            "void func Bump(ref int n) { } kernel { entry func Main() { let bool b = true; Bump(ref b); } }",
            Expect.Rejected);
        yield return new("ref/on-class",
            "class C { } void func Take(ref C c) { } kernel { entry func Main() { let C c = new C(); Take(ref c); } }",
            Expect.Any);
        yield return new("ref/in-operator",
            "class C { public operator C func +(ref C o) { return o; } } kernel { entry func Main() { } }", Expect.Any);

        #endregion

        #region for..in protocol
        yield return new("forin/array", "kernel { entry func Main() { for x in [1, 2] { } } }", Expect.Any);
        yield return new("forin/int", "kernel { entry func Main() { for x in 1 { } } }",
            Expect.Rejected, Codes.NotIterable);
        yield return new("forin/no-protocol",
            "class C { } kernel { entry func Main() { let C c = new C(); for x in c { } } }",
            Expect.Rejected, Codes.NotIterable);
        yield return new("forin/length-only",
            "class C { public int func Length() { return 0; } } kernel { entry func Main() { let C c = new C(); for x in c { } } }",
            Expect.Rejected);
        yield return new("forin/wrong-get-arity",
            "class C { public int func Length() { return 0; } public int func Get() { return 0; } } kernel { entry func Main() { let C c = new C(); for x in c { } } }",
            Expect.Rejected);
        yield return new("forin/full-protocol",
            "class C { public int func Length() { return 0; } public int func Get(int i) { return i; } } kernel { entry func Main() { for x in new C() { } } }",
            Expect.Any);
        yield return new("forin/assign-to-binding",
            "kernel { entry func Main() { for x in [1, 2] { x = 5; } } }", Expect.Any);
        yield return new("forin/shadow-outer",
            "kernel { entry func Main() { let int x = 1; for x in [1, 2] { } } }", Expect.Any);
        yield return new("forin/nested-same-name",
            "kernel { entry func Main() { for x in [1, 2] { for x in [3, 4] { } } } }", Expect.Any);

        #endregion

        #region interpolation
        yield return new("interp/class-operand",
            "class C { } kernel { entry func Main() { let C c = new C(); let s = $\"{c}\"; } }", Expect.Any);
        yield return new("interp/void-operand",
            "void func H() { } kernel { entry func Main() { let s = $\"{H()}\"; } }", Expect.Rejected);
        yield return new("interp/array-operand",
            "kernel { entry func Main() { let a = [1, 2]; let s = $\"{a}\"; } }", Expect.Any);
        yield return new("interp/nested-interp",
            "kernel { entry func Main() { let s = $\"{$\"{1}\"}\"; } }", Expect.Any);
        yield return new("interp/assign-inside",
            "kernel { entry func Main() { let int v = 0; let s = $\"{v = 1}\"; } }", Expect.Rejected);
        yield return new("interp/throws-inside",
            "throws int func T() { throw; } kernel { entry func Main() { let s = $\"{T()}\"; } }", Expect.Rejected);
        yield return new("interp/many-parts",
            "kernel { entry func Main() { let s = $\"a{1}b{2}c{3}d{4}e\"; } }", Expect.Any);
        yield return new("interp/plain-string-no-dollar",
            "kernel { entry func Main() { let s = \"{1}\"; } }", Expect.Any);

        #endregion

        #region scoping and shadowing
        yield return new("scope/redeclare-same-block",
            "kernel { entry func Main() { let int v = 1; let int v = 2; } }", Expect.Any);
        yield return new("scope/shadow-param",
            "void func H(int p) { let int p = 1; } kernel { entry func Main() { H(1); } }", Expect.Any);
        yield return new("scope/shadow-self",
            "class C { public void func M() { let int self = 1; } } kernel { entry func Main() { } }", Expect.Any);
        yield return new("scope/use-before-decl",
            "kernel { entry func Main() { let int a = b; let int b = 1; } }", Expect.Rejected);
        yield return new("scope/self-referential-init",
            "kernel { entry func Main() { let int a = a; } }", Expect.Rejected);
        yield return new("scope/leak-from-if",
            "kernel { entry func Main() { if (true) { let int a = 1; } let int b = a; } }", Expect.Rejected);
        yield return new("scope/leak-from-for",
            "kernel { entry func Main() { for (let int i = 0; i < 1; i++) { } let int j = i; } }", Expect.Rejected);
        yield return new("scope/name-shadows-class",
            "class C { } kernel { entry func Main() { let int C = 1; } }", Expect.Any);
        yield return new("scope/name-shadows-func",
            "void func H() { } kernel { entry func Main() { let int H = 1; } }", Expect.Any);

        #endregion

        #region throws x generics x catch
        yield return new("throws/generic-func",
            "throws T func Pick[T](T v) { return v; } kernel { entry func Main() { let int x = Pick(1) catch { assign 0; }; } }",
            Expect.Any);
        yield return new("throws/generic-class-method",
            "class Box[T] { public throws T func Get() { throw; } } kernel { entry func Main() { let Box[int] b = new Box[int](); let int v = b.Get() catch { assign 0; }; } }",
            Expect.Any);
        yield return new("throws/returns-class",
            "class C { } throws C func Make() { throw; } kernel { entry func Main() { let C c = Make() catch { assign new C(); }; } }",
            Expect.Any);
        yield return new("throws/in-operator",
            "class C { public throws operator C func +(C o) { return o; } } kernel { entry func Main() { } }",
            Expect.Rejected);
        // A constructor is a method named '_init'; 'func C()' on class C is just a method.
        yield return new("throws/in-ctor",
            "class C { throws func _init() { throw; } } kernel { entry func Main() { } }",
            Expect.Rejected, Codes.LifecycleThrows);
        yield return new("throws/in-dtor",
            "class C { throws func _deinit() { throw; } } kernel { entry func Main() { } }",
            Expect.Rejected, Codes.LifecycleThrows);
        yield return new("ctor/named-like-class",
            "class C { public void func C() { } } kernel { entry func Main() { } }", Expect.Any);
        yield return new("throws/nested-call-arg",
            "throws int func T() { throw; } void func H(int n) { } kernel { entry func Main() { try { H(T()); } catch { } } }",
            Expect.Rejected);
        yield return new("throws/in-condition",
            "throws bool func T() { throw; } kernel { entry func Main() { try { if (T()) { } } catch { } } }",
            Expect.Rejected);
        yield return new("throws/in-return",
            "throws int func T() { throw; } throws int func H() { return T(); } kernel { entry func Main() { try { H(); } catch { } } }",
            Expect.Rejected);
        yield return new("throws/double-call-one-stmt",
            "throws int func T() { throw; } kernel { entry func Main() { try { let int v = T() + T(); } catch { } } }",
            Expect.Rejected);
        yield return new("throws/catch-in-catch-block",
            "throws int func T() { throw; } kernel { entry func Main() { try { T(); } catch { let int v = T() catch { assign 0; }; } } }",
            Expect.Any);

        #endregion

        #region operator overload interactions
        yield return new("op/compound-uses-binary",
            "class C { public operator C func +(C o) { return o; } } kernel { entry func Main() { let C a = new C(); a += a; } }",
            Expect.Any);
        yield return new("op/compound-no-binary",
            "class C { } kernel { entry func Main() { let C a = new C(); a += a; } }", Expect.Rejected);
        yield return new("op/indexer-compound",
            "class C { public operator int func [](int k) { return k; } public operator func []=(int k, int v) { } } kernel { entry func Main() { let C c = new C(); c[0] += 1; } }",
            Expect.Any);
        yield return new("op/setter-without-getter-compound",
            "class C { public operator func []=(int k, int v) { } } kernel { entry func Main() { let C c = new C(); c[0] += 1; } }",
            Expect.Rejected);
        yield return new("op/unary-not",
            "class C { public operator bool func !() { return true; } } kernel { entry func Main() { let C c = new C(); let bool b = !c; } }",
            Expect.Any);
        yield return new("op/as-conversion",
            "class C { public operator C func as(int n) { return new C(); } } kernel { entry func Main() { let C c = 1 as C; } }",
            Expect.Any);
        yield return new("op/as-wrong-direction",
            "class C { public operator int func as(C c) { return 1; } } kernel { entry func Main() { } }", Expect.Any);
        yield return new("op/private-access",
            "class C { private operator C func +(C o) { return o; } } kernel { entry func Main() { let C a = new C(); let C b = a + a; } }",
            Expect.Rejected, Codes.PrivateMember);
        yield return new("op/eq-returns-int",
            "class C { public operator int func ==(C o) { return 1; } } kernel { entry func Main() { } }", Expect.Any);

        #endregion

        #region generics stress
        yield return new("gen/method-on-generic-class",
            "class Box[T] { T v; public U func Map[U](U seed) { return seed; } } kernel { entry func Main() { let Box[int] b = new Box[int](); let int r = b.Map(1); } }",
            Expect.Any);
        yield return new("gen/same-param-name-nested",
            "class Box[T] { public T func Id[T](T v) { return v; } } kernel { entry func Main() { let Box[int] b = new Box[int](); } }",
            Expect.Any);
        yield return new("gen/generic-throws-in-handler",
            "throws T func Pick[T](T v) { return v; } kernel { entry func Main() { let int a = Pick(1) catch { let int b = Pick(2) catch { assign 0; }; assign b; }; } }",
            Expect.Any);
        yield return new("gen/instantiate-with-void",
            "class Box[T] { T v; } kernel { entry func Main() { let Box[void] b = new Box[void](); } }", Expect.Rejected);
        yield return new("gen/instantiate-with-self",
            "class Box[T] { T v; } kernel { entry func Main() { let Box[Box[Box[int]]] b = new Box[Box[Box[int]]](); } }",
            Expect.Any);
        yield return new("gen/param-shadows-class",
            "class Thing { } class Box[Thing] { Thing v; } kernel { entry func Main() { let Box[int] b = new Box[int](); } }",
            Expect.Any);
        yield return new("gen/unused-param",
            "class Box[T] { int n; } kernel { entry func Main() { let Box[int] b = new Box[int](); } }", Expect.Any);

        #endregion

        #region native blocks and fields
        yield return new("native/top-level", "native { int x; } kernel { entry func Main() { } }", Expect.Any);
        yield return new("native/method-body",
            "class C { public int func M() native { return 1; } } kernel { entry func Main() { } }", Expect.Any);
        yield return new("native/fields-block",
            "class C { fields { int raw; } } kernel { entry func Main() { let C c = new C(); } }", Expect.Any);
        yield return new("native/statement",
            "kernel { entry func Main() { native { int local = 1; } } }", Expect.Any);
        yield return new("native/unbalanced-brace",
            "kernel { entry func Main() { } } native { if (1) { }", Expect.Rejected);
        yield return new("native/empty", "native { } kernel { entry func Main() { } }", Expect.Any);

        #endregion

        #region compound and unary numeric edges
        yield return new("num/bitwise-on-double",
            "kernel { entry func Main() { let double d = 1.5; let v = d & 1; } }", Expect.Rejected);
        yield return new("num/mod-on-double",
            "kernel { entry func Main() { let double d = 1.5; let v = d % 2.0; } }", Expect.Any);
        yield return new("num/shift-negative",
            "kernel { entry func Main() { let v = 1 << -1; } }", Expect.Any);
        yield return new("num/not-on-int",
            "kernel { entry func Main() { let v = !1; } }", Expect.Rejected);
        yield return new("num/bitnot-on-bool",
            "kernel { entry func Main() { let v = ~true; } }", Expect.Rejected);
        yield return new("num/neg-on-bool",
            "kernel { entry func Main() { let v = -true; } }", Expect.Rejected);
        yield return new("num/neg-on-string",
            "kernel { entry func Main() { let v = -\"s\"; } }", Expect.Rejected);
        // Nested unary minus: the two signs must not be printed adjacent, or C reads "--".
        yield return new("num/double-negate", "kernel { entry func Main() { let v = -(-1); } }", Expect.Any);
        yield return new("num/triple-negate", "kernel { entry func Main() { let int n = 1; let v = -(-(-n)); } }", Expect.Any);
        yield return new("num/negate-postfix", "kernel { entry func Main() { let int n = 1; let v = -(n++); } }", Expect.Any);
        yield return new("num/double-bitnot", "kernel { entry func Main() { let v = ~(~1); } }", Expect.Any);
        yield return new("num/double-not", "kernel { entry func Main() { let v = !(!true); } }", Expect.Any);
        yield return new("num/int-div-zero-const",
            "kernel { entry func Main() { let v = 1 / 0; } }", Expect.Any);

        #endregion

        #region import edges
        yield return new("import/unknown", "import NoSuchModule; kernel { entry func Main() { } }", Expect.Any);
        yield return new("import/empty-path", "import \"\"; kernel { entry func Main() { } }", Expect.Any);
        yield return new("import/after-decls", "kernel { entry func Main() { } } import gata;", Expect.Any);
        yield return new("import/duplicate", "import gata; import gata; kernel { entry func Main() { } }", Expect.Any);
        yield return new("import/inside-kernel", "kernel { import gata; entry func Main() { } }", Expect.Rejected);

        #endregion
    }

    #endregion
}
