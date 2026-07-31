import Console;
import List;

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

class Census {
    public int live;
    public int created;
    func _init() { self.live = 0; self.created = 0; }
}

class Tracked {
    Census census;
    public int id;
    public func _init(Census c, int id) {
        self.census = c;
        self.id = id;
        c.live = c.live + 1;
        c.created = c.created + 1;
    }
    func _deinit() { self.census.live = self.census.live - 1; }

    public operator bool func ==(Tracked o) { return self.id == o.id; }
}

union Signal {
    Quiet,
    One(Tracked t),
    Both(Tracked a, Tracked b),
    Level(int n)
}

union Envelope { Wrap(Signal s), Sealed(int code) }

class Mailbox {
    Signal slot;
    public func _init(Signal s) { self.slot = s; }
    public void func Put(Signal s) { self.slot = s; }
    public int func Weight() { return SignalWeight(self.slot); }
}

union Maybe[V] { Found(V v), Missing }

union Tree[V] { Leaf(V v), Fork(List[Tree[V]] kids) }

int func CountLeaves[V](Tree[V] t) {
    match (t) {
        case Leaf(v) { return 1; }
        case Fork(kids) {
            let int n = 0;
            for k in kids { n = n + CountLeaves(k); }
            return n;
        }
    }
}

Signal func MakeSignal(Census c, int id) { return Signal.One(new Tracked(c, id)); }

int func EnvelopeWeight(Envelope e) {
    match (e) {
        case Wrap(s) { return SignalWeight(s); }
        case Sealed(code) { return code; }
    }
}

int func SignalWeight(Signal s) {
    match (s) {
        case Quiet { return 0; }
        case One(t) { return t.id; }
        case Both(a, b) { return a.id + b.id; }
        case Level(n) { return n; }
    }
}

private int func Scale(int n) { return n * 2; }

int func LibScale(int n) { return Scale(n); }

void func Bump(ref int slot) { slot = slot + 1; }

throws T func Unwrap[T](T value, bool fail) {
    if (fail) { throw; }
    return value;
}

T func Relay[T](T value) { return Passthrough(value); }

private T func Passthrough[T](T value) { return value; }
