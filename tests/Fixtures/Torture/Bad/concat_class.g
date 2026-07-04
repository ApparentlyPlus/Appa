// EXPECT G004
import LibGata;
class C { }
kernel { entry func Main() { let C c = new C(); Console.PrintLine("x" + c); } }
