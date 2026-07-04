// EXPECT G004
// Ternary arms of unrelated types (int vs a class) cannot be unified.
class Box { int v; func _init(int x) { self.v = x; } }
kernel { entry func Main() {
  let int x = true ? 1 : new Box(2);
} }
