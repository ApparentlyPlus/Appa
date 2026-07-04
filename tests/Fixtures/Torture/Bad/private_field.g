// EXPECT G035
class Box { private int v; func _init(int x) { self.v = x; } }
kernel { entry func Main() {
  let Box b = new Box(1);
  let int z = b.v;          // private field accessed from outside
} }
