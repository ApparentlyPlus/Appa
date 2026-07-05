namespace Appa.Tests;

using Appa;

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
        var data = new TheoryData<string, string>
        {
            { "compound_bitwise", """
            kernel { entry func Main() {
              let int f = 0;
              f |= 8; f &= 12; f ^= 4; f <<= 2; f >>= 1;
              let uint u = 1u;
              u |= 2u; u <<= 3;
            } }
            """ },
            { "cond_bool", """
            kernel { entry func Main() {
              let int i = 0;
              while (i < 3) { i++; }
              if (i == 3) { } else { }
            } }
            """ },
            { "explicit_narrow", """
            kernel { entry func Main() {
              let int64 a = 5;
              let int b = a as int;
            } }
            """ },
            { "for_init_no_let", """
            kernel { entry func Main() {
              let int i = 0;
              let int sum = 0;
              for (i = 0; i < 5; i++) { sum = sum + i; }
            } }
            """ },
            { "generic_funcs", """
            T func max[T](T a, T b) { if (a > b) { return a; } return b; }
            T func identity[T](T x) { return x; }
            T func firstOf[T](T a, T b) { return a; }

            kernel { entry func Main() {
              let int    a = max(3, 7);
              let int64  b = max((10 as int64), (4 as int64));
              let int    c = max(5, 9);
              let int    d = identity(42);
              let int    e = firstOf(1, 2);
              if (a + c + d + e == 7 + 9 + 42 + 1) { } else { }
            } }
            """ },
            { "keep_free_func", """
            native {
                void call_it(void) { gata_helper(); }
            }
            @keep
            void func helper() { }
            kernel { entry func Main() { } }
            """ },
            { "kernel_float", """
            kernel { entry func Main() {
              let double d = 1.0;
              let float  f = 2.5f;
            } }
            """ },
            { "literals", """
            kernel { entry func Main() {
              let h1   = 0xFF;
              let h2   = 0xFFULL;
              let wide = 0x8000000000000000;
              let mask = 0x7FFFFFFFFFFFFFFFULL;
              let lng  = 100L;
              let usn  = 4096u;
              let big  = 5000000000;
              let umax = 18446744073709551615;
              let dec  = 42;
            } }
            user { foreground process App { thread T { entry func Run() {
              let huge  = 1.0e+300;
              let small = 5.96046447753906250000e-08;
              let neg   = 1.5e-10;
              let flt   = 2.5f;
              let fexp  = 2e10f;
            } } } }
            """ },
            { "native_brace_in_string", """
            native {
                char* s = "only a closing brace }";
            }
            kernel { entry func Main() { } }
            """ },
            { "process_thread_handles", """
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
            """ },
            { "return_ok", """
            kernel { entry func Main() { let x = add(2,3); } }
            int func add(int a, int b) { return a + b; }
            """ },
            { "switch_enum", """
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
                  case 0, 1 { continue; }
                  case 7 { break; }
                  default { hits = hits + 1; }
                }
              }
              let int t = turns(Dir.West);
              if (t + hits == 1 + 4) { } else { }
            } }
            """ },
            { "ternary", """
            kernel { entry func Main() {
              let int a = 3;
              let int b = 7;
              let int m = a > b ? a : b;
              let int band = a < 2 ? 0 : (a < 5 ? 1 : 2);
              let int64 w = a > 0 ? (m as int64) : b;
              let int s = (a > b ? a : b) + (a < b ? a : b);
              if (m == 7) { } else { }
            } }
            """ },
            { "topology_threads", """
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
            """ },
            { "widening", """
            kernel { entry func Main() {
              let int a = 5;
              let int64 b = a;
              let short c = 3;
              let int64 d = c;
            } }
            """ },
            { "widths", """
            throws int64 func parse(int x) {
              if (x < 0) { throw; }
              return (x as int64);
            }

            kernel { entry func Main() {
              let int    a = 1;
              let int64  b = 2;
              let uint   d = 3u;
              let uint64 e = 4u;
              let short  f = (5 as short);
              let ushort g = (6 as ushort);
              let byte   h = (7 as byte);
              let sbyte  i = (8 as sbyte);
              let usize  n = (9 as usize);

              let int64 sum = (a as int64) + b + (d as int64) + (e as int64)
                            + (f as int64) + (g as int64) + (h as int64) + (i as int64)
                            + (n as int64);

              try {
                let int64 parsed = parse(sum as int);
                sum = sum + parsed;
              } catch { }

              unsafe {
                let byte* p = &h;
                let byte first = *p;
                sum = sum + (first as int64);
              }
              if (sum > 0) { } else { }
            } }
            """ }
        };
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
        var data = new TheoryData<string, string, string>
        {
            { "ambiguous_overload", "G015", """
            int func F(int a, float b) { return a; }
            int func F(float a, int b) { return b; }
            kernel { entry func Main() { let int x = F(1, 1); } }
            """ },
            { "annotation_on_block", "G048", """
            @intrinsic(alloc)
            kernel { entry func Main() { } }
            """ },
            { "bitwise_assign_float", "G004", """
            user { foreground process App { thread T { entry func Run() {
              let int x = 0; let double d = 2.0;
              x <<= d;
            } } } }
            """ },
            { "bool_arith", "G004", """
            kernel { entry func Main() { let x = true + 5; } }
            """ },
            { "break_in_catch", "G022", """
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              try { let int x = risky(1); sink(x); } catch { break; }
            } }
            """ },
            { "call_entry", "G030", """
            kernel { entry func Main() { Main(); } }
            """ },
            { "call_nonclass", "G006", """
            kernel { entry func Main() { let int n = 5; n.foo(); } }
            """ },
            { "cast_to_void", "G028", """
            kernel { entry func Main() { let int x = 5; let y = x as void; } }
            """ },
            { "catch_binding", "G044", """
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            kernel { entry func Main() {
              try { let int v = risky(1); } catch (e) { }
            } }
            """ },
            { "char_bad_escape", "G047", """
            kernel { entry func Main() { let c = '\q'; } }
            """ },
            { "char_empty", "G046", """
            kernel { entry func Main() { let c = ''; } }
            """ },
            { "compound_str", "G004", """
            kernel { entry func Main() { let int a = 0; a -= "x"; } }
            """ },
            { "compound_void", "G007", """
            kernel { entry func Main(void x) { x -= "s"; } }
            """ },
            { "cond_int", "G029", """
            kernel { entry func Main() { if (5) { } } }
            """ },
            { "continue_outside", "G022", """
            kernel { entry func Main() { continue; } }
            """ },
            { "default_private_field", "G035", """
            class Box { int v; func _init(int x) { self.v = x; } }
            kernel { entry func Main() {
              let Box b = new Box(1);
              let int z = b.v;
            } }
            """ },
            { "default_private_method", "G035", """
            module M { int func helper() { return 1; } }
            kernel { entry func Main() { let int z = M.helper(); } }
            """ },
            { "defer_defer", "G004", """
            kernel { entry func Main() { defer defer { } } }
            """ },
            { "dual_return_type", "G053", """
            int func Foo() -> String { return "hi"; }
            kernel { entry func Main() { } }
            """ },
            { "dup_param", "G003", """
            kernel { entry func Main() { } }
            int func f(int x, int x) { return x; }
            """ },
            { "duplicate_intrinsic", "G018", """
            @intrinsic(alloc)
            void* func A(usize n) { return null; }
            @intrinsic(alloc)
            void* func B(usize n) { return null; }
            kernel { entry func Main() { } }
            """ },
            { "enum_trailing_comma", "G052", """
            enum Color { Red, Green, Blue, }
            kernel { entry func Main() { } }
            """ },
            { "field_as_method", "G006", """
            class C { int x; func f() { let y = self.x(); } }
            kernel { entry func Main() { } }
            """ },
            { "field_static", "G053", """
            class C { static int x; }
            kernel { entry func Main() { } }
            """ },
            { "field_with_entry", "G053", """
            class C { entry int x; }
            kernel { entry func Main() { } }
            """ },
            { "field_with_throws", "G053", """
            class C { throws int x; }
            kernel { entry func Main() { } }
            """ },
            { "for_step_not_expr", "G045", """
            kernel { entry func Main() {
              let int i = 0;
              for (i = 0; i < 5; i = i + 1) { }
            } }
            """ },
            { "forin_int", "G032", """
            kernel { entry func Main() { for i in 5 { } } }
            """ },
            { "forin_partial_protocol", "G032", """
            class Bag {
                int func Get(int i) { return i; }
            }
            kernel { entry func Main() {
                let Bag b = new Bag();
                for x in b { }
            } }
            """ },
            { "funcptr_ref_callsite", "G037", """
            int func AddOne(int x) { return x + 1; }
            kernel { entry func Main() {
              let f = AddOne;
              let int y = 5;
              let int r = f(ref y);
            } }
            """ },
            { "funcptr_ref_decay", "G004", """
            func Inc(ref int x) { x = x + 1; }
            kernel { entry func Main() {
              let f = Inc;
            } }
            """ },
            { "generic_conflict", "G009", """
            T func same[T](T a, T b) { return a; }
            kernel { entry func Main() { let int64 z = same(3, (4 as int64)); } }
            """ },
            { "generic_expr", "G044", """
            kernel { entry func Main() { let List[Main(x)] a; } }
            """ },
            { "generic_no_infer", "G007", """
            T func make[T]() { return default(T); }
            kernel { entry func Main() { let int z = make(); } }
            """ },
            { "hex_overflow", "G004", """
            kernel { entry func Main() { let x = 0x1FFFFFFFFFFFFFFFFF; } }
            """ },
            { "huge_int", "G004", """
            kernel { entry func Main() { let x = 999999999999999999999999; } }
            """ },
            { "index_int", "G012", """
            kernel { entry func Main() { let int n = 5; let x = n[0]; } }
            """ },
            { "instance_on_static", "G014", """
            class C { public static int func F() { return 1; } }
            kernel { entry func Main() { let C c = new C(); let int x = c.F(); } }
            """ },
            { "intrinsic_on_native_block", "G041", """
            @intrinsic(alloc)
            native { #kernel: int x; #user: int x; }
            kernel { entry func Main() { } }
            """ },
            { "keep_on_enum", "G048", """
            @keep
            enum Color { Red, Green }
            kernel { entry func Main() { } }
            """ },
            { "keep_on_method", "G041", """
            class C { @keep int func F() { return 1; } }
            kernel { entry func Main() { } }
            """ },
            { "keyword_as_ident_unreachable", "G044", """
            kernel { entry func Main() {
              let int x = thread + process;
            } }
            """ },
            { "logical_int", "G004", """
            kernel { entry func Main() { let x = 3 && 4; } }
            """ },
            { "match_duplicate_default", "G003", """
            union Shape { Circle(float radius), Square(float side), Point }

            float func Area(Shape s) {
                match (s) {
                    default { return -1.0; }
                    case Circle(r) { return r * r * 3.0f; }
                    default { return -2.0; }
                }
            }

            kernel { entry func Main() { } }
            """ },
            { "method_with_entry", "G053", """
            class C {
                entry void func Run() {}
            }
            kernel { entry func Main() {} }
            """ },
            { "missing_return", "G027", """
            kernel { entry func Main() { } int func f() { } }
            """ },
            { "narrow_long_int", "G007", """
            kernel { entry func Main() { let long a = 5; } }
            """ },
            { "native_in_class", "G044", """
            class Box {
              int v;
              native { int extra; }
            }
            kernel { entry func Main() { } }
            """ },
            { "nested_kernel", "G051", """
            kernel { kernel { entry func Main() { } } }
            """ },
            { "new_module", "G011", """
            module M { }
            kernel { entry func Main() { let M m = new M(); } }
            """ },
            { "no_matching_overload", "G016", """
            class Widget { }
            int func F(int a) { return a; }
            int func F(String a) { return 0; }
            kernel { entry func Main() { let Widget w = new Widget(); let int x = F(w); } }
            """ },
            { "nonsense_generic_class_header", "G053", """
            class Foo[Bar[Baz]] { int v; }
            kernel { entry func Main() { } }
            """ },
            { "not_lvalue", "G034", """
            kernel { entry func Main() { 5 = 3; } }
            """ },
            { "operator_index_no_setter", "G038", """
            class RO {
                int v;
                operator func [](int i) -> int { return self.v; }
            }
            kernel { entry func Main() {
                let RO r = new RO();
                r[0] = 5;
            } }
            """ },
            { "operator_index_undeclared", "G012", """
            class Plain {
                int v;
                int func Length() { return 1; }
                int func Get(int i) { return self.v; }
            }
            kernel { entry func Main() {
                let Plain p = new Plain();
                let int x = p[0];
            } }
            """ },
            { "panic_user", "G031", """
            kernel { entry func Main() {} }
            user { foreground process A { thread T { entry func Run() { panic "nope"; } } } }
            """ },
            { "preamble_on_func", "G041", """
            @preamble(user)
            int func Foo() { return 1; }
            kernel { entry func Main() { } }
            """ },
            { "private_field", "G035", """
            class Box { private int v; func _init(int x) { self.v = x; } }
            kernel { entry func Main() {
              let Box b = new Box(1);
              let int z = b.v;
            } }
            """ },
            { "private_method", "G035", """
            module M { private int func helper() { return 1; } }
            kernel { entry func Main() { let int z = M.helper(); } }
            """ },
            { "process_non_thread", "G053", """
            user { process App { int func oops() { return 1; } } }
            """ },
            { "redeclare", "G003", """
            kernel { entry func Main() { let int s = 10; let String s = "hi"; } }
            """ },
            { "ref_missing_at_callsite", "G037", """
            func Swap[T](ref T a, ref T b) { let tmp = a; a = b; b = tmp; }
            kernel { entry func Main() {
                let int x = 1;
                let int y = 2;
                Swap(x, y);
            } }
            """ },
            { "ref_not_lvalue", "G034", """
            func Swap[T](ref T a, ref T b) { let tmp = a; a = b; b = tmp; }
            kernel { entry func Main() {
                let int y = 2;
                Swap(ref 5, ref y);
            } }
            """ },
            { "ref_on_non_ref_param", "G037", """
            void func TakesInt(int n) { }
            kernel { entry func Main() {
                let int x = 1;
                TakesInt(ref x);
            } }
            """ },
            { "return_empty", "G010", """
            kernel { entry func Main() { } int func f() { return; } }
            """ },
            { "return_void_val", "G010", """
            kernel { entry func Main() { } void func f() { return 5; } }
            """ },
            { "static_free_func", "G040", """
            static int func helper() { return 1; }
            kernel { entry func Main() { let int x = helper(); } }
            """ },
            { "static_on_instance", "G013", """
            class C { public int func F() { return 1; } }
            kernel { entry func Main() { let int x = C.F(); } }
            """ },
            { "string_bad_escape", "G047", """
            kernel { entry func Main() { let s = "hi \q there"; } }
            """ },
            { "string_raw_newline", "G046", """
            kernel { entry func Main() { let s = "line1
            line2"; } }
            """ },
            { "switch_duplicate_default", "G003", """
            kernel { entry func Main() {
              let int x = 5;
              switch (x) {
                default { x = 1; }
                case 1 { x = 2; }
                default { x = 3; }
              }
            } }
            """ },
            { "switch_not_int", "G004", """
            kernel { entry func Main() {
              let String s = "x";
              switch (s) { default { } }
            } }
            """ },
            { "ternary_branch", "G004", """
            kernel { entry func Main() {
              let int a = 1;
              let int x = a > 0 ? 5 : "no";
            } }
            """ },
            { "ternary_class_mismatch", "G004", """
            class Box { int v; func _init(int x) { self.v = x; } }
            kernel { entry func Main() {
              let int x = true ? 1 : new Box(2);
            } }
            """ },
            { "ternary_cond", "G029", """
            kernel { entry func Main() {
              let int a = 1;
              let int b = a ? 2 : 3;
            } }
            """ },
            { "thread_mode_not_allowed", "G043", """
            kernel { entry func Main() {} }
            user {
              foreground process App {
                background thread Worker { entry func Run() {} }
              }
            }
            """ },
            { "thread_nested", "G051", """
            user { process App { thread T { thread U { entry func R() { } } } } }
            """ },
            { "thread_no_entry", "G053", """
            user { process App { thread T { } } }
            """ },
            { "thread_stray_func", "G053", """
            user { process App { thread T {
              entry func R() { }
              int func helper() { return 1; }
            } } }
            """ },
            { "throw_outside_throws", "G021", """
            kernel { entry func Main() {
              throw;
            } }
            """ },
            { "throws_as_arg", "G021", """
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              let int x = sink(risky(1));
            } }
            """ },
            { "throws_in_concat", "G021", """
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              let String s = "n=" + risky(1);
            } }
            """ },
            { "throws_ternary_arm", "G021", """
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              let int x = true ? risky(1) : 0;
            } }
            """ },
            { "throws_unhandled", "G021", """
            throws int func risky(int x) { if (x < 0) { throw; } return x; }
            int func sink(int a) { return a; }
            kernel { entry func Main() {
              let int x = risky(1);
            } }
            """ },
            { "trailing_return_type_only", "G053", """
            func Foo() -> int { return 1; }
            kernel { entry func Main() { } }
            """ },
            { "undef_var", "G005", """
            kernel { entry func Main() { let int x = y + 1; } }
            """ },
            { "union_trailing_comma", "G052", """
            union U { A, B, }
            kernel { entry func Main() { } }
            """ },
            { "unknown_annotation", "G048", """
            kernel { entry func Main() { let int @foo = 5; } }
            """ },
            { "unknown_intrinsic_role", "G017", """
            @intrinsic(totally_bogus_role)
            int func Foo() { return 0; }
            kernel { entry func Main() { } }
            """ },
            { "unsafe_required_addrof", "G033", """
            kernel { entry func Main() { let int x = 5; let int* p = &x; } }
            """ },
            { "void_local", "G007", """
            kernel { entry func Main() { let void v; } }
            """ },
            { "void_param", "G007", """
            kernel { entry func Main() { } func f(void x) { } }
            """ },
            { "width_narrow", "G004", """
            kernel { entry func Main() {
              let int64 a = 5;
              let int   b = a;
            } }
            """ },
            { "wrong_arg_count", "G008", """
            int func add(int a, int b) { return a + b; }
            kernel { entry func Main() { let int x = add(1); } }
            """ }
        };
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
        var data = new TheoryData<string, string, string>
        {
            { "empty_block", "G025", """
            kernel { entry func Main() { if (true) { } } }
            """ },
            { "redundant_return", "G026", """
            void func F() { return; }
            kernel { entry func Main() { F(); } }
            """ },
            { "unreachable_code", "G024", """
            int func F() { return 1; let int x = 2; }
            kernel { entry func Main() { let int y = F(); } }
            """ },
            { "unused_variable", "G023", """
            kernel { entry func Main() { let int x = 5; } }
            """ }
        };
        return data;
    }
}
