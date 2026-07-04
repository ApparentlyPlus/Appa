// EXPECT OK
// `List[T]` declares the real `operator []`/`operator []=` (thin wrappers over
// Get/Set) — the compiler's old hardcoded `"List_"` indexing hack is gone; this is
// the end-to-end proof the real mechanism replaced it correctly.
import LibGata;
import Collections;

kernel { entry func Main() {
    let List[int] l = new List[int]();
    l.Add(10);
    l.Add(20);
    l.Add(30);
    l[1] = 99;
    l[0] += 5;
    Console.PrintLine(Int.ToString(l[0] + l[1] + l[2]));
} }
