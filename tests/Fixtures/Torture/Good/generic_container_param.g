// EXPECT OK
// A generic free function can take a `List[T]`-shaped parameter and infer T from the
// caller's concrete `List[int]`/`List[String]` argument (UnifyParam/SubType handle the
// `Base_T` mangled-spelling pattern, not just bare `T`/`T*`).
import LibGata;
import Collections;

int func CountOf[T](List[T] list) { return list.Length(); }

kernel { entry func Main() {
    let List[int] li = new List[int]();
    li.Add(1);
    li.Add(2);
    let List[String] ls = new List[String]();
    ls.Add("a");
    Console.PrintLine(Int.ToString(CountOf(li)));
    Console.PrintLine(Int.ToString(CountOf(ls)));
} }
