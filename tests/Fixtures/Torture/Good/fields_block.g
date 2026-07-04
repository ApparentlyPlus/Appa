// EXPECT OK
// Raw C struct fields via a `fields { }` block, mixed with a typed Gata field and
// ordinary methods. This is the one libgata stopped using internally (collections
// moved to typed `T* data` fields instead) but the grammar still supports it and it
// must keep working.
import LibGata;

class Box {
  public int tag;
  fields { int raw; }

  public void func SetRaw(int v) native { #kernel: self->raw = v; #user: self->raw = v; }
  public int func GetRaw() native { #kernel: return self->raw; #user: return self->raw; }
}

kernel { entry func Main() {
  let Box b = new Box();
  b.tag = 7;
  b.SetRaw(42);
  let int r = b.GetRaw();
  if (r + b.tag == 49) { Console.PrintLine("ok"); } else { Console.PrintLine("bad"); }
} }
