// EXPECT OK
// `defer` runs on every exit from its enclosing block, in LIFO order with other
// defers in that block: normal fallthrough, an early return, a loop `break`, and
// the throw path out of a `throws` function.
import LibGata;

void func WithDefer(int mode) {
    Console.PrintLine("enter");
    defer Console.PrintLine("first-deferred");
    defer Console.PrintLine("second-deferred");   // runs before "first-deferred"
    if (mode == 0) { Console.PrintLine("normal-path"); return; }
    while (true) {
        defer Console.PrintLine("loop-deferred");
        if (mode == 1) { break; }
        return;
    }
    Console.PrintLine("after-loop");
}

throws int func RiskyDefer(int x) {
    defer Console.PrintLine("cleanup");
    if (x < 0) { throw; }
    return x;
}

kernel { entry func Main() {
    WithDefer(0);
    WithDefer(1);
    try {
        let int a = RiskyDefer(-1);
    } catch {
        Console.PrintLine("caught");
    }
} }
