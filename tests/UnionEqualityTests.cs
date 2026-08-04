namespace Appa.Tests;

/// <summary>
/// Execution tests for union equality: same variant, and its fields equal by whatever '==' already
/// means for each. These run the program because every interesting way it can be wrong still
/// produces valid C returning a bool - only running it tells them apart.
/// </summary>
public class UnionEqualityTests
{
    /// <summary>
    /// The full N x N comparison matrix over hand-picked values, because the failure modes are
    /// relational: reading both fields from one operand looks right on the diagonal, and a tag
    /// mixup shows only between specific pairs. 100 comparisons cost nothing.
    /// </summary>
    private const string MatrixProgram = """
        import Console;
        import String;
        import List;

        union U { Nil, K(int n), Pt(int x, int y), S(String s), Arr([2]int a) }

        realm userspace {
            entry func Main() {
                let List[U] vs = new List[U]();
                vs.Add(U.Nil());            // 0
                vs.Add(U.K(1));             // 1
                vs.Add(U.K(2));             // 2
                vs.Add(U.Pt(1, 2));         // 3
                vs.Add(U.Pt(2, 1));         // 4  - same fields as 3, swapped
                vs.Add(U.S("ab"));          // 5
                vs.Add(U.S("zz"));          // 6
                vs.Add(U.Arr([1, 2]));      // 7
                vs.Add(U.Arr([1, 3]));      // 8
                vs.Add(U.S("a" + "b"));     // 9  - equal to 5 by value, a different object

                let String row = "";
                for (let int i = 0; i < vs.Length(); i++) {
                    for (let int j = 0; j < vs.Length(); j++) {
                        row = row + (vs.Get(i) == vs.Get(j) ? "1" : "0");
                    }
                }
                Console.PrintLine(row);

                // '!=' must be the exact negation, not an independently generated comparison.
                let int mismatches = 0;
                for (let int i = 0; i < vs.Length(); i++) {
                    for (let int j = 0; j < vs.Length(); j++) {
                        if ((vs.Get(i) == vs.Get(j)) == (vs.Get(i) != vs.Get(j))) {
                            mismatches = mismatches + 1;
                        }
                    }
                }
                Console.PrintLine($"mismatches={mismatches}");
            }
        }
        """;

