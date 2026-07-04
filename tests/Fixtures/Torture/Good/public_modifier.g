// EXPECT OK
// `public` is the explicit opt-in that makes a member reachable from outside its
// declaring class/module (members are private by default — see
// tests/torture/bad/default_private_field.g / default_private_method.g for the
// negative case this is the mirror image of).
import LibGata;
class Box {
    public int v;
    func _init(int x) { self.v = x; }
    public int func Get() { return self.v; }
}
module M {
    public int func helper() { return 1; }
}
kernel { entry func Main() {
    let Box b = new Box(5);
    let int a = b.v;
    let int c = b.Get();
    let int d = M.helper();
    if (a + c + d == 11) { Console.PrintLine("ok"); } else { Console.PrintLine("bad"); }
} }
