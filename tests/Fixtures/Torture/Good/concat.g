// EXPECT OK
import LibGata;
kernel { entry func Main() {
  let int n = 42;
  Console.PrintLine("n=" + Int.ToString(n));
  let String s = "a" + "b";
} }
