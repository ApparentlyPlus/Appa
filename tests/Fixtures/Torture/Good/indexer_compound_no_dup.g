// EXPECT OK
// `x[i] op= v` against a class with `operator []`/`operator []=` used to reuse the
// SAME resolved `obj`/`idx` IrExpr nodes in both the getter call and the setter
// call — the emitter just reprints each node where it's referenced, so a
// side-effecting index expression (a call) ran TWICE instead of once. Fixed by
// hoisting `obj`/`idx` into temps before either call when they aren't already a
// bare variable/self/literal. Runtime-verified externally (not just transpile-
// checked here): `cc.Next()` must be called exactly once, so the printed call
// count must be 1, not 2.
import LibGata;
class IntBox {
    int v;
    func _init(int x) { self.v = x; }
    operator func [](int i) -> int { return self.v + i; }
    operator func []=(int i, int val) { self.v = val - i; }
}
class CallCounter {
    public int n;
    func _init() { self.n = 0; }
    public int func Next() { self.n = self.n + 1; return self.n - 1; }
}
kernel { entry func Main() {
    let IntBox b = new IntBox(10);
    let CallCounter cc = new CallCounter();
    b[cc.Next()] += 5;
    Console.PrintLine(Int.ToString(cc.n));
} }
