// EXPECT OK
// `for..in` is a structural protocol: any class with a zero-arg `Length() -> int` and a
// single-int-arg `Get(int) -> T` is iterable, not just the compiler's built-in `List`.
import LibGata;

class Bag {
    public int n;
    public int func Length() { return self.n; }
    public int func Get(int i) { return i * 10; }
}

kernel { entry func Main() {
    let Bag b = new Bag();
    b.n = 3;
    let int sum = 0;
    for x in b { sum = sum + x; }
    Console.PrintLine(Int.ToString(sum));
} }
