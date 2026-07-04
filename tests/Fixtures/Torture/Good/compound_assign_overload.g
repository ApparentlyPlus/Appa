// EXPECT OK
// Compound assignment (`a &= b`) composing through a *user-defined* operator
// overload, not just a primitive — the grammar note says "`x OP= y` composes from
// the matching binary operator when the LHS class declares one," but nothing
// exercised that for a custom class before. Runtime-verified externally (not just
// transpile-checked here): expected output is 16 (12 & 5 = 4, then 4 << 2 = 16).
import LibGata;
class Box {
    int v;
    func _init(int x) { self.v = x; }
    public int func Val() { return self.v; }
    operator func &(Box o) -> Box { return new Box(self.v & o.v); }
    operator func <<(int n) -> Box { return new Box(self.v << n); }
}
kernel { entry func Main() {
    let Box a = new Box(12);
    let Box b = new Box(5);
    a &= b;
    a <<= 2;
    Console.PrintLine(Int.ToString(a.Val()));
} }
