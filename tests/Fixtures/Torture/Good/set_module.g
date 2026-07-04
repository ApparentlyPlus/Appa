// EXPECT OK
// Set[T]/StringSet: hash sets sharing Map/StringMap's hashing engine (Mix/HashString
// extracted as free functions specifically so a static method nested in a generic
// class — which gets re-mangled per instantiation — doesn't need a non-existent
// "StringMap.Hash" cross-class reference).
import LibGata;
import Collections;

kernel { entry func Main() {
    let Set[int] s1 = new Set[int]();
    s1.Add(1); s1.Add(2); s1.Add(2);
    Console.PrintLine(Int.ToString(s1.Length()));
    let Set[int] s2 = new Set[int]();
    s2.Add(2); s2.Add(3);
    Console.PrintLine(Int.ToString(s1.Union(s2).Length()));
    Console.PrintLine(Int.ToString(s1.Intersect(s2).Length()));

    let StringSet ss = new StringSet();
    ss.Add("a"); ss.Add("a");
    Console.PrintLine(Int.ToString(ss.Length()));
} }
