// EXPECT OK
// The rest of OperatorSymbol's grammar (`- * / == != & | ^ << >>`) — only `+ < >
// <= >= [] []=` had any coverage before this (libgata's String/List/Map). Runtime-
// verified externally (not just transpile-checked here): `a == c` compares two
// *distinct* Box objects with equal value, which only comes out true if `==`
// genuinely dispatches to the overload rather than falling back to pointer
// identity. Expected: total == 146, eqVal == true, ne == true.
import LibGata;
class Box {
    int v;
    func _init(int x) { self.v = x; }
    public int func Val() { return self.v; }
    operator func -(Box o) -> Box { return new Box(self.v - o.v); }
    operator func *(Box o) -> Box { return new Box(self.v * o.v); }
    operator func /(Box o) -> Box { return new Box(self.v / o.v); }
    operator func ==(Box o) -> bool { return self.v == o.v; }
    operator func !=(Box o) -> bool { return self.v != o.v; }
    operator func &(Box o) -> Box { return new Box(self.v & o.v); }
    operator func |(Box o) -> Box { return new Box(self.v | o.v); }
    operator func ^(Box o) -> Box { return new Box(self.v ^ o.v); }
    operator func <<(int n) -> Box { return new Box(self.v << n); }
    operator func >>(int n) -> Box { return new Box(self.v >> n); }
}
kernel { entry func Main() {
    let Box a = new Box(12);
    let Box b = new Box(5);
    let Box c = new Box(12);   // distinct object, same value as a
    let Box d = a - b;
    let Box m = a * b;
    let Box q = a / b;
    let bool eqVal = (a == c);
    let bool ne = (a != b);
    let Box bAnd = a & b;
    let Box bOr  = a | b;
    let Box bXor = a ^ b;
    let Box shl = a << 2;
    let Box shr = a >> 2;
    let int total = d.Val() + m.Val() + q.Val() + bAnd.Val() + bOr.Val() + bXor.Val() + shl.Val() + shr.Val();
    Console.PrintLine(Int.ToString(total));
    Console.PrintLine(Bool.ToString(eqVal));
    Console.PrintLine(Bool.ToString(ne));
} }
