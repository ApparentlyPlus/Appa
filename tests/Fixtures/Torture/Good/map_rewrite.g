// EXPECT OK
// Map[K,V]/StringMap[V] redesign: SplitMix64-mixed + power-of-2-masked hashing,
// Clear/Capacity/Reserve/GetOrThrow/Keys/Values/operator[]/[]=, StringMap brought to
// full parity with Map (Remove/Clear, which it previously lacked entirely).
// Map.Keys()/Values() returning List[K]/List[V] is also the regression case for a
// real compiler bug this surfaced: a generic class returning another generic
// parameterized by its own type param (Map[K,V] -> List[K]) used to register a bogus
// literal "List_K" instantiation request at parse time.
import LibGata;
import Collections;

kernel { entry func Main() {
    let Map[int, int] m = new Map[int, int]();
    m[1] = 100;
    m[2] = 200;
    m.Remove(1);
    Console.PrintLine(Bool.ToString(m.Has(1)));
    Console.PrintLine(Int.ToString(m[2]));

    let keys = m.Keys();
    let values = m.Values();
    Console.PrintLine(Int.ToString(keys.Length()));
    Console.PrintLine(Int.ToString(values.Length()));

    m.Clear();
    Console.PrintLine(Bool.ToString(m.IsEmpty()));

    let StringMap[int] sm = new StringMap[int]();
    sm.Put("a", 1);
    sm.Remove("a");
    Console.PrintLine(Bool.ToString(sm.Has("a")));
} }
