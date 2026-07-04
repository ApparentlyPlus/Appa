// EXPECT G000
// Raw C fields in a class go in a `fields { }` block; a bare `native { }` is not a member.
class Box {
  int v;
  native { int extra; }
}
kernel { entry func Main() { } }
