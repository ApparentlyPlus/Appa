// EXPECT G035
// Fields/methods are private by default now (no `private` keyword needed) — only
// an explicit `public` member is reachable from outside its declaring class.
class Box { int v; func _init(int x) { self.v = x; } }
kernel { entry func Main() {
  let Box b = new Box(1);
  let int z = b.v;          // default-private field accessed from outside
} }
