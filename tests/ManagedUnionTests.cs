namespace Appa.Tests;

/// <summary>
/// Execution tests for managed unions. These run the program because every way it breaks still
/// compiles and prints the right answer - a leak, an over-release, a payload dropped at the wrong
/// moment - so each prints from a _deinit and the transcript pins the order.
/// </summary>
public class ManagedUnionTests
{
    /// <summary>
    /// Declarations shared by the sweep programs: a class that announces its own destruction, a
    /// union with managed, multi-managed, unmanaged and empty variants, and a union nested inside
    /// another so release has to recurse.
    /// </summary>
    private const string Prelude = """
        import Console;
        import String;

        class Res {
            public String tag;
            public func _init(String t) { self.tag = t; }
            func _deinit() { Console.PrintLine($"drop {self.tag}"); }
        }

        union Node { Leaf(int v), Held(Res r), Pair(Res a, Res b), Nil }
        union Outer { Wrap(Node n), Plain(int k) }

        Node func Make(String t) { return Node.Held(new Res(t)); }
        int func IsHeld(Node n) { match (n) { case Held(r) { return 1; } default { return 0; } } }

        class Holder {
            Node slot;
            public func _init(Node n) { self.slot = n; }
            public void func Set(Node n) { self.slot = n; }
            public int func Held() { return IsHeld(self.slot); }
        }
        """;

    /// <summary>
    /// The ownership sweep. Each section names itself, then whatever it drops appears between that
    /// line and the next - so the expected output below is a transcript of when every object died,
    /// which is precisely the contract managed unions have to keep.
    /// </summary>
    private const string OwnershipProgram = Prelude + """

        user {
            entry func Main() {
                Console.PrintLine("scope");
                { let Node a = Make("a"); Console.PrintLine($"held={IsHeld(a)}"); }

                Console.PrintLine("two-payloads");
                { let Node p = Node.Pair(new Res("p1"), new Res("p2")); Console.PrintLine($"held={IsHeld(p)}"); }

                Console.PrintLine("reassign");
                { let Node c = Make("c-old"); c = Make("c-new"); Console.PrintLine("swapped"); }

                Console.PrintLine("borrow");
                { let Node d = Make("d"); Console.PrintLine($"held={IsHeld(d)}{IsHeld(d)}"); }

                Console.PrintLine("nested");
                { let Outer e = Outer.Wrap(Make("e")); Console.PrintLine("built"); }

                Console.PrintLine("field");
                {
                    let Holder h = new Holder(Make("f-first"));
                    h.Set(Make("f-second"));
                    Console.PrintLine($"held={h.Held()}");
                }

                Console.PrintLine("unmanaged-variants");
                { let Node g = Node.Leaf(9); let Node n = Node.Nil(); Console.PrintLine($"held={IsHeld(g)}{IsHeld(n)}"); }

                Console.PrintLine("match-binding");
                {
                    let Node m = Make("m");
                    let String seen = "none";
                    match (m) { case Held(r) { seen = r.tag; } default { } }
                    Console.PrintLine($"seen={seen}");
                }

                Console.PrintLine("done");
            }
        }
        """;

    // Written out rather than computed, so a change in destruction order has to be acknowledged
    // here by a human instead of being absorbed by a clever expectation.
    private const string OwnershipExpected = """
        scope
        held=1
        drop a
        two-payloads
        held=0
        drop p1
        drop p2
        reassign
        drop c-old
        swapped
        drop c-new
        borrow
        held=11
        drop d
        nested
        built
        drop e
        field
        drop f-first
        held=1
        drop f-second
        unmanaged-variants
        held=00
        match-binding
        seen=m
        drop m
        done

        """;

