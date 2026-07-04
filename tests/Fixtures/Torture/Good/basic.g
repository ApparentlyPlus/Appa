// EXPECT OK
import LibGata;
kernel { entry func Main() {
  let int s = 0;
  for (let int i = 0; i < 10; i++) { s += i; }
  Console.PrintLine("sum=" + Int.ToString(s));
} }
