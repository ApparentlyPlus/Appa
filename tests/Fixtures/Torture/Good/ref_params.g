// EXPECT OK
// `ref` parameter modifier: compiler-checked alias passing (no `unsafe` needed),
// explicit `ref` required at both the declaration and the call site.
import LibGata;

func Swap[T](ref T a, ref T b) {
    let tmp = a;
    a = b;
    b = tmp;
}

void func Increment(ref int n) { n = n + 1; }

kernel { entry func Main() {
    let int x = 1;
    let int y = 2;
    Swap(ref x, ref y);   // x=2, y=1

    let String s1 = "hi";
    let String s2 = "there";
    Swap(ref s1, ref s2); // managed types alias correctly too

    Increment(ref x);
    Console.PrintLine(Int.ToString(x));
    Console.PrintLine(Int.ToString(y));
    Console.PrintLine(s1);
    Console.PrintLine(s2);
} }
