// The Release boot fixture.
//
// The main boot image is built Debug and announces itself with 'debug' markers. Release rejects
// both 'debug' and 'panic', so none of that can be reused - and the kernel had therefore never
// been compiled at '-O3 -fstrict-aliasing -fomit-frame-pointer -fno-stack-protector', which is
// what a shipped GatOS image is built with. Everything here reports through Console instead, and
// the test builds this same source twice: whatever the Debug image prints, the Release image has
// to print. That makes the oracle the pair rather than a table, so an optimiser that reorders a
// refcount write against a field write shows up as a differing line.

import Console;
import String;
import List;
import Sync;

class Counter {
    public int n;
    func _init() { self.n = 0; }
    public void func Bump(int k) { self.n = self.n + k; }
}

// Announces its own destruction, so the transcript pins destructor count and order - the part an
// aliasing-driven reorder would change without changing any arithmetic.
class Tracked {
    public int v;
    func _init(int v) { self.v = v; }
    func _deinit() { Console.PrintLine($"drop {self.v}"); }
}

class Vec {
    public int x;
    func _init(int a) { self.x = a; }
    public operator Vec func +(Vec o) { return new Vec(self.x + o.x); }
    public operator bool func ==(Vec o) { return self.x == o.x; }
}

class Box[T] { public T v; func _init(T x) { self.v = x; } public T func Get() { return self.v; } }
T func Echo[T](T x) { return x; }

union Shape { None, One(Tracked t), Pair(Tracked a, Tracked b) }

int func Weigh(Shape s) {
    match (s) {
        case None { return 0; }
        case One(t) { return t.v; }
        case Pair(a, b) { return a.v + b.v; }
    }
}

throws int func MaybeHalve(int n) { if (n % 2 == 1) { throw; } return n / 2; }

realm kernel {

    // Process-scoped state, reached from the entry's own realm through the process's function.
    background process Work {
        let AtomicInt hits = new AtomicInt();
        let int seed = 7;

        int func Take() { hits.Increment(); return seed + (hits.Get() as int); }

        thread T { entry func Run() { hits.Set(Take() as int64); } }
    }

    entry func Main() {
        // Reference counting through a generic, with the object outliving one scope.
        {
            let Box[Tracked] b = new Box[Tracked](new Tracked(1));
            Console.PrintLine($"R:box {b.Get().v}");
        }

        // A managed union, reassigned so the first payload has to be released.
        {
            let Shape s = Shape.Pair(new Tracked(2), new Tracked(3));
            Console.PrintLine($"R:pair {Weigh(s)}");
            s = Shape.One(new Tracked(4));
            Console.PrintLine($"R:one {Weigh(s)}");
        }

        // Operator overloading, where the temporary is the interesting one.
        {
            let Vec a = new Vec(2);
            let Vec b = new Vec(5);
            let Vec c = a + b;
            Console.PrintLine($"R:vec {c.x} {a == b} {c == new Vec(7)}");
        }

        // Allocation churn plus a container, so refcounts are touched in a loop.
        {
            let Counter c = new Counter();
            for (let int i = 0; i < 500; i++) { c.Bump(i % 3); }
            let List[Tracked] xs = new List[Tracked]();
            for (let int i = 0; i < 4; i++) { xs.Add(new Tracked(10 + i)); }
            let int total = 0;
            for x in xs { total = total + x.v; }
            Console.PrintLine($"R:churn {c.n} {xs.Length()} {total}");
        }

        // Throws, caught both ways, and a generic inferring from a class declared here.
        {
            let int acc = 0;
            for (let int i = 0; i < 5; i++) {
                let int h = MaybeHalve(i) catch { assign -1; };
                acc = acc + h;
            }
            let int t = 0;
            try { t = MaybeHalve(7); } catch { t = -9; }
            let Tracked e = Echo(new Tracked(20));
            Console.PrintLine($"R:throws {acc} {t} {e.v}");
        }

        // Integer edges the optimiser is allowed to assume things about, and a defer.
        {
            defer { Console.PrintLine("R:defer"); }
            let int neg = -2147483647;
            let uint u = 4294967295;
            Console.PrintLine($"R:nums {neg} {u} {neg / 3} {neg % 3}");
        }

        // A string built at runtime, so the comparison is not a folded literal.
        {
            let String s = "ab";
            for (let int i = 0; i < 3; i++) { s = s + "c"; }
            Console.PrintLine($"R:str {s} {s.Length()}");
        }

        // A qualifier reaches outward, never inward, so the entry cannot read the process's
        // state - the thread does that on its own. What the entry can prove is that the image
        // got here with all of it linked in.
        Console.PrintLine("R:done");
    }
}