    /// <summary>
    /// Equivalence classes of the ten values above: every value differs from every other, except
    /// index 9, which is a separately built String with the same contents as index 5 and must
    /// therefore compare equal to it.
    /// </summary>
    private static readonly int[] Classes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 5];

    [Fact]
    public void EqualityMatrix()
    {
        var (gata, cc) = Env();
        if (gata == null || cc == null) return;

        var expected = new System.Text.StringBuilder();
        foreach (int a in Classes)
            foreach (int b in Classes)
                expected.Append(a == b ? '1' : '0');

        var r = HostedRun.BuildAndRun(MatrixProgram, gata, cc);
        HostedRun.AssertClean(r);
        Assert.Equal($"{expected}\nmismatches=0\n", r.Output);
    }

    /// <summary>
    /// A class payload compares through the class's '==' when it declares one, by address otherwise
    /// - the rule that keeps union equality from inventing new semantics. It breaks silently,
    /// surfacing only as a container lookup that never finds anything.
    /// </summary>
    [Fact]
    public void ClassPayloadUsesOwnEquality()
    {
        var (gata, cc) = Env();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;

            class Valued {
                public int n;
                public func _init(int n) { self.n = n; }
                public operator bool func ==(Valued o) { return self.n == o.n; }
            }

            class Plain { public int n; public func _init(int n) { self.n = n; } }

            union U { V(Valued v), P(Plain p) }

            realm userspace {
                entry func Main() {
                    // Distinct objects, equal contents. Valued declares '==', so these are equal.
                    let bool byValue = U.V(new Valued(5)) == U.V(new Valued(5));
                    let bool byValueDiff = U.V(new Valued(5)) == U.V(new Valued(6));

                    // Plain declares none, so its references compare by address - exactly as a
                    // bare '==' on two Plain references would.
                    let Plain shared = new Plain(5);
                    let bool sameObject = U.P(shared) == U.P(shared);
                    let bool otherObject = U.P(new Plain(5)) == U.P(new Plain(5));

                    Console.PrintLine($"{byValue}{byValueDiff}{sameObject}{otherObject}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("truefalsetruefalse\n", r.Output);
    }

    /// <summary>
    /// A nested union recurses into its own generated equality rather than comparing the inner
    /// struct bit for bit - which would compare the inner payload's dead variants too.
    /// </summary>
    [Fact]
    public void NestedUnionsCompareStructurally()
    {
        var (gata, cc) = Env();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import String;

            union Inner { A(String s), B(int n), C }
            union Outer { W(Inner i), K(int n) }

            realm userspace {
                entry func Main() {
                    let bool same = Outer.W(Inner.A("x")) == Outer.W(Inner.A("x"));
                    let bool innerDiff = Outer.W(Inner.A("x")) == Outer.W(Inner.A("y"));
                    let bool innerVariant = Outer.W(Inner.A("x")) == Outer.W(Inner.B(1));
                    let bool outerVariant = Outer.W(Inner.C()) == Outer.K(0);
                    let bool empties = Outer.W(Inner.C()) == Outer.W(Inner.C());
                    Console.PrintLine($"{same}{innerDiff}{innerVariant}{outerVariant}{empties}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("truefalsefalsefalsetrue\n", r.Output);
    }

    /// <summary>
    /// The container case this feature exists for: a union in a List has to work with the
    /// operations defined in terms of '==', IndexOf and Contains, which is what a program actually
    /// does with a sum type and exactly what did not compile before.
    /// </summary>
    [Fact]
    public void UnionsWorkInListOps()
    {
        var (gata, cc) = Env();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;
            import String;
            import List;

            union Tok { Word(String s), Num(int n), End }

            realm userspace {
                entry func Main() {
                    let List[Tok] ts = new List[Tok]();
                    ts.Add(Tok.Word("let"));
                    ts.Add(Tok.Num(42));
                    ts.Add(Tok.End());

                    // Looked up by a separately constructed value, which is the whole point.
                    let int w = ts.IndexOf(Tok.Word("let"));
                    let int n = ts.IndexOf(Tok.Num(42));
                    let int e = ts.IndexOf(Tok.End());
                    let int missing = ts.IndexOf(Tok.Num(7));

                    Console.PrintLine($"idx={w}{n}{e} missing={missing} len={ts.Length()}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("idx=012 missing=-1 len=3\n", r.Output);
    }

    /// <summary>
    /// Equality over a managed union must not disturb reference counts. It takes its operands by
    /// value and only reads them, but that is worth pinning rather than assuming, since the
    /// generated function receives a copy of a union that owns a reference.
    /// </summary>
    [Fact]
    public void ComparingKeepsOwnership()
    {
        var (gata, cc) = Env();
        if (gata == null || cc == null) return;

        var r = HostedRun.BuildAndRun("""
            import Console;

            class Census { public int live; public func _init() { self.live = 0; } }
            class Tracked {
                Census c;
                public int id;
                public func _init(Census c, int id) { self.c = c; self.id = id; c.live = c.live + 1; }
                func _deinit() { self.c.live = self.c.live - 1; }
                public operator bool func ==(Tracked o) { return self.id == o.id; }
            }

            union U { T(Tracked t), K(int n) }

            realm userspace {
                entry func Main() {
                    let Census c = new Census();
                    let int equal = 0;
                    {
                        let U a = U.T(new Tracked(c, 1));
                        let U b = U.T(new Tracked(c, 1));
                        // Compared many times: a retain or release leaking out of the generated
                        // equality would show up as a drifting population, not as a wrong answer.
                        for (let int i = 0; i < 500; i++) {
                            if (a == b) { equal = equal + 1; }
                        }
                        Console.PrintLine($"during={c.live}");
                    }
                    Console.PrintLine($"equal={equal} after={c.live}");
                }
            }
            """, gata, cc);

        HostedRun.AssertClean(r);
        Assert.Equal("during=2\nequal=500 after=0\n", r.Output);
    }

    /// <summary>
    /// A generated equality goes only into realms declaring the operators it calls. A class inside
    /// 'realm userspace { }' is emitted into uproc.c alone, so a kmain.c copy calls an undeclared function - a
    /// warning on the pinned gcc 7, fatal on anything newer.
    /// </summary>
    [Fact]
    public void EqualityBodyStaysInRealm()
    {
        var (diag, module) = SingleFileCompile.Check("""
            realm kernel { entry func Main() { } }

            realm userspace {
                // Both the payload class and the union naming it live in the realm. A realm-scoped
                // declaration is not visible from an enclosing scope, so a top-level union could
                // not name Key at all - which is the encapsulation working, not a limitation.
                class Key {
                    public int id;
                    func _init() { self.id = 0; }
                    public operator bool func ==(Key other) { return self.id == other.id; }
                }

                union Tagged { Ident(Key k), Nothing }

                foreground process App {
                    thread T {
                        entry func Run() {
                            let Tagged x = Tagged.Ident(new Key());
                            let bool eq = x == Tagged.Nothing();
                        }
                    }
                }
            }
            """);

        Assert.False(diag.HasErrors, string.Join("; ", diag.All.Select(d => d.Message)));
        var o = new Emitter(module!, diag).Build();
        string ty = Mangler.Union("Tagged@userspace");
        string eq = Mangler.UnionEq("Tagged@userspace");
        string body = $"{eq}({ty} _a, {ty} _b) {{";

        Assert.Contains(body, o.UserFuncs);
        Assert.DoesNotContain(body, o.KernelFuncs);
        Assert.Contains(eq, o.SharedHeader);
    }

    /// <summary>
    /// Locates the checkout and compiler, skipping when either is missing.
    /// </summary>
    private static (string?, string?) Env()
    {
        var gata = HostedRun.FindGataCheckout();
        if (gata == null) { Assert.Skip("no sibling Gata checkout found"); return (null, null); }

        var cc = HostedRun.FindCompiler();
        if (cc == null) { Assert.Skip("no host C compiler (cc/gcc/clang) found"); return (null, null); }

        return (gata, cc);
    }
}
