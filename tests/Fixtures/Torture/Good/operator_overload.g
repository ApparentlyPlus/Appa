// EXPECT OK
// User-defined operator overloading (the same mechanism String's `+` now uses).
import LibGata;
class Vec {
  int x;
  int y;
  func _init(int a, int b) { self.x = a; self.y = b; }
  operator func +(Vec other) -> Vec { return new Vec(self.x + other.x, self.y + other.y); }
  public int func Sum() { return self.x + self.y; }
}
kernel { entry func Main() {
  let Vec a = new Vec(1, 2);
  let Vec b = new Vec(3, 4);
  let Vec c = a + b;                 // dispatches to Vec's `+` operator
  if (c.Sum() == 10) { } else { }
} }
