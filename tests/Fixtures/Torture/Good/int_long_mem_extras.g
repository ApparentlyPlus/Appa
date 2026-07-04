// EXPECT OK
// Small additive touches: Int/Long.ParseStrict (throws sibling to the lenient Parse),
// Long.Parse, Mem.Fill/Mem.Compare.
import LibGata;
import Collections;

kernel { entry func Main() {
    try { let int ok = Int.ParseStrict("42"); Console.PrintLine(Int.ToString(ok)); } catch { }
    try { let int bad = Int.ParseStrict("nope"); } catch { Console.PrintLine("rejected"); }
    Console.PrintLine(Long.ToString(Long.Parse("123456789012")));
    try { let int64 ok2 = Long.ParseStrict("99"); Console.PrintLine(Long.ToString(ok2)); } catch { }

    let int a = 5;
    let int b = 5;
    unsafe {
        Console.PrintLine(Int.ToString(Mem.Compare(&a, &b, sizeof(int) as usize)));
        Mem.Fill(&a, 0 as byte, sizeof(int) as usize);
    }
    Console.PrintLine(Int.ToString(a));
} }
