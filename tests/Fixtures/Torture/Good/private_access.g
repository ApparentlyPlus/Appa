// EXPECT OK
// Members are private by default — only `public` members are reachable from outside
// their declaring class/module. `private` is still accepted explicitly (redundant
// with the default, but symmetric with `public`). A file-local `private` free
// function is a separate mechanism (file scope, not class scope) and is unaffected.
import LibGata;
class Counter {
  private int n;                              // explicit, same as the default
  func _init() { self.n = 0; }
  public void func Bump() { self.n = self.n + 1; }   // self access to private field — ok
  public int func Value() { return self.n; }
}
module M {
  int func secret() { return 7; }              // private by default (no modifier)
  public int func Public() { return secret(); }      // sibling call to private — ok
}
private int func localHelper(int x) { return x * 2; }

kernel { entry func Main() {
  let Counter c = new Counter();
  c.Bump();
  let int v = c.Value() + M.Public() + localHelper(3);
  if (v == 1 + 7 + 6) { } else { }
} }
