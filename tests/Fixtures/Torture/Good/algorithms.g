// EXPECT OK
// Algorithms.g: duck-typed generics over `<` — Sort/IsSorted/BinarySearch take a
// `List[T]` parameter, inferring T from the caller's concrete instantiation.
import LibGata;
import Collections;

kernel { entry func Main() {
    let List[int] l = new List[int]();
    l.Add(5); l.Add(3); l.Add(1); l.Add(4); l.Add(2);
    Sort(l);
    Console.PrintLine(Bool.ToString(IsSorted(l)));
    Console.PrintLine(Int.ToString(BinarySearch(l, 4)));

    let x = 10;
    let y = 20;
    Console.PrintLine(Int.ToString(Min(x, y)));
    Console.PrintLine(Int.ToString(Max(x, y)));
    Swap(ref x, ref y);
    Console.PrintLine(Int.ToString(x));
} }
