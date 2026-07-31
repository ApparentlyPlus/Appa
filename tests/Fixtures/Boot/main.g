import List;
import Console;
import Int;
import String;
import Math;
import "src/lib.g";
import "src/conc.g";

int func combine(int a, int b) { return a + b; }
int64 func combine(int64 a, int64 b) { return a + b; }

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

// Shadowed at three levels below, so the boot image proves '@shadows' is a compile-time rule with
// no runtime cost: each level's Depth() is a different function, picked by where the call is written.
int func Depth() { return 1; }

// Called from the kernel realm but written outside it, so it still sees the root Depth - the
// displacement is confined to the scope that declared it.
int func Deep() { return Depth(); }

realm kernel {
    @shadows int func Depth() { return 2; }

    entry func Main() {
        debug "M:start";
        Console.PrintLine($"depth={Depth()} root={Deep()}");
        debug "M:shadowing";

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
        // A generic free function in lib.g, over a class declared here, whose body instantiates a
        // second generic in that same file: the transitive stamping path.
        let Counter relayed = Relay(new Counter());
        relayed.Bump(6);
        let int relayVal = relayed.Value() as int;
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

        // Managed unions on the real target. Every payload reports into one Census, so the
        // numbers below are the assertion: if any generated release is missing, 'live' ends
        // non-zero; if any fires twice, 'created' and the observed weights disagree.
        let Census census = new Census();
        let int weights = 0;
        {
            let Signal s = MakeSignal(census, 3);
            weights = weights + SignalWeight(s);

            // reassignment must release the payload the slot was holding
            s = MakeSignal(census, 4);
            weights = weights + SignalWeight(s);

            // two payloads in one variant, and a variant carrying none
            let Signal pair = Signal.Both(new Tracked(census, 1), new Tracked(census, 2));
            weights = weights + SignalWeight(pair);
            let Signal quiet = Signal.Quiet();
            weights = weights + SignalWeight(quiet);

            // nested managed union, and a managed union held in a class field
            let Envelope envelope = Envelope.Wrap(MakeSignal(census, 5));
            weights = weights + EnvelopeWeight(envelope);
            let Mailbox box = new Mailbox(MakeSignal(census, 6));
            box.Put(MakeSignal(census, 7));
            weights = weights + box.Weight();
        }
        // every payload above is now out of scope, so the population must be back to zero
        let int unionLive = census.live;
        let int unionMade = census.created;

        // churn, to catch a pairing that is off by one only under repetition
        let int churn = 0;
        for (let int u = 0; u < 200; u++) {
            let Signal s = MakeSignal(census, 1);
            s = Signal.Level(2);
            churn = churn + SignalWeight(s);
        }
        let int churnLive = census.live;
        debug "M:managed-union";

        // Structural equality, on the target the language exists for. Each bit of 'eqBits' is a
        // different rule: same variant same payload, same variant different payload, two
        // payload-free variants, different variants, a nested union, and a reference-counted
        // payload compared through its own '==' rather than by address.
        let int eqBits = 0;
        if (Signal.Level(3) == Signal.Level(3)) { eqBits = eqBits + 1; }
        if (Signal.Level(3) == Signal.Level(4)) { eqBits = eqBits + 2; }
        if (Signal.Quiet() == Signal.Quiet()) { eqBits = eqBits + 4; }
        if (Signal.Level(1) != Signal.Quiet()) { eqBits = eqBits + 8; }
        if (Envelope.Sealed(2) == Envelope.Sealed(2)) { eqBits = eqBits + 16; }
        if (Envelope.Wrap(Signal.Level(1)) == Envelope.Wrap(Signal.Level(1))) { eqBits = eqBits + 32; }
        if (MakeSignal(census, 9) == MakeSignal(census, 9)) { eqBits = eqBits + 64; }
        if (MakeSignal(census, 9) == MakeSignal(census, 8)) { eqBits = eqBits + 128; }
        let int eqLive = census.live;
        debug "M:union-equality";

        // Generic unions on the real target: one template, two instantiations - one unmanaged,
        // one reference-counted - plus a recursive one over a container.
        let Maybe[int] someInt = Maybe.Found(6);
        let Maybe[int] noInt = Maybe.Missing();
        let Maybe[Tracked] someObj = Maybe.Found(new Tracked(census, 2));
        let int gsum = 0;
        match (someInt) { case Found(v) { gsum = gsum + v; } case Missing { gsum = gsum - 1; } }
        match (noInt) { case Found(v) { gsum = gsum + v; } case Missing { gsum = gsum - 1; } }
        match (someObj) { case Found(t) { gsum = gsum + t.id; } case Missing { gsum = gsum - 1; } }
        if (someInt == Maybe.Found(6)) { gsum = gsum + 100; }
        if (someInt == noInt) { gsum = gsum + 1000; }

        let List[Tree[int]] leaves = new List[Tree[int]]();
        leaves.Add(Tree.Leaf(1));
        leaves.Add(Tree.Leaf(2));
        let List[Tree[int]] top = new List[Tree[int]]();
        top.Add(Tree.Fork(leaves));
        top.Add(Tree.Leaf(3));
        let Tree[int] tree = Tree.Fork(top);
        let int leafCount = CountLeaves(tree);
        debug "M:generic-union";

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
        Console.PrintLine($"keywords={signed} scaled={scaled} deref={deref} crate={crate.item.Value() as int} relay={relayVal}");
        Console.PrintLine($"recursed={recursed} strchurn={strChurn}");
        Console.PrintLine($"uweights={weights} umade={unionMade} ulive={unionLive} uchurn={churn} uchurnlive={churnLive}");
        Console.PrintLine($"ueq={eqBits} ueqlive={eqLive}");
        Console.PrintLine($"gsum={gsum} leaves={leafCount}");

        if (acc == 9 && shown == 9900) { Console.PrintLine("REGRESSION_OK"); }
        debug "M:done";
    }

    // Deliberately the same process and thread names the userspace realm uses below. Realms are
    // separate namespaces, so both are legal - and both used to mangle to one C symbol, defined
    // once in each translation unit.
    background process App {
        thread T {
            entry func Run() { debug "M:kernel-thread"; }
        }
    }


    // Two processes of two threads each, so threads of one process and threads of different
    // processes both reach the same kernel-space object.
    background process ConcA {
        let AtomicInt owned = new AtomicInt();
        let AtomicInt settled = new AtomicInt();
        let SpinLock  ownedGuard = new SpinLock();
        let int       step = 3;
        let int64     unguarded = 0 as int64;
        let String    tag = "conca";
        let func(int) -> int scale = Triple;

        int func Triple(int x) { return x * 3; }

        void func Contribute() {
            let int i = 0;
            while (i < 40) {
                owned.Add(step as int64);
                ownedGuard.Lock();
                unguarded = unguarded + (1 as int64);
                ownedGuard.Unlock();
                i = i + 1;
            }
            settled.Increment();
        }

        thread Setup {
            entry func Run() {
                if (SyncBasics()) { debug "M:sync-basics-ok"; }
                else              { debug "M:sync-basics-BAD"; }
                KShared.Set(PrepareShared(ConcKWorkers(), true));
                debug "M:kconc-setup";
                Contribute();
                Grind(AwaitShared(true), true);
            }
        }
        thread W {
            entry func Run() {
                Contribute();
                Grind(AwaitShared(true), true);
            }
        }
        thread State {
            entry func Run() {
                while (settled.Get() < (2 as int64)) { Sys.Yield(); }
                debug "M:state-joined";
                // 2 threads x 40 rounds: 240 through the atomic, 80 through the lock. 'tag' pins
                // that a managed initialiser survived to be read from a thread that did not run it.
                let bool ok = owned.Get() == (240 as int64)
                              && unguarded == (80 as int64)
                              && step == 3
                              && tag.Length() == 5
                              && scale(7) == 21;
                if (ok) { debug "M:state-ok"; } else { debug "M:state-BAD"; }
            }
        }
    }
    background process ConcB {
        thread W { entry func Run() { Grind(AwaitShared(true), true); } }
        thread X { entry func Run() { Grind(AwaitShared(true), true); } }
    }
    background process ConcReport {
        thread R {
            entry func Run() {
                let Shared s = AwaitShared(true);
                while (s.done.Get() < (ConcKWorkers() as int64)) { Sys.Yield(); }
                debug "M:kconc-joined";
                if (VerifyShared(s, ConcKWorkers(), true)) { debug "M:kconc-ok"; }
                else                                 { debug "M:kconc-BAD"; }
                // 's' plus the pin are the only references left, so the count must be back to
                // exactly 2. Anything else means a retain or a release was lost along the way.
                if (KShared.Rc() == (2 as int64)) { debug "M:kconc-rc-ok"; }
                else                              { debug "M:kconc-rc-BAD"; }
            }
        }
    }
}

