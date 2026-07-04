// EXPECT OK
// PriorityQueue[T]: binary min-heap ordered by `<` — duck-typed (works for numerics,
// String, or any class with `operator <`); a non-comparable T fails to compile, by design.
import LibGata;
import Collections;

kernel { entry func Main() {
    let PriorityQueue[int] pq = new PriorityQueue[int]();
    pq.Push(5); pq.Push(1); pq.Push(3);
    Console.PrintLine(Int.ToString(pq.Length()));
    Console.PrintLine(Int.ToString(pq.Peek()));
    Console.PrintLine(Int.ToString(pq.Pop()));
    Console.PrintLine(Int.ToString(pq.Pop()));
    Console.PrintLine(Int.ToString(pq.Pop()));
    Console.PrintLine(Bool.ToString(pq.IsEmpty()));
} }
