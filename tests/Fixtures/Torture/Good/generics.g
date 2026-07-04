// EXPECT OK
import LibGata; import Collections;
kernel { entry func Main() {
  let List[int] xs = new List[int]();
  xs.Add(1); xs.Add(2);
  for x in xs { Console.PrintLine(Int.ToString(x)); }
} }
