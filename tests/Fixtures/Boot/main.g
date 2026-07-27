// Boot regression program.
//
// Every section ends in a `debug` marker, and the test asserts each marker reaches the serial
// log. That distinguishes "the ISO booted" from "the ISO booted and every construct actually
// ran": a section that is silently skipped or faults partway leaves its marker missing, which
// checking only the final answers would never reveal. Kernel-realm markers land on COM1,
// user-realm ones on COM3; the harness reads both.

import List;
import Console;
import Int;
import String;
import Math;
import "src/lib.g";

// overloaded free functions (selective mangling)
int func combine(int a, int b) { return a + b; }
int64 func combine(int64 a, int64 b) { return a + b; }

// Same private name as lib.g's: the two must mangle apart or one silently wins.
private int func Scale(int n) { return n * 3; }

class Counter {
    int64 n;
    func _init() { self.n = (0 as int64); }
    public void func Bump(int by) { self.n = self.n + (by as int64); }
    public int64 func Value() { return self.n; }
}

// A defer whose body is a block, followed by an early return: the shape that used to fault
// the compiler outright, and whose deferred action must still run exactly once per exit.
int func DeferAndReturn(int k) {
    let Counter c = new Counter();
    c.Bump(k);
    defer { let Counter shadow = new Counter(); shadow.Bump(1); }
    if (k % 2 == 0) { return c.Value() as int; }
    return 0;
}

// Recursion with a reference-counted local and a deferred action live in every frame, so
// hundreds of ARC frames are outstanding simultaneously rather than one at a time.
int func Recurse(int depth, int acc) {
    if (depth <= 0) { return acc; }
    let Counter c = new Counter();
    c.Bump(depth);
    defer { let Counter shadow = new Counter(); shadow.Bump(1); }
    return Recurse(depth - 1, acc + (c.Value() as int));
}

// A throws function whose Result carries a managed value.
throws Counter func MaybeCounter(int k) {
    let Counter c = new Counter();
    c.Bump(k);
    if (k % 4 == 0) { throw; }
    return c;
}

kernel {
    entry func Main() {
        debug "M:start";

        // arithmetic + explicit narrowing
        let int64 total = (0 as int64);
        for (let int i = 0; i < 100; i++) {
            total = total + combine(i, i);     // int overload
        }
        let int shown = total as int;          // explicit narrowing
        debug "M:arith";

        // ARC churn: allocate/free Counters in a loop
        let Counter c = new Counter();
        for (let int k = 0; k < 2000; k++) {
            let Counter tmp = new Counter();
            tmp.Bump(k);
            c.Bump(tmp.Value() as int);
        }
        debug "M:arc";

        // nested generics + collection iteration
        let List[List[int]] grid = new List[List[int]]();
        for (let int r = 0; r < 3; r++) {
            let List[int] row = new List[int]();
            row.Add(r); row.Add(r + 1);
            grid.Add(row);
        }
        let int acc = 0;
        for row in grid { for v in row { acc += v; } }
        debug "M:generics";

        // defer + early return, both branches, with an owner live at the exit
        let int deferSum = 0;
        for (let int d = 0; d < 40; d++) { deferSum += DeferAndReturn(d); }
        debug "M:defer";

        // a catch handler supplying a managed value, and the propagating path
        let int caught = 0;
        for (let int t = 0; t < 40; t++) {
            let Counter got = MaybeCounter(t) catch { assign new Counter(); };
            caught += got.Value() as int;
        }
        for (let int u = 0; u < 20; u++) {
            try { let Counter q = MaybeCounter(u); caught += q.Value() as int; }
            catch { caught += 1; }
        }
        debug "M:throws";

        // ARC across many simultaneously live frames
        let int recursed = Recurse(200, 0);
        // reference-counted string temporaries, allocated and released in a tight loop
        let int strChurn = 0;
        for (let int q = 0; q < 500; q++) { let String tmp = $"r{q}-{q * 2}"; strChurn += tmp.Length(); }
        debug "M:pressure";

        // a generic throws function declared in another file: its Result typedef only exists
        // once this instantiation is stamped
        let int unwrapped = Unwrap(7, false) catch { assign 0; };
        let int failed = Unwrap(9, true) catch { assign 100; };
        debug "M:generic-throws";

        // a library-shaped generic declared in lib.g, instantiated over a class declared here
        let List[Counter] counters = new List[Counter]();
        for (let int b = 0; b < 20; b++) { counters.Add(new Counter()); }
        let Crate[Counter] crate = new Crate[Counter]();
        crate.item = new Counter();
        crate.item.Bump(4);
        debug "M:cross-file-generic";

        // an enum and a union declared in another file; the union carries a fixed array, so
        // its payload names an aggregate that must be emitted before the union itself
        let Grade g = Grade.High;
        let int gradeVal = 0;
        switch (g) { case Grade.Low { gradeVal = 1; } case Grade.High { gradeVal = 5; } default { } }

        let Reading rd = Reading.Point(3, 4);
        let int readSum = 0;
        match (rd) {
            case Empty { readSum = -1; }
            case Point(x, y) { readSum = x + y; }
            case Bytes(raw) { readSum = raw[0]; }
        }
        debug "M:enum-union";

        // aggregates: a zero-valued fixed array, and nested unary minus
        let [4]int zeros = default([4]int);
        let int negated = -(-(-5));
        let int flipped = ~(~9);
        debug "M:aggregates";

        // identifiers that are C keywords, and a ref parameter across a file boundary
        let int struct = 2;
        let int register = 3;
        let int signed = struct % register;
        Bump(ref signed);
        debug "M:c-keywords";

        // both files' private Scale must resolve to their own
        let int scaled = Scale(2) + LibScale(2);
        debug "M:private-mangling";

        // unsafe pointer round trip
        let int deref = 0;
        unsafe {
            let int cell = 41;
            let int* p = &cell;
            deref = *p + 1;
        }
        debug "M:unsafe";

        // strings: concat + interpolation
        Console.PrintLine("shown=" + Int.ToString(shown));
        Console.PrintLine($"counter={c.Value() as int} acc={acc}");
        Console.PrintLine($"defer={deferSum} caught={caught} unwrap={unwrapped}/{failed}");
        Console.PrintLine($"grade={gradeVal} read={readSum} zeros={zeros[0]} neg={negated} flip={flipped}");
        Console.PrintLine($"keywords={signed} scaled={scaled} deref={deref} crate={crate.item.Value() as int}");
        Console.PrintLine($"recursed={recursed} strchurn={strChurn}");

        if (acc == 9 && shown == 9900) { Console.PrintLine("REGRESSION_OK"); }
        debug "M:done";
    }
}

user {
    foreground process App {
        thread T {
            entry func Run() {
                debug "M:user-thread";
                let double p = Math.Pi();
                let Payload load = new Payload();
                load.Add(2);
                debug "M:user-arc";
                Console.PrintLine($"pi*2={Math.Round(p + p) as int} load={load.weight}");
                debug "M:user-done";
            }
        }
    }
}
