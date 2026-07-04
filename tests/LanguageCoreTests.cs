namespace Appa.Tests;

using Appa;

// Ported from the historical torture/{good,bad,warn} fixture corpus, restricted to
// the subset with zero imports - these test language semantics only, with no
// dependency on libgata (String/Collections/etc). Each entry keeps the source
// fixture's original filename as its first data value for traceability.
/// <summary>
/// Data-driven language-core coverage: every torture fixture that needs no
/// import, run in-process via SingleFileCompile with no disk, no libgata.
/// </summary>
public class LanguageCoreTests
{
    /// <summary>
    /// A language-core program with no diagnostics of any kind - it must transpile cleanly.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoodPrograms))]
    public void GoodProgramTranspilesCleanly(string name, string src)
    {
        var (diag, module) = SingleFileCompile.Check(src);
        Assert.False(diag.HasErrors, $"{name} should transpile with no errors but got: " +
            string.Join(", ", diag.All.Where(d => d.Severity == Severity.Error).Select(d => d.Code)));
        Assert.NotNull(module);
    }

    public static TheoryData<string, string> GoodPrograms()
    {
        var data = new TheoryData<string, string>();
        data.Add("compound_bitwise", """
            kernel { entry func Main() {
              let int f = 0;
              f |= 8; f &= 12; f ^= 4; f <<= 2; f >>= 1;
              let uint u = 1u;
              u |= 2u; u <<= 3;
            } }
            """);
        data.Add("cond_bool", """
            kernel { entry func Main() {
              let int i = 0;
              while (i < 3) { i++; }
              if (i == 3) { } else { }
            } }
            """);
        data.Add("explicit_narrow", """
            kernel { entry func Main() {
              let int64 a = 5;
              let int b = a as int;   // explicit narrowing OK
            } }
            """);
        data.Add("for_init_no_let", """
            // ForInit's second form: a plain expression/assignment reusing an existing variable
            // instead of declaring a new one with `let`. Also confirms the real grammar
            // restriction this is paired with: the third for-clause is `[ Expr ]` only, not a
            // full ForInit — `for (i = 0; i < 5; i = i + 1)` is a syntax error (assignment is
            // not an Expr in Gata; `i++`/`i--` are, since they're postfix Expr operators).
            kernel { entry func Main() {
              let int i = 0;
              let int sum = 0;
              for (i = 0; i < 5; i++) { sum = sum + i; }
            } }
            """);
        data.Add("generic_funcs", """
            // Generic free functions with argument-type inference, monomorphized per instance.
            T func max[T](T a, T b) { if (a > b) { return a; } return b; }
            T func identity[T](T x) { return x; }
            T func firstOf[T](T a, T b) { return a; }

            kernel { entry func Main() {
              let int    a = max(3, 7);                          // T=int
              let int64  b = max((10 as int64), (4 as int64));   // T=int64 (distinct instance)
              let int    c = max(5, 9);                          // reuses the T=int instance
              let int    d = identity(42);
              let int    e = firstOf(1, 2);
              if (a + c + d + e == 7 + 9 + 42 + 1) { } else { }
            } }
            """);
        data.Add("keep_free_func", """
            // `@keep` on a free function never called from any Gata expression, only from raw
            // native text (`gata_helper()`), keeps it alive through Dce AND keeps its readable
            // `gata_helper` C name through Densifier (a non-`@keep`, non-entry free function
            // would normally collapse to a dense token like "_g3").
            native {
                void call_it(void) { gata_helper(); }
            }
            @keep
            void func helper() { }
            kernel { entry func Main() { } }
            """);
        data.Add("kernel_float", """
            // Float now works in any realm — GatOS restricts SSE only in interrupt-context files,
            // so the language no longer bans floating point in the kernel. (Was bad/float_kernel.g
            // + bad/double_kernel.g, which expected the now-removed G030/G031.)
            kernel { entry func Main() {
              let double d = 1.0;
              let float  f = 2.5f;
            } }
            """);
        data.Add("literals", """
            // Numeric literal forms: hex, u/l suffixes, magnitude widening, and float exponents
            // / f suffix. (Float is legal in any realm now; the split here is just for variety.)
            kernel { entry func Main() {
              let h1   = 0xFF;                       // hex
              let h2   = 0xFFULL;                    // hex + unsigned-int64 suffix
              let wide = 0x8000000000000000;         // wide hex, no suffix -> uint64
              let mask = 0x7FFFFFFFFFFFFFFFULL;      // fdlibm sign mask
              let lng  = 100L;                       // int64 suffix
              let usn  = 4096u;                      // unsigned suffix
              let big  = 5000000000;                 // > int32 -> widens to int64
              let umax = 18446744073709551615;       // > int64 -> uint64
              let dec  = 42;                         // plain int
            } }
            user { foreground process App { thread T { entry func Run() {
              let huge  = 1.0e+300;                  // double, positive exponent
              let small = 5.96046447753906250000e-08;// double, negative exponent
              let neg   = 1.5e-10;
              let flt   = 2.5f;                       // float suffix
              let fexp  = 2e10f;                      // exponent + float suffix
            } } } }
            """);
        data.Add("native_brace_in_string", """
            // ReadBalanced (the native{}/native type/fields{} brace-counter) used to count
            // every literal `{`/`}` byte with zero awareness of C comments or string/char
            // literals — a brace inside either desynced the depth counter and truncated the
            // native block early, leaking the remaining raw C as bogus Gata tokens. Confirms
            // both a `}` inside a C string and inside a `//` comment no longer desync it.
            native {
                char* s = "only a closing brace }";
                // a comment with } an unbalanced brace
            }
            kernel { entry func Main() { } }
            """);
        data.Add("process_thread_handles", """
            // `Process`/`Thread` are opaque handle types (SimpleTypeName alternatives to a
            // plain class name) that lower straight to `void*` — meant for whatever a real
            // platform binding hands back from spawning a process/thread. Nothing in libgata
            // exposes one yet, so this test supplies its own tiny native stand-in. Catching
            // this gap surfaced a real parser bug: `let Process p = ...` previously failed to
            // parse at all ("expected Ident, found 'Process'") — LooksLikeTypeAndIdent only
            // ever recognized TK.Ident as a type-lookahead start, never TK.Process/TK.Thread,
            // the same class of bug `let CustomType*` was before its own fix. Fixed in
            // Parser.cs's LooksLikeTypeAndIdent.
            native {
                void* make_handle(void) { return (void*)1; }
            }
            @extern func make_handle() -> Process;

            void func storeHandle(Process p, Process* slot) {
                unsafe { *slot = p; }
            }

            kernel { entry func Main() {
                let Process p = make_handle();
                let Thread t = make_handle() as Thread;
                if (p != null && t != null) {
                    unsafe {
                        let Process saved = null;
                        storeHandle(p, &saved);
                        if (saved != null) { }
                    }
                }
            } }
            """);
        data.Add("return_ok", """
            kernel { entry func Main() { let x = add(2,3); } }
            int func add(int a, int b) { return a + b; }
            """);
        data.Add("switch_enum", """
            // Enums + switch (multi-label, default, definite-return) + break/continue in cases.
            enum Dir { North, East, South, West }

            int func turns(Dir d) {
              switch (d) {
                case Dir.North { return 0; }
                case Dir.East, Dir.West { return 1; }
                default { return 2; }
              }
            }

            kernel { entry func Main() {
              let int hits = 0;
              for (let int i = 0; i < 8; i++) {
                switch (i) {
                  case 0, 1 { continue; }     // targets the loop, not the switch
                  case 7 { break; }           // targets the loop
                  default { hits = hits + 1; }
                }
              }
              let int t = turns(Dir.West);
              if (t + hits == 1 + 4) { } else { }
            } }
            """);
        data.Add("ternary", """
            kernel { entry func Main() {
              let int a = 3;
              let int b = 7;
              // basic selection
              let int m = a > b ? a : b;
              // nested / right-associative chain
              let int band = a < 2 ? 0 : (a < 5 ? 1 : 2);
              // numeric widening: int64 arm + int arm unify to int64
              let int64 w = a > 0 ? (m as int64) : b;
              // used inside a larger expression
              let int s = (a > b ? a : b) + (a < b ? a : b);
              if (m == 7) { } else { }
            } }
            """);
        data.Add("topology_threads", """
            // A foreground and a background process, each with a thread; thread modes vary.
            int func work(int a) { return a + 1; }

            kernel { entry func Main() { } }

            user {
              foreground process App {
                thread Ui      { entry func Run() { let int a = work(1); } }
                thread Worker { entry func Run() { let int b = work(2); } }
              }
              background process Daemon {
                thread Loop { entry func Run() { let int c = work(3); } }
              }
            }
            """);
        data.Add("widening", """
            kernel { entry func Main() {
              let int a = 5;
              let int64 b = a;       // widening int->int64 is implicit
              let short c = 3;
              let int64 d = c;       // widening short->int64
            } }
            """);
        data.Add("widths", """
            // Width-explicit primitive names + alias folding (long ≡ int64, size_t ≡ usize) +
            // a throws return that exercises Result_<canonical> naming + an unsafe pointer local.
            throws int64 func parse(int x) {
              if (x < 0) { throw; }
              return (x as int64);
            }

            kernel { entry func Main() {
              let int    a = 1;
              let int64  b = 2;          // 64-bit signed
              let uint   d = 3u;
              let uint64 e = 4u;
              let short  f = (5 as short);
              let ushort g = (6 as ushort);
              let byte   h = (7 as byte);
              let sbyte  i = (8 as sbyte);
              let usize  n = (9 as usize);

              // widening across the width names is implicit; the wide arithmetic stays 64-bit
              let int64 sum = (a as int64) + b + (d as int64) + (e as int64)
                            + (f as int64) + (g as int64) + (h as int64) + (i as int64)
                            + (n as int64);

              try {
                let int64 parsed = parse(sum as int);   // Result_int64 round-trips
                sum = sum + parsed;
              } catch { }

              // pointer locals with a width keyword work like any primitive
              unsafe {
                let byte* p = &h;
                let byte first = *p;
                sum = sum + (first as int64);
              }
              if (sum > 0) { } else { }
            } }
            """);
        return data;
    }