realm userspace {
    @shadows int func Depth() { return 3; }

    foreground process App {
        @shadows int func Depth() { return 4; }

        thread T {
            entry func Run() {
                debug "M:user-thread";
                // Four Depths, one name. Which one each call means is decided by the qualifier, so
                // this line is the whole scope tree read back from a running kernel.
                Console.PrintLine($"udepth={Depth()} q={userspace.App.Depth()}{userspace.Depth()}{::Depth()}");
                debug "M:qualified";
                let double p = Math.Pi();
                let Payload load = new Payload();
                load.Add(2);
                debug "M:user-arc";
                Console.PrintLine($"pi*2={Math.Round(p + p) as int} load={load.weight}");
                debug "M:user-done";
            }
        }
    }


    // One process, three threads. A userspace process has its own address space, so this shares
    // through the userspace translation unit's own slot rather than the kernel's.
    //
    // Background, not foreground: a second foreground process competes with App above for the
    // visible TTY, and App's thread then intermittently missed the image's time budget. The
    // reporter's verdict markers carry every invariant, so nothing is lost by not printing.
    background process ConcU {
        thread Setup {
            entry func Run() {
                UShared.Set(PrepareShared(ConcUWorkers(), false));
                debug "M:uconc-setup";
                Grind(AwaitShared(false), false);
            }
        }
        thread W { entry func Run() { Grind(AwaitShared(false), false); } }

        thread R {
            entry func Run() {
                let Shared s = AwaitShared(false);
                while (s.done.Get() < (ConcUWorkers() as int64)) { Sys.Yield(); }
                debug "M:uconc-joined";
                if (VerifyShared(s, ConcUWorkers(), false)) { debug "M:uconc-ok"; }
                else                                 { debug "M:uconc-BAD"; }
                if (UShared.Rc() == (2 as int64)) { debug "M:uconc-rc-ok"; }
                else                              { debug "M:uconc-rc-BAD"; }

            }
        }
    }
}