    [Fact]
    public void DestructionOrderIsExact()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun(OwnershipProgram, gata, cc);
        HostedRun.AssertClean(r);
        Assert.Equal(OwnershipExpected.Replace("\r\n", "\n"), r.Output);
    }

    /// <summary>
    /// The paths where ownership is hardest: a value inside a Result, a throw past a live owner, a
    /// handler supplying a replacement, an early return with a defer, a ternary. The throw-past is
    /// the sharp one - 'keep' is live when the next call fails.
    /// </summary>
    private const string ControlFlowProgram = Prelude + """

        throws Node func MayFail(bool bad, String t) {
            if (bad) { throw; }
            return Make(t);
        }

        void func EarlyReturn() {
            let Node a = Make("early");
            defer { Console.PrintLine("deferred"); }
            if (IsHeld(a) == 1) { return; }
            Console.PrintLine("unreachable");
        }

        user {
            entry func Main() {
                Console.PrintLine("throws-ok");
                {
                    try { let Node h = MayFail(false, "h"); Console.PrintLine($"held={IsHeld(h)}"); }
                    catch { Console.PrintLine("caught"); }
                }

                Console.PrintLine("throws-past-owner");
                {
                    try {
                        let Node keep = Make("keep");
                        let Node bad = MayFail(true, "never");
                        Console.PrintLine($"unreachable={IsHeld(bad)}{IsHeld(keep)}");
                    }
                    catch { Console.PrintLine("caught"); }
                }

                Console.PrintLine("catch-handler");
                {
                    let Node j = MayFail(true, "never") catch { assign Make("fallback"); };
                    Console.PrintLine($"held={IsHeld(j)}");
                }

                Console.PrintLine("early-return");
                EarlyReturn();

                Console.PrintLine("ternary");
                {
                    let Node l = IsHeld(Make("probe")) == 1 ? Make("taken") : Make("untaken");
                    Console.PrintLine($"held={IsHeld(l)}");
                }

                Console.PrintLine("done");
            }
        }
        """;

    // 'untaken' never appears: a ternary constructs only the arm it evaluates, so the arm not
    // taken must never be built and therefore never dropped.
    private const string ControlFlowExpected = """
        throws-ok
        held=1
        drop h
        throws-past-owner
        drop keep
        caught
        catch-handler
        held=1
        drop fallback
        early-return
        deferred
        drop early
        ternary
        drop probe
        held=1
        drop taken
        done

        """;

    [Fact]
    public void ControlFlowPathsReleaseOnce()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun(ControlFlowProgram, gata, cc);
        HostedRun.AssertClean(r);
        Assert.Equal(ControlFlowExpected.Replace("\r\n", "\n"), r.Output);
    }

    /// <summary>
    /// Churns managed unions hard enough that an off-by-one in the retain/release pairing diverges
    /// rather than stranding one object. The live count is the assertion: a leak report cannot tell
    /// "released too often" from "correct", and a population count can.
    /// </summary>
    private const string StressProgram = """
        import Console;

        // Gata has no globals, so the population is counted through an object every Res holds.
        // Res keeps a strong reference to it, which also means the counter necessarily outlives
        // every object reporting into it.
        class Census {
            public int live;
            public int created;
            public func _init() { self.live = 0; self.created = 0; }
        }

        class Res {
            Census c;
            public func _init(Census c) {
                self.c = c;
                c.live = c.live + 1;
                c.created = c.created + 1;
            }
            func _deinit() { self.c.live = self.c.live - 1; }
        }

        union Node { Leaf(int v), Held(Res r), Pair(Res a, Res b), Nil }
        union Outer { Wrap(Node n), Plain(int k) }

        Node func Make(Census c) { return Node.Held(new Res(c)); }
        int func IsHeld(Node n) { match (n) { case Held(r) { return 1; } default { return 0; } } }

        user {
            entry func Main() {
                let Census c = new Census();
                let int seen = 0;
                let int i = 0;
                while (i < 1000) {
                    let Node a = Make(c);
                    a = Make(c);
                    let Outer o = Outer.Wrap(Make(c));
                    let Node p = Node.Pair(new Res(c), new Res(c));
                    seen = seen + IsHeld(a) + IsHeld(p);
                    let Node u = Node.Leaf(i);
                    seen = seen + IsHeld(u);
                    i = i + 1;
                }
                Console.PrintLine($"created={c.created} alive={c.live} seen={seen}");
            }
        }
        """;

    [Fact]
    public void RepeatedReplacementBalances()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun(StressProgram, gata, cc);
        HostedRun.AssertClean(r);
        Assert.Equal("created=5000 alive=0 seen=1000\n", r.Output);
    }

    /// <summary>
    /// A generic class instantiated over a managed union: its destructor must call the union's
    /// release, not the runtime's, which takes a void pointer and would reject an aggregate. The
    /// Monomorphizer picks the field's type and the Emitter, passes later, must classify it.
    /// </summary>
    [Fact]
    public void GenericOverManagedUnionReleases()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun(Prelude + """

            class Crate[T] { public T item; }

            user {
                entry func Main() {
                    {
                        let Crate[Node] c = new Crate[Node]();
                        c.item = Make("crated");
                        Console.PrintLine($"held={IsHeld(c.item)}");

                        // replacing the field must release what it held
                        c.item = Make("replaced");
                        Console.PrintLine("swapped");
                    }
                    Console.PrintLine("done");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("held=1\ndrop crated\nswapped\ndrop replaced\ndone\n", r.Output);
    }

    /// <summary>
    /// A managed union declared in one file and used from another. Its retain/release pair lands in
    /// the shared header and every unit must see the same one - a per-file copy would fail to link,
    /// or link and leave one file's unions uncounted.
    /// </summary>
    [Fact]
    public void ManagedUnionCrossesFileBoundaries()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var files = new Dictionary<string, string>
        {
            ["shapes.g"] = """
                import Console;
                import String;

                class Res {
                    public String tag;
                    public func _init(String t) { self.tag = t; }
                    func _deinit() { Console.PrintLine($"drop {self.tag}"); }
                }

                union Node { Leaf(int v), Held(Res r) }

                public Node func Make(String t) { return Node.Held(new Res(t)); }
                public int func IsHeld(Node n) { match (n) { case Held(r) { return 1; } default { return 0; } } }
                """,
            ["main.g"] = """
                import Console;
                import "src/shapes.g";

                user {
                    entry func Main() {
                        { let Node a = Make("a"); Console.PrintLine($"held={IsHeld(a)}"); }
                        Console.PrintLine("done");
                    }
                }
                """,
        };

        var r = HostedRun.BuildAndRun(files, gata, cc);
        HostedRun.AssertClean(r);
        Assert.Equal("held=1\ndrop a\ndone\n", r.Output);
    }

    /// <summary>
    /// One union template, several instantiations, each with its own tag layout, ARC pair and
    /// equality. Two bleeding together still compiles and reads the wrong payload, so the
    /// assertions are on values and destruction. Also pins how the instance is chosen.
    /// </summary>
    [Fact]
    public void GenericUnionInstancesStaySeparate()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import String;
            import List;

            union Maybe[V] { Found(V v), Missing }
            union Either[A, B] { Left(A a), Right(B b) }

            class Census { public int live; public func _init() { self.live = 0; } }
            class Res {
                Census c;
                public int id;
                public func _init(Census c, int id) { self.c = c; self.id = id; c.live = c.live + 1; }
                func _deinit() { self.c.live = self.c.live - 1; }
            }

            int func UnwrapInt(Maybe[int] m) {
                match (m) { case Found(v) { return v; } case Missing { return -1; } }
            }

            String func UnwrapStr(Maybe[String] m) {
                match (m) { case Found(v) { return v; } case Missing { return "none"; } }
            }

            Maybe[int] func Positive(int n) {
                if (n > 0) { return Maybe.Found(n); }
                return Maybe.Missing();
            }

            user {
                entry func Main() {
                    // Inferred from the argument, and from the binding for the payload-free variant.
                    let Maybe[int] a = Maybe.Found(7);
                    let Maybe[String] b = Maybe.Found("hi");
                    let Maybe[int] none = Maybe.Missing();
                    Console.PrintLine($"basic={UnwrapInt(a)} {UnwrapStr(b)} {UnwrapInt(none)}");

                    // Inferred from the declared return type, through both paths.
                    Console.PrintLine($"ret={UnwrapInt(Positive(4))} {UnwrapInt(Positive(-4))}");

                    // Two type parameters, and both variants of each.
                    let Either[int, String] l = Either.Left(1);
                    let Either[int, String] rr = Either.Right("x");
                    let int le = 0;
                    let String re = "?";
                    match (l) { case Left(x) { le = x; } case Right(y) { re = y; } }
                    match (rr) { case Left(x) { le = x; } case Right(y) { re = y; } }
                    Console.PrintLine($"either={le}{re}");

                    // Equality is per instantiation, and structural within one.
                    let String s1 = "s";
                    let String s2 = "s";
                    let bool e1 = Maybe.Found(1) == Maybe.Found(1);
                    let bool e2 = Maybe.Found(1) == Maybe.Found(2);
                    let bool e3 = Maybe.Found(s1) == Maybe.Found(s2);
                    Console.PrintLine($"eq={e1}{e2}{e3}");

                    // A generic union in a container, looked up by value.
                    let List[Maybe[int]] xs = new List[Maybe[int]]();
                    xs.Add(Maybe.Found(3));
                    xs.Add(none);
                    Console.PrintLine($"list={xs.Length()} idx={xs.IndexOf(Maybe.Found(3))}");

                    // ARC through an instantiation over a reference-counted payload.
                    let Census c = new Census();
                    {
                        let Maybe[Res] m = Maybe.Found(new Res(c, 1));
                        let Maybe[Res] m2 = m;
                        Console.PrintLine($"during={c.live}");
                    }
                    Console.PrintLine($"after={c.live}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal(
            "basic=7 hi -1\n" +
            "ret=4 -1\n" +
            "either=1x\n" +
            "eq=101\n" +
            "list=2 idx=0\n" +
            "during=1\n" +
            "after=0\n", r.Output);
    }

    /// <summary>
    /// 'Maybe[int].Found(7)' and the indexing it collides with, in one program - the same tokens
    /// when the brackets hold one identifier, so both directions are asserted: getting one right by
    /// breaking the other is the easy mistake, and 'arr[i].n' is ordinary.
    /// </summary>
    [Fact]
    public void InstantiationAndIndexingCoexist()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import String;
            import List;

            union Maybe[V] { Found(V v), Missing }

            class Holder { public int n; public func _init(int n) { self.n = n; } }

            int func Unwrap(Maybe[int] m) {
                match (m) { case Found(v) { return v; } case Missing { return -1; } }
            }

            user {
                entry func Main() {
                    // The type reading.
                    let Maybe[int] a = Maybe[int].Found(7);
                    let Maybe[int] n = Maybe[int].Missing();
                    let Maybe[String] s = Maybe[String].Found("hi");
                    let String unwrapped = "?";
                    match (s) { case Found(v) { unwrapped = v; } case Missing { } }

                    // Inline as a call argument: no declared type to infer from, so naming the
                    // instantiation is the only way to say it.
                    let List[Maybe[int]] xs = new List[Maybe[int]]();
                    xs.Add(Maybe[int].Missing());
                    xs.Add(Maybe[int].Found(3));

                    // The index reading of the same shape, with a variable and a bare identifier
                    // between the brackets followed by a member access.
                    let List[Holder] hs = new List[Holder]();
                    hs.Add(new Holder(11));
                    let int zero = 0;
                    let int viaCall = hs.Get(zero).n;

                    let int i = 1;
                    let [3]int nums = [4, 5, 6];
                    let int viaIndex = nums[i];
                    let int viaExpr = nums[i + 1];

                    Console.PrintLine($"type={Unwrap(a)}{Unwrap(n)} {unwrapped} " +
                                      $"inline={Unwrap(xs.Get(1))} index={viaCall}{viaIndex}{viaExpr}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("type=7-1 hi inline=3 index=1156\n", r.Output);
    }

    /// <summary>
    /// A recursive generic sum type - a polymorphic AST - and a generic function over it. Needs a
    /// template reaching for another generic through its own parameter more than one level deep
    /// ('List[Node[T]]' inside 'Node[T]'), and inference from a stamped union. Both failed once.
    /// </summary>
    [Fact]
    public void RecursiveGenericUnionIsWalkable()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import String;
            import List;

            union Node[T] { Leaf(T v), Branch(List[Node[T]] kids) }

            int func CountLeaves[T](Node[T] n) {
                match (n) {
                    case Leaf(v) { return 1; }
                    case Branch(kids) {
                        let int total = 0;
                        for k in kids { total = total + CountLeaves(k); }
                        return total;
                    }
                }
            }

            int func Depth[T](Node[T] n) {
                match (n) {
                    case Leaf(v) { return 1; }
                    case Branch(kids) {
                        let int best = 0;
                        for k in kids { let int d = Depth(k); if (d > best) { best = d; } }
                        return best + 1;
                    }
                }
            }

            user {
                entry func Main() {
                    let List[Node[int]] inner = new List[Node[int]]();
                    inner.Add(Node.Leaf(1));
                    inner.Add(Node.Leaf(2));

                    let List[Node[int]] outer = new List[Node[int]]();
                    outer.Add(Node.Branch(inner));
                    outer.Add(Node.Leaf(3));
                    let Node[int] tree = Node.Branch(outer);

                    // The same template over a reference-counted payload.
                    let List[Node[String]] skids = new List[Node[String]]();
                    skids.Add(Node.Leaf("a"));
                    skids.Add(Node.Leaf("b"));
                    let Node[String] stree = Node.Branch(skids);

                    Console.PrintLine($"int leaves={CountLeaves(tree)} depth={Depth(tree)}");
                    Console.PrintLine($"str leaves={CountLeaves(stree)} depth={Depth(stree)}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("int leaves=3 depth=3\nstr leaves=2 depth=2\n", r.Output);
    }

    /// <summary>
    /// Leaks on purpose, so the file's leak checks cannot pass by never running - every other
    /// assertion here is satisfied by an absence, which a dead detector also satisfies. Not LSan,
    /// which misses a leaked pointer still sitting in a dead stack slot.
    /// </summary>
    [Fact]
    public void LeakDetectionIsArmed()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import Runtime;

            class Census { public int live; public func _init() { self.live = 0; } }

            class Res {
                Census c;
                public func _init(Census c) { self.c = c; c.live = c.live + 1; }
                func _deinit() { self.c.live = self.c.live - 1; Console.PrintLine("drop"); }
            }

            union Node { Leaf(int v), Held(Res r) }

            user {
                entry func Main() {
                    let Census c = new Census();
                    {
                        let Node a = Node.Held(new Res(c));
                        unsafe { let Node leaked = retain(a); }   // +1 with no matching release
                    }
                    Console.PrintLine($"alive={c.live}");
                }
            }
            """, gata, cc);

        // The program itself must still run to completion - the point is that it leaks, not
        // that it crashes.
        HostedRun.AssertClean(r);

        Assert.Contains("alive=1", r.Output);
        Assert.DoesNotContain("drop", r.Output);
    }

    /// <summary>
    /// libgata's single-probe lookup API stays ownership-correct when V is managed. TryGet is the
    /// one worth running: its 'release(out); out = retain(...)' compiles to nothing for an int, so
    /// stdlib tests that store primitives leave the idiom untested.
    /// </summary>
    [Fact]
    public void SingleProbeLookupsOwnCorrectly()
    {
        var (gata, cc) = Environment();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import List;
            import Map;
            import Set;
            import Optional;
            import String;

            class Census { public int live; func _init() { self.live = 0; } }

            class Res {
                public int n;
                public Census c;
                func _init() { self.n = 0; }
                func _deinit() { if (self.c != null) { self.c.live = self.c.live - 1; } }
            }

            Res func Make(Census c, int n) {
                let Res r = new Res();
                r.c = c; r.n = n; c.live = c.live + 1;
                return r;
            }

            // Containers live and die inside this call, so the census is taken after they are gone.
            void func Body(Census c) {
                let List[Res] xs = new List[Res]();
                let Map[int, Res] m = new Map[int, Res]();
                let StringMap[Res] sm = new StringMap[Res]();
                for (let int i = 0; i < 20; i++) { xs.Add(Make(c, i)); m.Put(i, Make(c, i * 2)); }
                sm.Put("k", Make(c, 7));

                // At: in range and out of range, so both Optional variants carry a managed payload
                // through a match.
                let int sum = 0;
                for (let int i = -3; i < 23; i++) {
                    match (xs.At(i)) { case Some(v) { sum = sum + v.n; } case None { } }
                }

                // TryGet into one variable, alternating hit and miss, so the release-then-retain
                // runs against a live previous value many times over.
                let Res slot = Make(c, 0);
                let int hits = 0;
                for (let int i = 0; i < 200; i++) {
                    if (m.TryGet(i % 30, ref slot)) { hits = hits + 1; }
                }

                let Res fallback = Make(c, -1);
                Console.PrintLine($"sum={sum} hits={hits} " +
                                  $"find={IsSome(m.Find(5))}{IsSome(m.Find(99))}{IsSome(sm.Find("k"))} " +
                                  $"or={m.GetOr(5, fallback).n},{m.GetOr(99, fallback).n} " +
                                  $"during={c.live}");
            }

            user {
                entry func Main() {
                    let Census c = new Census();
                    Body(c);
                    Console.PrintLine($"live={c.live}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);

        // sum is 0..19 = 190; hits is 20 of every 30 over 200 iterations. 43 built, 42 alive:
        // slot's original is the one TryGet released before overwriting - 44 would mean the
        // release was skipped, 41 that the caller's live value was freed.
        Assert.Equal("sum=190 hits=140 find=101 or=10,-1 during=42\nlive=0\n", r.Output);
    }

    /// <summary>
    /// Locates the checkout and compiler, skipping the test when either is missing. Returns (null,
    /// null) after calling Assert.Skip, so callers return immediately.
    /// </summary>
    private static (string?, string?) Environment()
    {
        var gata = HostedRun.FindGataCheckout();
        if (gata == null) { Assert.Skip("no sibling Gata checkout found"); return (null, null); }

        var cc = HostedRun.FindCompiler();
        if (cc == null) { Assert.Skip("no host C compiler (cc/gcc/clang) found"); return (null, null); }

        return (gata, cc);
    }
}