    /// <summary>
    /// A language-core program with a single semantic error - it must fail with
    /// exactly the diagnostic code named in the fixture's original header.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadPrograms))]
    public void BadProgramFailsWithExpectedCode(string name, string expectedCode, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        Assert.True(diag.HasErrors, $"{name} expected {expectedCode} but produced no errors");
        Assert.Contains(diag.All, d => d.Severity == Severity.Error && d.Code == expectedCode);
    }

    public static TheoryData<string, string, string> BadPrograms()
    {
        var data = new TheoryData<string, string, string>();
        data.Add("ambiguous_overload", "G015", """
            int func F(int a, float b) { return a; }
            int func F(float a, int b) { return b; }
            kernel { entry func Main() { let int x = F(1, 1); } }
            """);
        data.Add("annotation_on_block", "G000", """
            // `@intrinsic`/`@preamble` have no defined meaning on a `kernel{}`/`user{}` block —
            // previously silently parsed and discarded; now a hard error.
            @intrinsic(alloc)
            kernel { entry func Main() { } }
            """);
        data.Add("bitwise_assign_float", "G004", """
            user { foreground process App { thread T { entry func Run() {
              let int x = 0; let double d = 2.0;
              x <<= d;
            } } } }
            """);
        data.Add("bool_arith", "G004", """
            kernel { entry func Main() { let x = true + 5; } }
            """);
        data.Add("break_in_catch", "G022", """
            // 'break' inside a catch with no enclosing loop has nothing to break out of.
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              try { let int x = risky(1); sink(x); } catch { break; }
            } }
            """);
        data.Add("call_entry", "G030", """
            kernel { entry func Main() { Main(); } }
            """);
        data.Add("call_nonclass", "G006", """
            kernel { entry func Main() { let int n = 5; n.foo(); } }
            """);
        data.Add("cast_to_void", "G028", """
            kernel { entry func Main() { let int x = 5; let y = x as void; } }
            """);
        data.Add("catch_binding", "G000", """
            // `catch` takes no binding (a throw carries no payload). The old `catch (e)` form
            // is gone — anything but a block after `catch` is a syntax error.
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            kernel { entry func Main() {
              try { let int v = risky(1); } catch (e) { }
            } }
            """);
        data.Add("char_bad_escape", "G000", """
            // Mirror of string_bad_escape.g for char literals: an unrecognized escape used to
            // silently drop the backslash and become the literal char ('\q' silently became
            // 'q', zero diagnostic). Now rejected.
            kernel { entry func Main() { let c = '\q'; } }
            """);
        data.Add("char_empty", "G000", """
            // `''` (empty char literal) used to be silently accepted and become NUL with no
            // diagnostic. Now rejected.
            kernel { entry func Main() { let c = ''; } }
            """);
        data.Add("compound_str", "G004", """
            kernel { entry func Main() { let int a = 0; a -= "x"; } }
            """);
        data.Add("compound_void", "G007", """
            kernel { entry func Main(void x) { x -= "s"; } }
            """);
        data.Add("cond_int", "G029", """
            kernel { entry func Main() { if (5) { } } }
            """);
        data.Add("continue_outside", "G022", """
            kernel { entry func Main() { continue; } }
            """);
        data.Add("default_private_field", "G035", """
            // Fields/methods are private by default now (no `private` keyword needed) — only
            // an explicit `public` member is reachable from outside its declaring class.
            class Box { int v; func _init(int x) { self.v = x; } }
            kernel { entry func Main() {
              let Box b = new Box(1);
              let int z = b.v;          // default-private field accessed from outside
            } }
            """);
        data.Add("default_private_method", "G035", """
            // Same as default_private_field.g but for a method, and via a module rather than a
            // class — modules are subject to the same private-by-default rule.
            module M { int func helper() { return 1; } }
            kernel { entry func Main() { let int z = M.helper(); } }
            """);
        data.Add("defer_defer", "G004", """
            // `defer defer X;` — nesting a defer inside a defer's own action — used to crash
            // the compiler (InvalidOperationException: "Collection was modified; enumeration
            // operation may not execute") rather than producing a diagnostic: Ownership.cs's
            // ReleaseFrame splices a frame's deferred actions by iterating `f.Defers` and
            // re-lowering each one, and lowering a nested defer inserts into that same list
            // mid-iteration. Found by tests/stress/fuzz_grammar.py (seed 200, case 202). Same
            // family as defer_return.g/defer_break.g — a defer body cannot itself defer.
            kernel { entry func Main() { defer defer { } } }
            """);
        data.Add("dual_return_type", "G000", """
            // A free function used to accept a return type written BOTH before `func` and
            // after the parameter list, with the trailing one silently winning and the leading
            // one discarded with no warning. Now a hard error: write the return type once.
            int func Foo() -> String { return "hi"; }
            kernel { entry func Main() { } }
            """);
        data.Add("dup_param", "G003", """
            kernel { entry func Main() { } }
            int func f(int x, int x) { return x; }
            """);
        data.Add("duplicate_intrinsic", "G018", """
            @intrinsic(alloc)
            void* func A(usize n) { return null; }
            @intrinsic(alloc)
            void* func B(usize n) { return null; }
            kernel { entry func Main() { } }
            """);
        data.Add("enum_trailing_comma", "G000", """
            // Enums no longer allow a trailing comma before the closing brace — was an
            // inconsistent exception versus union (see union_trailing_comma.g), now rejected
            // the same way for both.
            enum Color { Red, Green, Blue, }
            kernel { entry func Main() { } }
            """);
        data.Add("field_as_method", "G006", """
            class C { int x; func f() { let y = self.x(); } }
            kernel { entry func Main() { } }
            """);
        data.Add("field_static", "G000", """
            // `static` has no meaning on a field either — Gata has no class-level/shared field
            // storage model, so a "static field" would just be a regular per-instance field
            // wearing a misleading label. Was silently accepted (never read by any check); now
            // a hard error, same as `static` on a free function (see static_free_func.g).
            class C { static int x; }
            kernel { entry func Main() { } }
            """);
        data.Add("field_with_entry", "G000", """
            // Same as field_with_throws.g but for `entry`.
            class C { entry int x; }
            kernel { entry func Main() { } }
            """);
        data.Add("field_with_throws", "G000", """
            // A field used to silently accept `entry`/`throws`/annotations preceding it (the
            // flags were just dropped, no error) since the parser consumes them unconditionally
            // before deciding method-vs-field. Now a hard error.
            class C { throws int x; }
            kernel { entry func Main() { } }
            """);
        data.Add("for_step_not_expr", "G000", """
            // The for-loop's third clause is `[ Expr ]` only (ForStmt grammar), not a full
            // ForInit/statement — an assignment is not an Expr in Gata (it's a statement-level
            // form), so `i = i + 1` in step position is a syntax error. `i++`/`i--` work
            // because postfix `++`/`--` ARE Expr-level operators (see for_init_no_let.g).
            kernel { entry func Main() {
              let int i = 0;
              for (i = 0; i < 5; i = i + 1) { }
            } }
            """);
        data.Add("forin_int", "G032", """
            kernel { entry func Main() { for i in 5 { } } }
            """);
        data.Add("forin_partial_protocol", "G032", """
            // Has Get(int) but no Length() -> int — the structural for..in protocol requires both.
            class Bag {
                int func Get(int i) { return i; }
            }
            kernel { entry func Main() {
                let Bag b = new Bag();
                for x in b { }
            } }
            """);
        data.Add("funcptr_ref_callsite", "G037", """
            // Mirror of funcptr_ref_decay.g: `ref` written at an indirect-call site (through a
            // function-pointer-typed variable) used to be silently stripped to the plain
            // argument with zero diagnostic — ResolveCall unwraps every `ref x` to `x`
            // unconditionally before the callee is known, and unlike direct calls (which
            // re-check the original `ref` against the callee's signature in CoerceArgs),
            // nothing did that for an indirect call. Since a function-pointer type can never
            // have a `ref` parameter (see funcptr_ref_decay.g), writing `ref` here is always
            // wrong — now rejected unconditionally.
            int func AddOne(int x) { return x + 1; }
            kernel { entry func Main() {
              let f = AddOne;
              let int y = 5;
              let int r = f(ref y);
            } }
            """);
        data.Add("funcptr_ref_decay", "G004", """
            // A `ref`-taking function used to decay into a function-pointer value with the
            // `ref` silently erased — `func(...) -> R` types have no slot to express which
            // parameters are `ref` at all, so the generated C function-pointer type and the
            // real function's C signature disagreed (`int` vs `int*`): assigning/calling
            // through the pointer compiled, but was a real type mismatch (undefined behavior
            // at runtime), not just an unchecked feature. Now rejected at the point the value
            // would be created.
            func Inc(ref int x) { x = x + 1; }
            kernel { entry func Main() {
              let f = Inc;
            } }
            """);
        data.Add("generic_conflict", "G009", """
            // The same type parameter inferred to two different types from different arguments.
            T func same[T](T a, T b) { return a; }
            kernel { entry func Main() { let int64 z = same(3, (4 as int64)); } }
            """);
        data.Add("generic_expr", "G000", """
            kernel { entry func Main() { let List[Main(x)] a; } }
            """);
        data.Add("generic_no_infer", "G007", """
            // A type parameter that never appears in a parameter type cannot be inferred.
            T func make[T]() { return default(T); }
            kernel { entry func Main() { let int z = make(); } }
            """);
        data.Add("hex_overflow", "G004", """
            // A hex literal that does not fit in 64 bits is rejected, like its decimal sibling.
            kernel { entry func Main() { let x = 0x1FFFFFFFFFFFFFFFFF; } }
            """);
        data.Add("huge_int", "G004", """
            kernel { entry func Main() { let x = 999999999999999999999999; } }
            """);
        data.Add("index_int", "G012", """
            kernel { entry func Main() { let int n = 5; let x = n[0]; } }
            """);
        data.Add("instance_on_static", "G014", """
            class C { public static int func F() { return 1; } }
            kernel { entry func Main() { let C c = new C(); let int x = c.F(); } }
            """);
        data.Add("intrinsic_on_native_block", "G041", """
            // Mirror of preamble_on_func.g: `@intrinsic` only means anything on a native
            // type/method/free-func/extern-func (something with a C name to bind a role to); a
            // plain top-level native block has no such name and only ever reads `@preamble`.
            // Previously silently discarded; now rejected.
            @intrinsic(alloc)
            native { #kernel: int x; #user: int x; }
            kernel { entry func Main() { } }
            """);
        data.Add("keep_on_enum", "G000", """
            // `@keep` only means anything on a class or free function — Dce never
            // independently considers an enum's reachability the way it does a class/free
            // function (enums aren't even in `m.Classes`/`m.FreeFunctions`), so `@keep` would
            // be a no-op there. Rejected the same way every other annotation already is on an
            // enum/union/kernel/user/process.
            @keep
            enum Color { Red, Green }
            kernel { entry func Main() { } }
            """);
        data.Add("keep_on_method", "G041", """
            // `@keep` only matters on a free function or a class — a method rides with its
            // owning class's reachability (Dce.MarkClass marks every method/operator
            // unconditionally once the class itself is reachable), so `@keep` on a method
            // specifically would be a no-op. Rejected.
            class C { @keep int func F() { return 1; } }
            kernel { entry func Main() { } }
            """);
        data.Add("keyword_as_ident_unreachable", "G000", """
            // `kernel`/`user`/`thread`/`process` used to ALSO be accepted in Primary
            // expression position alongside IDENT — syntactically you could write `thread()`
            // or bare `process`. But every declaration site (let/func/param/field/generic-
            // param/enum-member) requires a strict IDENT token, so no symbol could ever be
            // declared with one of these names — the carve-out could parse a reference, but
            // that reference could never resolve to anything (always G005). Removed entirely
            // rather than left as harmless dead code: now a parse error, same as any other
            // unexpected keyword in expression position.
            kernel { entry func Main() {
              let int x = thread + process;
            } }
            """);
        data.Add("logical_int", "G004", """
            kernel { entry func Main() { let x = 3 && 4; } }
            """);
        data.Add("match_duplicate_default", "G000", """
            // Same fix as switch_duplicate_default.g, for `match`.
            union Shape { Circle(float radius), Square(float side), Point }

            float func Area(Shape s) {
                match (s) {
                    default { return -1.0; }
                    case Circle(r) { return r * r * 3.0f; }
                    default { return -2.0; }
                }
            }

            kernel { entry func Main() { } }
            """);
        data.Add("method_with_entry", "G000", """
            // entry modifier is not allowed on class methods.
            class C {
                entry void func Run() {}
            }
            kernel { entry func Main() {} }
            """);
        data.Add("missing_return", "G027", """
            kernel { entry func Main() { } int func f() { } }
            """);
        data.Add("narrow_long_int", "G007", """
            // `long` is no longer a Gata type name — widths are explicit. Use `int64`.
            // (The C-flavoured spellings long/size_t/int32_t survive only inside native bodies.)
            kernel { entry func Main() { let long a = 5; } }
            """);
        data.Add("native_in_class", "G000", """
            // Raw C fields in a class go in a `fields { }` block; a bare `native { }` is not a member.
            class Box {
              int v;
              native { int extra; }
            }
            kernel { entry func Main() { } }
            """);
        data.Add("nested_kernel", "G000", """
            kernel { kernel { entry func Main() { } } }
            """);
        data.Add("new_module", "G011", """
            module M { }
            kernel { entry func Main() { let M m = new M(); } }
            """);
        data.Add("no_matching_overload", "G016", """
            class Widget { }
            int func F(int a) { return a; }
            int func F(String a) { return 0; }
            kernel { entry func Main() { let Widget w = new Widget(); let int x = F(w); } }
            """);
        data.Add("nonsense_generic_class_header", "G000", """
            // A class's generic parameter list used to reuse the loose type-reference grammar,
            // so `class Foo[Bar[Baz]]` parsed as a single mangled "parameter" nothing could
            // ever refer to — syntactically legal nonsense with no real meaning. Now requires
            // each parameter to be a plain name.
            class Foo[Bar[Baz]] { int v; }
            kernel { entry func Main() { } }
            """);
        data.Add("not_lvalue", "G034", """
            kernel { entry func Main() { 5 = 3; } }
            """);
        data.Add("operator_index_no_setter", "G038", """
            // A class with only an `operator []` getter cannot be assigned through `[]`.
            class RO {
                int v;
                operator func [](int i) -> int { return self.v; }
            }
            kernel { entry func Main() {
                let RO r = new RO();
                r[0] = 5;
            } }
            """);
        data.Add("operator_index_undeclared", "G012", """
            // `[]` indexing is nominal — a class without a declared `operator []` cannot be indexed,
            // even if it happens to have a structurally-iterable Length()/Get(int) shape.
            class Plain {
                int v;
                int func Length() { return 1; }
                int func Get(int i) { return self.v; }
            }
            kernel { entry func Main() {
                let Plain p = new Plain();
                let int x = p[0];
            } }
            """);
        data.Add("panic_user", "G031", """
            // panic is valid only in the kernel realm; using it in a userspace thread is an error.
            kernel { entry func Main() {} }
            user { foreground process A { thread T { entry func Run() { panic "nope"; } } } }
            """);
        data.Add("preamble_on_func", "G041", """
            // `@preamble` only means anything on a top-level native block (it sets which
            // section of generated C the block's body lands in); a free function only ever
            // reads `@intrinsic`. Previously silently discarded (BindIntrinsics' loop skipped
            // any non-IntrinsicAnnotation with no error); now rejected.
            @preamble(user)
            int func Foo() { return 1; }
            kernel { entry func Main() { } }
            """);
        data.Add("private_field", "G035", """
            class Box { private int v; func _init(int x) { self.v = x; } }
            kernel { entry func Main() {
              let Box b = new Box(1);
              let int z = b.v;          // private field accessed from outside
            } }
            """);
        data.Add("private_method", "G035", """
            module M { private int func helper() { return 1; } }
            kernel { entry func Main() { let int z = M.helper(); } }
            """);
        data.Add("process_non_thread", "G000", """
            // A process body may contain only 'thread' declarations, not loose functions.
            user { process App { int func oops() { return 1; } } }
            """);
        data.Add("redeclare", "G003", """
            kernel { entry func Main() { let int s = 10; let String s = "hi"; } }
            """);
        data.Add("ref_missing_at_callsite", "G037", """
            // `ref` is required at the call site, not just the declaration.
            func Swap[T](ref T a, ref T b) { let tmp = a; a = b; b = tmp; }
            kernel { entry func Main() {
                let int x = 1;
                let int y = 2;
                Swap(x, y);
            } }
            """);
        data.Add("ref_not_lvalue", "G034", """
            // A `ref` argument must be an lvalue.
            func Swap[T](ref T a, ref T b) { let tmp = a; a = b; b = tmp; }
            kernel { entry func Main() {
                let int y = 2;
                Swap(ref 5, ref y);
            } }
            """);
        data.Add("ref_on_non_ref_param", "G037", """
            // Passing `ref` to a parameter that isn't declared `ref` is also rejected.
            void func TakesInt(int n) { }
            kernel { entry func Main() {
                let int x = 1;
                TakesInt(ref x);
            } }
            """);
        data.Add("return_empty", "G010", """
            kernel { entry func Main() { } int func f() { return; } }
            """);
        data.Add("return_void_val", "G010", """
            kernel { entry func Main() { } void func f() { return 5; } }
            """);
        data.Add("static_free_func", "G040", """
            // `static` only means anything on a class/module method; a free function is
            // already never an instance member, so the modifier is a category error here.
            static int func helper() { return 1; }
            kernel { entry func Main() { let int x = helper(); } }
            """);
        data.Add("static_on_instance", "G013", """
            class C { public int func F() { return 1; } }
            kernel { entry func Main() { let int x = C.F(); } }
            """);
        data.Add("string_bad_escape", "G000", """
            // An unrecognized escape sequence (anything but \n \t \r \0 \' \" \\) used to be
            // silently passed through verbatim into the generated C, which gcc warns on and
            // silently drops the backslash for at the C level — a behavior difference the
            // Gata source gave zero indication of. Now rejected at the lexer.
            kernel { entry func Main() { let s = "hi \q there"; } }
            """);
        data.Add("string_raw_newline", "G000", """
            // A raw, unescaped newline inside a string literal used to be accepted and
            // forwarded verbatim into the generated C string literal, splitting it across
            // two physical lines — guaranteed invalid C ("missing terminating \" character").
            // Now rejected at the lexer as an unterminated string.
            kernel { entry func Main() { let s = "line1
            line2"; } }
            """);
        data.Add("switch_duplicate_default", "G000", """
            // `switch` used to place no limit on how many `default` arms appear — each one
            // silently OVERWROTE the previous, discarding its entire parsed body with no
            // warning. Now a hard error: at most one `default`.
            kernel { entry func Main() {
              let int x = 5;
              switch (x) {
                default { x = 1; }
                case 1 { x = 2; }
                default { x = 3; }
              }
            } }
            """);
        data.Add("switch_not_int", "G004", """
            kernel { entry func Main() {
              let String s = "x";
              switch (s) { default { } }
            } }
            """);
        data.Add("ternary_branch", "G004", """
            // The two arms have incompatible types (int vs String) and cannot be unified.
            kernel { entry func Main() {
              let int a = 1;
              let int x = a > 0 ? 5 : "no";
            } }
            """);
        data.Add("ternary_class_mismatch", "G004", """
            // Ternary arms of unrelated types (int vs a class) cannot be unified.
            class Box { int v; func _init(int x) { self.v = x; } }
            kernel { entry func Main() {
              let int x = true ? 1 : new Box(2);
            } }
            """);
        data.Add("ternary_cond", "G029", """
            // The ternary condition must be 'bool', not an int.
            kernel { entry func Main() {
              let int a = 1;
              let int b = a ? 2 : 3;
            } }
            """);
        data.Add("thread_mode_not_allowed", "G043", """
            // foreground or background modifiers are not allowed on thread declarations.
            kernel { entry func Main() {} }
            user {
              foreground process App {
                background thread Worker { entry func Run() {} }
              }
            }
            """);
        data.Add("thread_nested", "G000", """
            // Threads are pure topology — they cannot nest.
            user { process App { thread T { thread U { entry func R() { } } } } }
            """);
        data.Add("thread_no_entry", "G000", """
            // A thread must declare its single 'entry func'.
            user { process App { thread T { } } }
            """);
        data.Add("thread_stray_func", "G000", """
            // A thread body holds exactly one 'entry func' — no helper functions.
            // (Define helpers at module/file scope and call them from the entry.)
            user { process App { thread T {
              entry func R() { }
              int func helper() { return 1; }
            } } }
            """);
        data.Add("throw_outside_throws", "G021", """
            // A bare 'throw;' in a function that is neither 'throws' nor inside a 'try'.
            kernel { entry func Main() {
              throw;
            } }
            """);
        data.Add("throws_as_arg", "G021", """
            // A throwing call cannot hide inside a larger expression (here, an argument):
            // its Result has nowhere to be unpacked.
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              let int x = sink(risky(1));
            } }
            """);
        data.Add("throws_in_concat", "G021", """
            // A throwing call inside a string concatenation is still nested — rejected.
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              let String s = "n=" + risky(1);
            } }
            """);
        data.Add("throws_ternary_arm", "G021", """
            // A throwing call as a ternary arm is nested inside an expression — rejected.
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              let int x = true ? risky(1) : 0;
            } }
            """);
        data.Add("throws_unhandled", "G021", """
            // A throwing call at statement root but outside any 'try' / 'throws' function.
            // (Regression guard: this used to crash the backend instead of diagnosing.)
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              let int x = risky(1);
            } }
            """);
        data.Add("trailing_return_type_only", "G000", """
            // A free function's return type only goes before `func`, period — not just "not
            // both at once" (see dual_return_type.g): the trailing `-> Type` spelling on its
            // own, with no leading type at all, is also rejected. `int func Foo()`, never
            // `func Foo() -> int`.
            func Foo() -> int { return 1; }
            kernel { entry func Main() { } }
            """);
        data.Add("undef_var", "G005", """
            kernel { entry func Main() { let int x = y + 1; } }
            """);
        data.Add("union_trailing_comma", "G000", """
            // Unions don't allow a trailing comma before the closing brace. Enums used to
            // (inconsistently) — see enum_trailing_comma.g, now rejected the same way.
            union U { A, B, }
            kernel { entry func Main() { } }
            """);
        data.Add("unknown_annotation", "G000", """
            // An unrecognized `@word` used to silently lex as a plain Ident, usable as a
            // variable/function name — it type-checked clean all the way through the
            // resolver and only failed at the C toolchain stage ("stray '@' in program"),
            // nowhere near the actual Gata source defect. Now rejected at the lexer.
            kernel { entry func Main() { let int @foo = 5; } }
            """);
        data.Add("unknown_intrinsic_role", "G017", """
            @intrinsic(totally_bogus_role)
            int func Foo() { return 0; }
            kernel { entry func Main() { } }
            """);
        data.Add("unsafe_required_addrof", "G033", """
            kernel { entry func Main() { let int x = 5; let int* p = &x; } }
            """);
        data.Add("void_local", "G007", """
            kernel { entry func Main() { let void v; } }
            """);
        data.Add("void_param", "G007", """
            kernel { entry func Main() { } func f(void x) { } }
            """);
        data.Add("width_narrow", "G004", """
            // Narrowing a 64-bit value into a 32-bit one needs an explicit `as`.
            kernel { entry func Main() {
              let int64 a = 5;
              let int   b = a;
            } }
            """);
        data.Add("wrong_arg_count", "G008", """
            int func add(int a, int b) { return a + b; }
            kernel { entry func Main() { let int x = add(1); } }
            """);
        return data;
    }

    /// <summary>
    /// A language-core program that produces the warning code named in the
    /// fixture's original header.
    /// </summary>
    [Theory]
    [MemberData(nameof(WarnPrograms))]
    public void WarnProgramProducesExpectedCode(string name, string expectedCode, string src)
    {
        var (diag, _) = SingleFileCompile.Check(src);
        var codes = diag.All.Select(d => d.Code).ToList();
        Assert.True(codes.Contains(expectedCode), $"{name} expected warning {expectedCode} but got [{string.Join(',', codes)}]");
    }

    public static TheoryData<string, string, string> WarnPrograms()
    {
        var data = new TheoryData<string, string, string>();
        data.Add("empty_block", "G025", """
            kernel { entry func Main() { if (true) { } } }
            """);
        data.Add("redundant_return", "G026", """
            void func F() { return; }
            kernel { entry func Main() { F(); } }
            """);
        data.Add("unreachable_code", "G024", """
            int func F() { return 1; let int x = 2; }
            kernel { entry func Main() { let int y = F(); } }
            """);
        data.Add("unused_variable", "G023", """
            kernel { entry func Main() { let int x = 5; } }
            """);
        return data;
    }
}
