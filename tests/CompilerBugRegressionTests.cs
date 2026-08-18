namespace Appa.Tests;

/// <summary>
/// Two miscompiles found while porting a Span[T]-shaped library to Gata, both confirmed against a
/// real gcc compile of the emitted C (not just "appa accepted it") since both bugs let appa's own
/// checks pass while producing C that either computed the wrong answer or didn't compile at all.
/// </summary>
public class CompilerBugRegressionTests
{
    /// <summary>
    /// Locates the checkout and compiler, skipping the test when either is missing.
    /// </summary>
    private static (string?, string?) Environment()
    {
        var gata = HostedRun.FindGataCheckout();
        if (gata == null) { Assert.Skip("no sibling Gata checkout found"); return (null, null); }

        var cc = HostedRun.FindCompiler();
        if (cc == null) { Assert.Skip("no host C compiler (cc/gcc/clang) found"); return (null, null); }

        return (gata, cc);
    }

    /// <summary>
    /// A generic union's `T*` payload field, pattern-bound inside a foreign generic function, was
    /// treated as eligible for the pointee class's OWN operator overloads whenever T was a class type. 
    /// </summary>
    [Fact]
    public void PointerArithmeticOnClassTypedPayloadDoesNotDispatchToClassOperator()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        const string src = """
            import Console;

            union Box[T] { Has(T* ptr) }

            class Adder {
                public int x;
                func _init() { self.x = 0; }
                public operator Adder func +(Adder other) {
                    let r = new Adder();
                    r.x = self.x + other.x;
                    return r;
                }
            }

            Box[T] func Shift[T](Box[T] b, int n) {
                match (b) {
                    case Has(ptr) {
                        unsafe { return Box[T].Has(ptr + n); }
                    }
                }
            }

            realm userspace {
                entry func Main() {
                    let Adder a0 = new Adder();
                    a0.x = 1;
                    let Adder a1 = new Adder();
                    a1.x = 2;

                    // Sanity: the class's own '+' still dispatches normally on real values.
                    let Adder sum = a0 + a1;
                    Console.PrintLine("sum=" + (sum.x as String));

                    unsafe {
                        let Adder* arr = alloc((2 as usize) * sizeof(Adder)) as Adder*;
                        arr[0] = a0;
                        arr[1] = a1;
                        let Box[Adder] b0 = Box[Adder].Has(arr);
                        let Box[Adder] b1 = Shift(b0, 1);
                        match (b1) {
                            case Has(p) {
                                let Adder shifted = *p;
                                Console.PrintLine("shifted.x=" + (shifted.x as String));
                            }
                        }
                    }
                }
            }
            """;

        var r = HostedRun.BuildAndRun(src, gata, cc);
        HostedRun.AssertClean(r);
        Assert.Equal("sum=3\nshifted.x=2\n", r.Output);
    }

    /// <summary>
    /// A generic method on a class or module (its OWN type parameter, distinct from any enclosing
    /// class's) that calls a generic free function only reachable through that one call site had
    /// its callee's definition silently dropped.
    /// </summary>
    [Fact]
    public void GenericMethodCallingGenericFreeFunctionEmitsTheCallee()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        const string src = """
            import Console;

            union Sp[T] { View(T* ptr, int len) }

            int func LenOf[T](Sp[T] s) {
                match (s) { case View(ptr, len) { return len; } }
            }

            T* func RawOf[T](Sp[T] s) {
                match (s) { case View(ptr, len) { return ptr; } }
            }

            module Algorithms {
                public int func SumSpan[T](Sp[T] s) {
                    let n = LenOf(s);
                    let total = 0;
                    unsafe {
                        let d = RawOf(s);
                        let i = 0;
                        while (i < n) { total = total + d[i]; i = i + 1; }
                    }
                    return total;
                }
            }

            realm userspace {
                entry func Main() {
                    unsafe {
                        let int* buf = alloc((3 as usize) * sizeof(int)) as int*;
                        buf[0] = 1; buf[1] = 2; buf[2] = 3;
                        let Sp[int] s = Sp[int].View(buf, 3);
                        let int total = Algorithms.SumSpan(s);
                        Console.PrintLine("total=" + (total as String));
                    }
                }
            }
            """;

        var r = HostedRun.BuildAndRun(src, gata, cc);
        HostedRun.AssertClean(r);
        Assert.Equal("total=6\n", r.Output);
    }
}
