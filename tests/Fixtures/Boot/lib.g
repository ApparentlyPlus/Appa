// A second translation unit, so the boot regression covers the multi-file front end on the
// real target: cross-file imports, cross-file types, and a generic declared here but
// instantiated over a class declared in main.g.
import Console;

class Payload {
    public int weight;
    func _init() { self.weight = 1; }
    public void func Add(int by) { self.weight = self.weight + by; }
}

class Crate[T] {
    public T item;
}

enum Grade { Low, High = 5 }

union Reading {
    Empty,
    Point(int x, int y),
    Bytes([4]int raw)
}

// A private free function: two files declaring this name must not collide in C.
private int func Scale(int n) { return n * 2; }

public int func LibScale(int n) { return Scale(n); }

// 'ref' across a file boundary.
public void func Bump(ref int slot) { slot = slot + 1; }

// A generic throws function, whose Result typedef is only known once instantiated.
public throws T func Unwrap[T](T value, bool fail) {
    if (fail) { throw; }
    return value;
}
