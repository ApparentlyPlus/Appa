// EXPECT OK
// Function-pointer types: a callback local assigned from a bare function name,
// passing a function as a callback argument, indirect calls, and an array of
// function pointers (vtable-style dispatch).
import LibGata;

int func AddOne(int x) { return x + 1; }
int func Double(int x) { return x * 2; }

int func ApplyTwice(func(int) -> int f, int x) {
    return f(f(x));
}

kernel { entry func Main() {
    let func(int) -> int cb = AddOne;
    let int a = cb(5);                  // 6
    let int b = ApplyTwice(Double, 3);  // 12

    let [2]func(int) -> int ops = [AddOne, Double];
    let int c = ops[0](10);             // 11
    let int d = ops[1](10);             // 20

    if (a + b + c + d == 6 + 12 + 11 + 20) { Console.PrintLine("ok"); } else { Console.PrintLine("bad"); }
} }
