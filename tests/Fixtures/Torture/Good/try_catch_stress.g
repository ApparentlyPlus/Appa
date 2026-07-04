// EXPECT OK
// Adversarial-but-valid try/catch/throws control flow.
import LibGata;
throws int func risky(int x) { if (x < 0) { throw; } return x; }
int func sink(int a) { return a; }
class Box { int v; func _init(int x) { self.v = x; } int func Get() { return self.v; } }

kernel { entry func Main() {
  // nested try; the inner catch re-throws, routing to the OUTER catch.
  try {
    try { let int a = risky(1); sink(a); }
    catch { throw; }
  } catch { sink(99); }

  // two throwing calls sequenced in one try.
  try {
    let int a = risky(2);
    let int b = risky(3);
    sink(a + b);
  } catch { }

  // a managed local live at the point of an early return inside try (ARC must
  // release it on the way out).
  try { let Box b = new Box(7); return; } catch { }
} }

user {
  foreground process App {
    thread T {
      entry func Run() {
        // break out of a loop from inside a catch that sits in the loop body.
        let int i = 0;
        while (i < 5) {
          try { let int x = risky(i); sink(x); } catch { }
          i = i + 1;
          if (i == 3) { break; }
        }
      }
    }
  }
}
