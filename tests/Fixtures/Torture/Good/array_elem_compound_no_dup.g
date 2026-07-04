// EXPECT OK
// Same bug, legacy path: `arr[i] op= v` for a fixed array of a class with a custom
// element operator desugars to `arr[i] = elemOp(arr[i], v)` — `arr[i]` (i.e. the
// `obj`/`idx` pair) appears on both sides, so a side-effecting index expression
// ran twice there too. Runtime-verified: `cc.Next()` call count must be 1.
import LibGata;
class IntBox {
    int v;
    func _init(int x) { self.v = x; }
    operator func +(int n) -> IntBox { return new IntBox(self.v + n); }
}
class CallCounter {
    public int n;
    func _init() { self.n = 0; }
    public int func Next() { self.n = self.n + 1; return self.n - 1; }
}
kernel { entry func Main() {
    let [3]IntBox arr = [new IntBox(1), new IntBox(2), new IntBox(3)];
    let CallCounter cc = new CallCounter();
    unsafe {
        arr[cc.Next()] += 10;
    }
    Console.PrintLine(Int.ToString(cc.n));
} }
